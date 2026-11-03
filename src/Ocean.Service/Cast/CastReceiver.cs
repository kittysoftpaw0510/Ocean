using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Cast;

/// <summary>
/// The multicast I'm watching (viewer side): joins the host's session, listens for its packets and writes them into
/// the stream cache, so the player streams the video the usual way and only fetches from the host what the multicast
/// didn't bring (nothing arrives at all on some Wi-Fi, through VPN adapters or across bridges; then it all comes
/// directly). Reports to the host every few seconds and keeps the app posted on where the host is.
/// </summary>
public sealed class CastReceiver : IDisposable
{
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlStale = TimeSpan.FromSeconds(3);

    private sealed class View
    {
        public string PeerId = "", SessionId = "", HostName = "";
        public Guid Id;
        public byte[] Key = Array.Empty<byte>();
        public ItemDto Item = new();
        public StreamCache.ExternalFeed? Feed;
        public Socket? Socket;
        public IPAddress Group = IPAddress.None;
        /// <summary>The local adapter addresses the group is joined on (dropped and joined again after a network change).</summary>
        public readonly List<IPAddress> Joined = new();
        public readonly object JoinLock = new();
        public DateTime LastListen;
        public Thread? Thread;
        public readonly CancellationTokenSource Cts = new();
        public CastStateDto State = new();
        public long StateAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public DateTime LastControl;
        public long BytesMulticast;
        public int Lost, Recovered;
        /// <summary>Sealed payloads of recent data packets (for parity recovery) and the ids of recent parity packets (empty).</summary>
        public readonly Dictionary<uint, byte[]> Recent = new();
        /// <summary>Parity packets waiting until their group has had time to arrive (copies from several adapters come in any order).</summary>
        public readonly SortedDictionary<uint, byte[]> PendingParity = new();
        public bool Ended;
    }

    private readonly PeerService _peers;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly StreamCache _cache;
    private readonly EventHub _events;
    private readonly ILogger<CastReceiver> _log;
    private readonly object _lock = new();
    private View? _v;
    private DateTime _lastPublished;

    public CastReceiver(PeerService peers, PeerClient client, PeerRegistry registry, StreamCache cache, EventHub events, ILogger<CastReceiver> log)
    {
        _peers = peers; _client = client; _registry = registry; _cache = cache; _events = events; _log = log;
        // Wi-Fi reconnecting, a new address, a VPN switched on or off: the group membership may be gone with the old
        // adapter state, and nothing would arrive by multicast again for the rest of the session.
        Net.LocalNetworks.Changed += () =>
        {
            View? v;
            lock (_lock) v = _v;
            if (v is { Ended: false, Socket: not null }) Listen(v, "the network changed");
        };
    }

    public async Task<CastViewDto> JoinAsync(string peerId, string sessionId, CancellationToken ct)
    {
        await LeaveAsync();
        if (!_registry.TryGetOnline(peerId, out var peer)) throw new PeerUnavailableException("That PC is offline.");
        var req = _client.Build(peer, HttpMethod.Post, "/p/cast/" + Uri.EscapeDataString(sessionId) + "/join", _peers.GrantsFor(peerId));
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("That multicast has ended.");
        if (resp.StatusCode == HttpStatusCode.BadRequest && peer.Bridged)
            throw new InvalidOperationException($"This PC reaches {DisplayNames.Of(peer.User, peer.Name)} only through other PCs, and none of them can pass a multicast on yet (they run an older Ocean). It works once one of them is updated to 1.2.0 or later.");
        if (!resp.IsSuccessStatusCode) throw await PeerStatusException.FromAsync(resp);
        var join = await resp.Content.ReadFromJsonAsync<CastJoinDto>(Json.Options, ct) ?? throw new InvalidOperationException("Empty answer from the host.");

        var v = new View
        {
            PeerId = peerId, SessionId = sessionId, HostName = DisplayNames.Of(peer.User, peer.Name), Id = Guid.ParseExact(sessionId, "N"),
            Key = Convert.FromBase64String(join.Key), State = join.State,
            Item = new ItemDto { Id = join.ItemId, Name = join.Name, ShareId = join.ShareId, Size = join.Size, ModifiedAt = join.ModifiedAt, DurationMs = join.DurationMs, Unlocked = true },
        };
        if (LightVariant.Split(join.ItemId, out _)) { v.Item.Light = LightVariant.Ready; v.Item.LightSize = join.Size; }
        // the player streams this item id from the host; the cache holds what the multicast brings
        _peers.RegisterCastItem(peerId, v.Item);
        v.Feed = _cache.OpenFeed(peerId, v.Item);
        if (v.Feed is null) _log.LogWarning("multicast: the stream cache is off or full; everything will come directly from {Host}", v.HostName);
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, join.Port));
            sock.ReceiveTimeout = 1000;
            sock.ReceiveBufferSize = 4 * 1024 * 1024;
            v.Group = IPAddress.Parse(join.Group);
            v.Socket = sock;
            Listen(v, null);
        }
        catch (Exception ex) { _log.LogWarning("multicast: can't listen ({Msg}); everything comes directly from {Host}", ex.Message, v.HostName); }
        lock (_lock) _v = v;
        v.Thread = new Thread(() => ReceiveLoop(v), 512 * 1024) { IsBackground = true, Name = "ocean-cast-rx" };
        v.Thread.Start();
        _ = Task.Run(() => BeatLoopAsync(v));
        _log.LogInformation("multicast: joined {Name} from {Host}", join.Name, v.HostName);
        Publish(v, force: true);
        return Status()!;
    }

    /// <summary>
    /// Joins the group on the real network adapters the host is reached through (never a VPN or virtual adapter),
    /// dropping the earlier memberships first. <paramref name="again"/>: why it is done again (logged), null the first time.
    /// </summary>
    private void Listen(View v, string? again)
    {
        if (v.Socket is not { } sock) return;
        lock (v.JoinLock)
        {
            v.LastListen = DateTime.UtcNow;
            var before = v.Joined.ToList();
            foreach (var addr in before)
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(v.Group, addr)); } catch { }
            v.Joined.Clear();
            var hostAddrs = _registry.TryGet(v.PeerId, out var peer)
                ? (peer.Preferred is { } pref ? new[] { pref } : Array.Empty<IPAddress>()).Concat(peer.Addresses.Keys).Append(peer.Address)
                : Array.Empty<IPAddress>();
            var adapters = Net.LocalNetworks.MulticastAddresses(hostAddrs);
            foreach (var (addr, adapter) in adapters)
            {
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(v.Group, addr)); v.Joined.Add(addr); }
                catch (Exception ex) { _log.LogDebug("multicast: can't listen on {Adapter} ({Addr}): {Msg}", adapter, addr, ex.Message); }
            }
            var where = v.Joined.Count == 0 ? "no adapter" : string.Join(", ", adapters.Where(a => v.Joined.Contains(a.Address)).Select(a => $"{a.Adapter} ({a.Address})"));
            if (again is null) _log.LogInformation("multicast: listening on {Adapters}", where);
            else if (!before.SequenceEqual(v.Joined)) _log.LogInformation("multicast: listening again on {Adapters} ({Why})", where, again);
            else _log.LogDebug("multicast: joined the group again on {Adapters} ({Why})", where, again);
            if (v.Joined.Count == 0) _log.LogWarning("multicast: no network adapter to listen on; everything comes directly from {Host}", v.HostName);
        }
    }

    public async Task LeaveAsync()
    {
        View? v;
        lock (_lock) { v = _v; _v = null; }
        if (v is null) return;
        v.Cts.Cancel();
        try { v.Socket?.Close(); } catch { }
        v.Feed?.Dispose();
        _peers.UnregisterCastItem(v.PeerId, v.Item.Id);
        if (!v.Ended)
        {
            try
            {
                if (_registry.TryGetOnline(v.PeerId, out var peer))
                {
                    var req = _client.Build(peer, HttpMethod.Post, "/p/cast/" + v.SessionId + "/leave", _peers.GrantsFor(v.PeerId));
                    using var cts = new CancellationTokenSource(3000);
                    using var _ = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
            }
            catch (Exception ex) { _log.LogDebug("multicast: leave: {Msg}", ex.Message); }
        }
        _log.LogInformation("multicast: left {Name} ({Multicast} by multicast, {Lost} lost, {Recovered} recovered)", v.Item.Name, Format.Bytes(v.BytesMulticast), v.Lost, v.Recovered);
        _events.Publish("castView", new CastViewDto());
    }

    public CastViewDto? Status()
    {
        View? v;
        lock (_lock) v = _v;
        return v is null ? null : ToDto(v);
    }

    private CastViewDto ToDto(View v) => new()
    {
        Active = !v.Ended, PeerId = v.PeerId, SessionId = v.SessionId, ItemId = v.Item.Id, ItemName = v.Item.Name, HostName = v.HostName,
        Position = v.State.Position, Playing = v.State.Playing, HostTimeMs = v.State.HostTimeMs, ReceivedAtMs = v.StateAtMs, Viewers = v.State.Viewers, Ended = v.Ended,
        BytesMulticast = v.BytesMulticast, Lost = v.Lost, Recovered = v.Recovered,
        MulticastWorking = DateTime.UtcNow - v.LastControl < ControlStale,
    };

    private void Publish(View v, bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastPublished < TimeSpan.FromMilliseconds(400)) return;
        _lastPublished = DateTime.UtcNow;
        _events.Publish("castView", ToDto(v));
    }

    // ------------------------------------------------------------------ packets

    private void ReceiveLoop(View v)
    {
        if (v.Socket is null) return;
        using var aes = new AesGcm(v.Key, 16);
        var packet = new byte[2048];
        var plain = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!v.Cts.IsCancellationRequested)
        {
            int n;
            try { n = v.Socket.ReceiveFrom(packet, ref from); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
            catch { break; }
            try
            {
                if (from is IPEndPoint src && !Net.LocalNetworks.IsLan(src.Address)) continue; // local network only
                if (!CastWire.TryParse(packet.AsSpan(0, n), out var session, out var type, out var seq, out var payload) || session != v.Id) continue;
                if (v.Recent.ContainsKey(seq)) continue; // the same packet again (the host sends on every adapter)
                if (type == CastWire.TypeParity)
                {
                    v.Recent[seq] = Array.Empty<byte>();
                    v.PendingParity[seq] = payload.ToArray();
                    continue;
                }
                if (!CastWire.Open(aes, type, seq, payload, plain, out var len)) continue; // not ours, or tampered with
                if (type == CastWire.TypeData)
                {
                    if (!CastWire.ReadData(plain.AsSpan(0, len), out var offset, out var bytes)) continue;
                    v.Feed?.Write(offset, bytes);
                    v.BytesMulticast += bytes.Length;
                    v.Recent[seq] = payload.ToArray();
                    // a group whose parity is 32 packets behind has had its time: recover one missing packet, count the rest
                    while (v.PendingParity.Count > 0 && v.PendingParity.First().Key + 32 < seq)
                    {
                        var (pseq, parity) = v.PendingParity.First();
                        v.PendingParity.Remove(pseq);
                        var missing = CastWire.Missing(parity, s => v.Recent.ContainsKey(s));
                        if (missing == 1)
                        {
                            var recovered = CastWire.Recover(parity, s => v.Recent.TryGetValue(s, out var d) && d.Length > 0 ? d : null);
                            if (recovered is { } r && CastWire.Open(aes, CastWire.TypeData, r.Seq, r.Sealed, plain, out var rlen) && CastWire.ReadData(plain.AsSpan(0, rlen), out var roff, out var rbytes))
                            {
                                v.Feed?.Write(roff, rbytes);
                                v.Recent[r.Seq] = r.Sealed;
                                v.BytesMulticast += rbytes.Length; v.Recovered++;
                                missing = 0;
                            }
                        }
                        v.Lost += missing; // what the direct fetch will have to bring
                    }
                    Trim(v, seq);
                }
                else if (type == CastWire.TypeControl && CastWire.ReadControl(plain.AsSpan(0, len), out var pos, out var playing, out var hostMs, out var viewers, out var ended))
                {
                    v.LastControl = DateTime.UtcNow;
                    v.State = new CastStateDto { SessionId = v.SessionId, Active = !ended, Position = pos, Playing = playing, HostTimeMs = hostMs, Viewers = viewers, Ended = ended };
                    v.StateAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (ended) { EndedByHost(v); return; }
                    Publish(v);
                }
            }
            catch (Exception ex) { _log.LogDebug("multicast packet: {Msg}", ex.Message); }
        }
    }

    private static void Trim(View v, uint seq)
    {
        if (v.Recent.Count <= 512) return;
        foreach (var old in v.Recent.Keys.Where(k => k + 256 < seq).ToList()) v.Recent.Remove(old);
    }

    private async Task BeatLoopAsync(View v)
    {
        var ct = v.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(BeatEvery, ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (!_registry.TryGetOnline(v.PeerId, out var peer)) continue;
                var req = _client.Build(peer, HttpMethod.Post, "/p/cast/" + v.SessionId + "/beat", _peers.GrantsFor(v.PeerId));
                req.Content = JsonContent.Create(new CastBeatDto { BytesMulticast = v.BytesMulticast, Lost = v.Lost }, options: Json.Options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(8000);
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (resp.StatusCode == HttpStatusCode.NotFound) { EndedByHost(v); return; }
                if (!resp.IsSuccessStatusCode) continue;
                var st = await resp.Content.ReadFromJsonAsync<CastStateDto>(Json.Options, cts.Token);
                if (st is null) continue;
                if (st.Ended) { EndedByHost(v); return; }
                // The host answers but no packet has come for a while: join the group again (at most once a minute;
                // where multicast never arrives, as on some Wi-Fi, this changes nothing and costs nothing).
                if (DateTime.UtcNow - v.LastControl > TimeSpan.FromSeconds(20) && DateTime.UtcNow - v.LastListen > TimeSpan.FromMinutes(1))
                    Listen(v, "no multicast packets for a while");
                // no packets arrive here: the host's answer is the only word on where it is
                if (DateTime.UtcNow - v.LastControl > ControlStale) { v.State = st; v.StateAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Publish(v, force: true); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug("multicast beat: {Msg}", ex.Message); }
        }
    }

    private void EndedByHost(View v)
    {
        if (v.Ended) return;
        v.Ended = true;
        _log.LogInformation("multicast: {Host} ended {Name}", v.HostName, v.Item.Name);
        _events.Publish("castView", ToDto(v));
        _ = LeaveAsync(); // the cached bytes stay: the player can go on as a normal stream
    }

    public void Dispose() => LeaveAsync().GetAwaiter().GetResult();
}
