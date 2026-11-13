using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Cast;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Screen;

/// <summary>
/// The screen I'm watching (viewer side): joins the host's session and puts its stream together, in order, for the
/// app's decoder (<c>/api/screen/view/stream</c>). Two sources fill the same stream:
/// - UDP packets (to the multicast group, or straight to this PC), with one lost packet per group rebuilt from parity;
/// - the direct connection to the host: always for the start (from the latest key picture), then only when
///   something is missing for a moment (a hole after 120 ms, a missing end after 300 ms) or no packets arrive at
///   all. It is dropped again as soon as the packets keep up, so the delay stays at its lowest and nothing is ever
///   missing in the picture.
/// One screen at a time.
/// The sound, when the host shares it, comes as its own packets (each with the frame before it) and goes to the
/// app as it arrives (<c>/api/screen/view/sound</c>), where it is put in order and played on time; frames still
/// missing then are concealed, not waited for. Without packets it comes over the direct connection.
/// </summary>
public sealed class ScreenViewer : IDisposable
{
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(5);
    /// <summary>For tests: this share (percent) of the data and parity packets is thrown away on arrival (OCEAN_TEST_SCREEN_DROP).</summary>
    private static readonly int TestDrop = int.TryParse(Environment.GetEnvironmentVariable("OCEAN_TEST_SCREEN_DROP"), out var d) ? Math.Clamp(d, 0, 100) : 0;

    private sealed class View
    {
        public string PeerId = "", Sid = "", HostName = "";
        public Guid Id;
        public byte[] Key = Array.Empty<byte>();
        public ScreenJoinDto Join = new();
        public Socket? Uni, Multi;
        public IPAddress Group = IPAddress.None;
        public readonly List<IPAddress> Joined = new();
        public readonly object JoinLock = new();
        public DateTime LastListen;
        public readonly CancellationTokenSource Cts = new();
        // the stream being put together (all under Gate)
        public readonly object Gate = new();
        public long Next = -1;                       // the next byte the app gets (-1: not started)
        public readonly SortedDictionary<long, byte[]> Ahead = new();
        public long AheadBytes;
        public DateTime HoleSince, LastProgress = DateTime.UtcNow;
        public Channel<byte[]>? Out;
        public int Pump;
        // packets
        public long Head;
        public DateTime LastControl, LastUdpData, LastLoss;
        public readonly Dictionary<uint, byte[]> Recent = new();
        public readonly SortedDictionary<uint, byte[]> PendingParity = new();
        public long BytesUdp, BytesDirect;
        public int Lost, Recovered, Viewers;
        public volatile bool Ended;
        public volatile bool DirectNow;
        public DateTime LastBeatOk = DateTime.UtcNow;
        // sound (all under SoundGate)
        public volatile bool Sound;
        public readonly object SoundGate = new();
        public readonly HashSet<uint> SoundSeen = new();
        public long SoundNewest = -1;
        /// <summary>The host's newest frame (its control packets), and since when this PC is more than half a second behind it.</summary>
        public long HostSoundNewest = -1;
        public DateTime SoundBehindSince = DateTime.MaxValue;
        public DateTime LastUdpSound;
        public Channel<ScreenSoundFrames.Frame>? SoundOut;
        public int SoundPump;
        public long SoundBytes;
    }

    private readonly PeerService _peers;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly EventHub _events;
    private readonly ILogger<ScreenViewer> _log;
    private readonly object _lock = new();
    private View? _v;
    private string _lastPublished = "";

    public ScreenViewer(PeerService peers, PeerClient client, PeerRegistry registry, EventHub events, ILogger<ScreenViewer> log)
    {
        _peers = peers; _client = client; _registry = registry; _events = events; _log = log;
        Net.LocalNetworks.Changed += () =>
        {
            View? v;
            lock (_lock) v = _v;
            if (v is { Ended: false, Multi: not null }) Listen(v, "the network changed");
        };
    }

    public bool Active { get { lock (_lock) return _v is { Ended: false }; } }

    public async Task<ScreenViewDto> JoinAsync(string peerId, string sessionId, CancellationToken ct)
    {
        await LeaveAsync();
        if (!_registry.TryGetOnline(peerId, out var peer)) throw new PeerUnavailableException("That PC is offline.");
        var v = new View { PeerId = peerId, Sid = sessionId, HostName = DisplayNames.Of(peer.User, peer.Name), Id = Guid.ParseExact(sessionId, "N") };
        // a port of my own first: the host may send packets straight to it
        try
        {
            v.Uni = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            v.Uni.Bind(new IPEndPoint(IPAddress.Any, 0));
            v.Uni.ReceiveTimeout = 1000;
            v.Uni.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch (Exception ex) { _log.LogDebug("screen: no UDP port: {Msg}", ex.Message); v.Uni?.Dispose(); v.Uni = null; }
        try
        {
            var req = _client.Build(peer, HttpMethod.Post, "/p/screen/" + Uri.EscapeDataString(sessionId) + "/join");
            req.Content = JsonContent.Create(new ScreenJoinRequestDto { UdpPort = (v.Uni?.LocalEndPoint as IPEndPoint)?.Port ?? 0 }, options: Json.Options);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"{v.HostName} is no longer sharing that screen.");
            if (resp.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException($"{v.HostName} shares that screen with other PCs only.");
            if (!resp.IsSuccessStatusCode) throw await PeerStatusException.FromAsync(resp);
            v.Join = await resp.Content.ReadFromJsonAsync<ScreenJoinDto>(Json.Options, ct) ?? throw new InvalidOperationException("Empty answer from the host.");
            v.Key = Convert.FromBase64String(v.Join.Key);
            v.Sound = v.Join.Sound;
        }
        catch { v.Uni?.Dispose(); throw; }

        if (v.Join.Group is { } group)
        {
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, v.Join.Port));
                sock.ReceiveTimeout = 1000;
                sock.ReceiveBufferSize = 4 * 1024 * 1024;
                v.Group = IPAddress.Parse(group);
                v.Multi = sock;
                Listen(v, null);
            }
            catch (Exception ex) { _log.LogWarning("screen: can't listen for multicast ({Msg}); it comes directly from {Host}", ex.Message, v.HostName); }
        }
        lock (_lock) _v = v;
        foreach (var sock in new[] { v.Uni, v.Multi })
            if (sock is not null) new Thread(() => ReceiveLoop(v, sock), 512 * 1024) { IsBackground = true, Name = "ocean-screen-rx" }.Start();
        _ = Task.Run(() => BeatLoopAsync(v));
        _log.LogInformation("screen: watching {Title} from {Host} ({W}x{H}; packets {How})", v.Join.Title, v.HostName, v.Join.Width, v.Join.Height,
            v.Join.Group is not null ? "by multicast" : v.Join.Unicast ? "straight to this PC" : "not possible: direct connection only");
        Publish(v, force: true);
        return ToDto(v);
    }

    private void Listen(View v, string? again)
    {
        if (v.Multi is not { } sock) return;
        lock (v.JoinLock)
        {
            v.LastListen = DateTime.UtcNow;
            foreach (var addr in v.Joined.ToList())
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(v.Group, addr)); } catch { }
            v.Joined.Clear();
            var hostAddrs = _registry.TryGet(v.PeerId, out var peer)
                ? (peer.Preferred is { } pref ? new[] { pref } : Array.Empty<IPAddress>()).Concat(peer.Addresses.Keys).Append(peer.Address)
                : Array.Empty<IPAddress>();
            foreach (var (addr, adapter) in Net.LocalNetworks.MulticastAddresses(hostAddrs))
            {
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(v.Group, addr)); v.Joined.Add(addr); }
                catch (Exception ex) { _log.LogDebug("screen: can't listen on {Adapter} ({Addr}): {Msg}", adapter, addr, ex.Message); }
            }
            _log.LogInformation("screen: listening on {Adapters}{Why}", v.Joined.Count == 0 ? "no adapter" : string.Join(", ", v.Joined), again is null ? "" : $" ({again})");
        }
    }

    public async Task LeaveAsync()
    {
        View? v;
        lock (_lock) { v = _v; _v = null; }
        if (v is null) return;
        v.Cts.Cancel();
        try { v.Uni?.Close(); } catch { }
        try { v.Multi?.Close(); } catch { }
        if (!v.Ended)
        {
            try
            {
                if (_registry.TryGetOnline(v.PeerId, out var peer))
                {
                    var req = _client.Build(peer, HttpMethod.Post, "/p/screen/" + v.Sid + "/leave");
                    using var cts = new CancellationTokenSource(3000);
                    using var _ = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
            }
            catch (Exception ex) { _log.LogDebug("screen: leave: {Msg}", ex.Message); }
        }
        _log.LogInformation("screen: stopped watching {Host} ({Udp} by UDP, {Direct} directly, {Lost} lost, {Recovered} recovered{Sound})", v.HostName,
            Format.Bytes(v.BytesUdp), Format.Bytes(v.BytesDirect), v.Lost, v.Recovered, v.SoundBytes > 0 ? $"; {Format.Bytes(v.SoundBytes)} of sound" : "");
        _events.Publish("screenView", new ScreenViewDto());
    }

    public ScreenViewDto Status()
    {
        View? v;
        lock (_lock) v = _v;
        return v is null ? new ScreenViewDto() : ToDto(v);
    }

    private static ScreenViewDto ToDto(View v) => new()
    {
        Active = !v.Ended, PeerId = v.PeerId, SessionId = v.Sid, Title = v.Join.Title, HostName = v.HostName,
        Width = v.Join.Width, Height = v.Join.Height, Fps = v.Join.Fps, Viewers = v.Viewers, Ended = v.Ended,
        Mode = v.Next < 0 ? "connecting" : v.DirectNow ? "direct" : "udp",
        BytesUdp = v.BytesUdp, BytesDirect = v.BytesDirect, Lost = v.Lost, Recovered = v.Recovered, Sound = v.Sound,
    };

    /// <summary>Only when something the app shows changed (not the byte counters).</summary>
    private void Publish(View v, bool force = false)
    {
        var dto = ToDto(v);
        var key = $"{dto.Mode}|{dto.Viewers}|{dto.Ended}|{dto.Sound}";
        if (!force && key == _lastPublished) return;
        _lastPublished = key;
        _events.Publish("screenView", dto);
    }

    // ------------------------------------------------------------------ putting the stream together

    /// <summary>Bytes of the stream from either source. Hands on what is in order, keeps what is ahead (packets only).</summary>
    private static void Deliver(View v, long offset, ReadOnlySpan<byte> bytes, bool udp)
    {
        lock (v.Gate)
        {
            var now = DateTime.UtcNow;
            if (v.Next < 0 || offset > v.Next)
            {
                if (!udp || v.Ahead.ContainsKey(offset)) return;
                if (v.AheadBytes > 6 * 1024 * 1024) { v.Ahead.Clear(); v.AheadBytes = 0; } // hopelessly behind: the direct connection brings it
                if (v.Ahead.Count == 0) v.HoleSince = now;
                v.Ahead[offset] = bytes.ToArray(); v.AheadBytes += bytes.Length;
                return;
            }
            var end = offset + bytes.Length;
            if (end <= v.Next) return;
            Emit(v, bytes[(int)(v.Next - offset)..], udp);
            v.Next = end; v.LastProgress = now;
            var flushed = false;
            while (v.Ahead.Count > 0)
            {
                var (at, chunk) = v.Ahead.First();
                if (at > v.Next) break;
                v.Ahead.Remove(at); v.AheadBytes -= chunk.Length; flushed = true;
                if (at + chunk.Length > v.Next) { Emit(v, chunk.AsSpan((int)(v.Next - at)), true); v.Next = at + chunk.Length; }
            }
            if (flushed && v.Ahead.Count > 0) v.HoleSince = now; // the next hole starts waiting now
        }
    }

    private static void Emit(View v, ReadOnlySpan<byte> bytes, bool udp)
    {
        if (udp) v.BytesUdp += bytes.Length; else v.BytesDirect += bytes.Length;
        v.Out?.Writer.TryWrite(bytes.ToArray());
    }

    /// <summary>The direct connection starts handing over at this offset.</summary>
    private static void Started(View v, long offset)
    {
        lock (v.Gate)
        {
            if (v.Next >= 0 && offset <= v.Next) return;
            v.Next = offset; v.LastProgress = DateTime.UtcNow;
            foreach (var at in v.Ahead.Where(kv => kv.Key + kv.Value.Length <= offset).Select(kv => kv.Key).ToList()) { v.AheadBytes -= v.Ahead[at].Length; v.Ahead.Remove(at); }
            if (v.Ahead.Count > 0) v.HoleSince = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// The stream for the app's decoder, from the latest key picture on, until the app closes it or the session ends.
    /// Every call starts afresh (a decoder can only begin at a key picture).
    /// </summary>
    public async Task<bool> StreamAsync(Func<ReadOnlyMemory<byte>, Task> write, CancellationToken ct)
    {
        View? v;
        lock (_lock) v = _v;
        if (v is null || v.Ended) return false;
        var output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        int pump;
        lock (v.Gate) { v.Next = -1; v.Ahead.Clear(); v.AheadBytes = 0; v.Out = output; pump = ++v.Pump; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, v.Cts.Token);
        Task<string>? direct = null;
        CancellationTokenSource? directCts = null;
        var directStarted = DateTime.MinValue;
        var directEnded = DateTime.MinValue;
        var directWait = TimeSpan.FromMilliseconds(200);
        var stickyUntil = DateTime.MinValue;
        var starts = new Queue<DateTime>();
        try
        {
            while (!linked.IsCancellationRequested && v.Pump == pump)
            {
                var wrote = false;
                while (output.Reader.TryRead(out var chunk)) { await write(chunk); wrote = true; }
                if (v.Ended) break;
                var now = DateTime.UtcNow;
                if (direct is { IsCompleted: true })
                {
                    var how = await direct;
                    direct = null; directCts?.Dispose(); directCts = null; directEnded = now;
                    if (how == "ended") { EndedByHost(v); break; }
                    if (how == "gone") { lock (v.Gate) { v.Next = -1; } } // too far behind: on from the latest key picture
                    directWait = how is "error" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(200);
                }
                if (direct is null)
                {
                    if (now - directEnded >= directWait && NeedDirect(v, now))
                    {
                        long from;
                        lock (v.Gate) from = v.Next;
                        directCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                        direct = DirectAsync(v, from, directCts.Token);
                        directStarted = now; v.DirectNow = true;
                        // packets that keep failing: stay on the direct connection for a while instead of going back and forth
                        starts.Enqueue(now);
                        while (starts.Count > 0 && now - starts.Peek() > TimeSpan.FromSeconds(20)) starts.Dequeue();
                        if (starts.Count >= 4) { stickyUntil = now + TimeSpan.FromSeconds(30); starts.Clear(); }
                        Publish(v);
                    }
                    else if (v.DirectNow && v.Next >= 0) { v.DirectNow = false; Publish(v); }
                }
                else if (now >= stickyUntil && now - directStarted > TimeSpan.FromMilliseconds(600) && PacketsKeepUp(v, now))
                {
                    directCts!.Cancel();
                    try { await direct; } catch { }
                    direct = null; directCts.Dispose(); directCts = null; directEnded = now; directWait = TimeSpan.Zero;
                    v.DirectNow = false;
                    Publish(v);
                }
                if (!wrote)
                {
                    using var nap = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    nap.CancelAfter(40);
                    try { await output.Reader.WaitToReadAsync(nap.Token); } catch (OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            directCts?.Cancel();
            if (direct is not null) { try { await direct; } catch { } }
            directCts?.Dispose();
            lock (v.Gate) { if (v.Pump == pump) { v.Out = null; v.Next = -1; } }
        }
        return true;
    }

    /// <summary>Is something missing that the packets should have brought by now?</summary>
    private static bool NeedDirect(View v, DateTime now)
    {
        lock (v.Gate)
        {
            if (v.Next < 0) return true;                                                              // the start
            if (v.Ahead.Count > 0 && now - v.HoleSince > TimeSpan.FromMilliseconds(120)) return true; // a hole
            if (v.Head > v.Next && now - v.LastProgress > TimeSpan.FromMilliseconds(300)) return true; // the end is missing
            return now - v.LastControl > TimeSpan.FromSeconds(2);                                     // no packets at all
        }
    }

    private static bool PacketsKeepUp(View v, DateTime now)
    {
        lock (v.Gate)
            return now - v.LastControl < TimeSpan.FromMilliseconds(1200) && now - v.LastUdpData < TimeSpan.FromMilliseconds(500)
                   && now - v.LastLoss > TimeSpan.FromSeconds(2) && v.Next >= 0;
    }

    /// <summary>Reads the stream from the host over the peer connection. Returns ended, gone, eof, error or cancelled.</summary>
    private async Task<string> DirectAsync(View v, long from, CancellationToken ct)
    {
        try
        {
            if (!_registry.TryGetOnline(v.PeerId, out var peer)) return "error";
            var req = _client.Build(peer, HttpMethod.Get, $"/p/screen/{v.Sid}/stream?from={from}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return "ended";
            if (resp.StatusCode == HttpStatusCode.Gone) return "gone";
            if (!resp.IsSuccessStatusCode) return "error";
            if (!resp.Headers.TryGetValues("X-Ocean-Offset", out var values) || !long.TryParse(values.FirstOrDefault(), out var offset)) return "error";
            Started(v, offset);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) return "eof";
                Deliver(v, offset, buffer.AsSpan(0, n), udp: false);
                offset += n;
            }
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception ex) { _log.LogDebug("screen: direct stream: {Msg}", ex.Message); return "error"; }
    }

    // ------------------------------------------------------------------ sound

    /// <summary>A sound frame from either source: on to the app unless it came already (packets carry each frame twice).</summary>
    private static void DeliverSound(View v, in ScreenSoundFrames.Frame f, bool udp)
    {
        lock (v.SoundGate)
        {
            if (udp) v.LastUdpSound = DateTime.UtcNow;
            if (f.Seq + 300 < v.SoundNewest || !v.SoundSeen.Add(f.Seq)) return;
            if (f.Seq > v.SoundNewest) v.SoundNewest = f.Seq;
            if (v.SoundSeen.Count > 600) v.SoundSeen.RemoveWhere(x => x + 300 < v.SoundNewest);
            v.SoundBytes += f.Data.Length;
            v.SoundOut?.Writer.TryWrite(f);
        }
    }

    /// <summary>
    /// The sound for the app's player, as it arrives (the player puts it in order), until the app closes it or the
    /// session ends. Silent while the host has the sound off. Without packets (none for 2 s, or the sound's packets
    /// stay behind what the host says it sent) it comes over the direct connection, and goes back to packets as
    /// soon as they arrive again.
    /// </summary>
    public async Task<bool> StreamSoundAsync(Func<IReadOnlyList<ScreenSoundFrames.Frame>, Task> write, CancellationToken ct)
    {
        View? v;
        lock (_lock) v = _v;
        if (v is null || v.Ended) return false;
        // a player that falls behind loses the oldest frames, not the newest
        var output = Channel.CreateBounded<ScreenSoundFrames.Frame>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        int pump;
        lock (v.SoundGate) { v.SoundOut = output; pump = ++v.SoundPump; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, v.Cts.Token);
        Task? direct = null;
        CancellationTokenSource? directCts = null;
        var directEnded = DateTime.MinValue;
        var batch = new List<ScreenSoundFrames.Frame>();
        try
        {
            while (!linked.IsCancellationRequested && v.SoundPump == pump && !v.Ended)
            {
                batch.Clear();
                while (output.Reader.TryRead(out var f)) batch.Add(f);
                if (batch.Count > 0) await write(batch);
                var now = DateTime.UtcNow;
                DateTime lastControl, lastUdp, behindSince;
                lock (v.Gate) lastControl = v.LastControl;
                lock (v.SoundGate) { lastUdp = v.LastUdpSound; behindSince = v.SoundBehindSince; }
                var noPackets = now - lastControl > TimeSpan.FromSeconds(2);
                var soundMissing = behindSince != DateTime.MaxValue && now - behindSince > TimeSpan.FromMilliseconds(500);
                if (direct is { IsCompleted: true }) { direct = null; directCts?.Dispose(); directCts = null; directEnded = now; }
                if (direct is null && v.Sound && (noPackets || soundMissing) && now - directEnded > TimeSpan.FromSeconds(1))
                {
                    long from;
                    lock (v.SoundGate) from = v.SoundNewest;
                    directCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    direct = DirectSoundAsync(v, from, directCts.Token);
                }
                else if (direct is not null && (!v.Sound || (!noPackets && now - lastUdp < TimeSpan.FromMilliseconds(500))))
                {
                    directCts!.Cancel();
                    try { await direct; } catch { }
                    direct = null; directCts.Dispose(); directCts = null; directEnded = now;
                }
                if (batch.Count == 0)
                {
                    using var nap = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    nap.CancelAfter(100);
                    try { await output.Reader.WaitToReadAsync(nap.Token); } catch (OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            directCts?.Cancel();
            if (direct is not null) { try { await direct; } catch { } }
            directCts?.Dispose();
            lock (v.SoundGate) { if (v.SoundPump == pump) v.SoundOut = null; }
        }
        return true;
    }

    private async Task DirectSoundAsync(View v, long after, CancellationToken ct)
    {
        try
        {
            if (!_registry.TryGetOnline(v.PeerId, out var peer)) return;
            var req = _client.Build(peer, HttpMethod.Get, $"/p/screen/{v.Sid}/sound?after={after}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return; // (an older host has no sound)
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var header = new byte[ScreenSoundFrames.HeaderSize];
            while (await ScreenSoundFrames.ReadAsync(stream, header, ct) is { } f) DeliverSound(v, f, udp: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("screen: direct sound: {Msg}", ex.Message); }
    }

    // ------------------------------------------------------------------ packets

    private void ReceiveLoop(View v, Socket sock)
    {
        using var aes = new AesGcm(v.Key, 16);
        var packet = new byte[2048];
        var plain = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!v.Cts.IsCancellationRequested)
        {
            int n;
            try { n = sock.ReceiveFrom(packet, ref from); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            catch { break; }
            try
            {
                if (from is IPEndPoint src && !Net.LocalNetworks.IsLan(src.Address)) continue; // local network only
                if (!CastWire.TryParse(packet.AsSpan(0, n), out var session, out var type, out var seq, out var payload) || session != v.Id) continue;
                if (TestDrop > 0 && type != CastWire.TypeControl && Random.Shared.Next(100) < TestDrop) continue;
                if (type == ScreenWire.TypeSound)
                {
                    if (!CastWire.Open(aes, type, seq, payload, plain, out var slen)) continue;
                    foreach (var f in ScreenWire.ReadSound(plain.AsSpan(0, slen))) DeliverSound(v, f, udp: true);
                    continue;
                }
                lock (v.Recent) // two sockets may bring the same packet
                {
                    if (v.Recent.ContainsKey(seq)) continue;
                    if (type == CastWire.TypeParity)
                    {
                        v.Recent[seq] = Array.Empty<byte>();
                        v.PendingParity[seq] = payload.ToArray();
                        TryParity(v, aes, plain, seq);
                        continue;
                    }
                    if (!CastWire.Open(aes, type, seq, payload, plain, out var len)) continue; // not ours, or tampered with
                    if (type == CastWire.TypeData)
                    {
                        if (!CastWire.ReadData(plain.AsSpan(0, len), out var offset, out var bytes)) continue;
                        v.Recent[seq] = payload.ToArray();
                        v.LastUdpData = DateTime.UtcNow;
                        Deliver(v, offset, bytes, udp: true);
                        if (v.PendingParity.Count > 0) TryParity(v, aes, plain, seq);
                        if (v.Recent.Count > 1024) foreach (var old in v.Recent.Keys.Where(k => k + 512 < seq).ToList()) v.Recent.Remove(old);
                    }
                    else if (type == CastWire.TypeControl && ScreenWire.ReadControl(plain.AsSpan(0, len), out var head, out var viewers, out var ended, out var sound, out var soundNewest))
                    {
                        v.Recent[seq] = Array.Empty<byte>();
                        lock (v.Gate) { v.Head = Math.Max(v.Head, head); v.LastControl = DateTime.UtcNow; }
                        v.Viewers = viewers;
                        v.Sound = sound;
                        lock (v.SoundGate)
                        {
                            v.HostSoundNewest = soundNewest;
                            var behind = sound && soundNewest > v.SoundNewest + 25;
                            if (!behind) v.SoundBehindSince = DateTime.MaxValue;
                            else if (v.SoundBehindSince == DateTime.MaxValue) v.SoundBehindSince = DateTime.UtcNow;
                        }
                        if (ended) { EndedByHost(v); return; }
                        Publish(v);
                    }
                }
            }
            catch (Exception ex) { _log.LogDebug("screen packet: {Msg}", ex.Message); }
        }
    }

    /// <summary>
    /// Parity groups whose packets are all here but one: that one is rebuilt. A group still short of more than one
    /// packet waits a little (packets come in any order), then counts as lost: the direct connection brings it.
    /// </summary>
    private static void TryParity(View v, AesGcm aes, byte[] plain, uint seq)
    {
        foreach (var (pseq, parity) in v.PendingParity.ToList())
        {
            var missing = CastWire.Missing(parity, s => v.Recent.TryGetValue(s, out var d) && d.Length > 0);
            if (missing == 0) { v.PendingParity.Remove(pseq); continue; }
            if (missing == 1)
            {
                var recovered = CastWire.Recover(parity, s => v.Recent.TryGetValue(s, out var d) && d.Length > 0 ? d : null);
                if (recovered is { } r && CastWire.Open(aes, CastWire.TypeData, r.Seq, r.Sealed, plain, out var rlen) && CastWire.ReadData(plain.AsSpan(0, rlen), out var roff, out var rbytes))
                {
                    v.Recent[r.Seq] = r.Sealed;
                    v.Recovered++;
                    Deliver(v, roff, rbytes, udp: true);
                    v.PendingParity.Remove(pseq);
                    continue;
                }
            }
            if (pseq + 24 < seq)
            {
                v.PendingParity.Remove(pseq);
                v.Lost += missing;
                lock (v.Gate) v.LastLoss = DateTime.UtcNow;
            }
        }
    }

    private async Task BeatLoopAsync(View v)
    {
        var ct = v.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(BeatEvery, ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (_registry.TryGetOnline(v.PeerId, out var peer))
                {
                    var req = _client.Build(peer, HttpMethod.Post, "/p/screen/" + v.Sid + "/beat");
                    req.Content = JsonContent.Create(new ScreenBeatDto { BytesUdp = v.BytesUdp, BytesDirect = v.BytesDirect, Lost = v.Lost, Recovered = v.Recovered, Direct = v.DirectNow }, options: Json.Options);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(8000);
                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                    if (resp.StatusCode == HttpStatusCode.NotFound) { EndedByHost(v); return; }
                    if (resp.IsSuccessStatusCode)
                    {
                        var st = await resp.Content.ReadFromJsonAsync<ScreenStateDto>(Json.Options, cts.Token);
                        if (st is { Ended: true }) { EndedByHost(v); return; }
                        if (st is not null) { v.Viewers = st.Viewers; v.Sound = st.Sound; v.LastBeatOk = DateTime.UtcNow; Publish(v); }
                        // the host answers but no packet has come for a while: join the group again (at most once a minute)
                        if (v.Multi is not null && DateTime.UtcNow - v.LastControl > TimeSpan.FromSeconds(20) && DateTime.UtcNow - v.LastListen > TimeSpan.FromMinutes(1))
                            Listen(v, "no packets for a while");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug("screen beat: {Msg}", ex.Message); }
            // the host has been out of reach for a while (switched off, left the network): it is over
            if (DateTime.UtcNow - v.LastBeatOk > TimeSpan.FromSeconds(40)) { EndedByHost(v, "out of reach"); return; }
        }
    }

    private void EndedByHost(View v, string why = "ended by the host")
    {
        if (v.Ended) return;
        v.Ended = true;
        _log.LogInformation("screen: {Host}'s sharing is over ({Why})", v.HostName, why);
        _events.Publish("screenView", ToDto(v));
        _ = LeaveAsync();
    }

    public void Dispose() => LeaveAsync().GetAwaiter().GetResult();
}
