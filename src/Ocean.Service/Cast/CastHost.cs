using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Media;
using Ocean.Service.Net;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Cast;

/// <summary>
/// My multicast (host side): sends one of my videos once onto the LAN, in playback order and paced a little ahead of
/// where my player is, and tells viewers where I am (play, pause, seek). One at a time per PC; at most 30 % of the PCs
/// on the network at once. Viewers join through <c>/p/cast</c> (same access rules as the video), get the session key
/// over the encrypted channel, and fetch directly from me whatever the multicast didn't bring them.
/// </summary>
public sealed class CastHost : IDisposable
{
    /// <summary>At most this share of the PCs on the network may multicast at the same time.</summary>
    public const double MaxShare = 0.30;
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(15);
    private const double LeadSeconds = 20;   // how far ahead of the host's position the packets run
    private const double RateFactor = 1.5;   // × the video's bitrate once the lead is filled
    private const double CatchUpFactor = 4;  // × the bitrate while the packets are within 5 s of the host's position (start, seek)

    private sealed class Viewer
    {
        public string Name = "";
        public DateTimeOffset LastBeat;
        public long BytesMulticast;
        public long PreviousBytes;
        public bool Receiving;
    }

    private sealed class Session
    {
        public Guid Id;
        public byte[] Key = Array.Empty<byte>();
        public string ItemId = "", StreamItemId = "", Name = "", ShareId = "", ShareName = "", Path = "";
        public bool Light;
        public long Size;
        public double Duration;
        public DateTimeOffset ModifiedAt, StartedAt;
        public double Position;      // where the host's player is …
        public long PositionAtMs;    // … at this moment (Unix ms)
        public bool Playing;
        /// <summary>The host closed the player: the session runs on its own clock and ends when the video does.</summary>
        public bool Unattended;
        /// <summary>The PC's networks changed: the sender opens its sockets again on the adapters there are now.</summary>
        public volatile bool NetworkChanged;
        public bool Ended;
        public long Cursor;          // next byte to send
        public readonly ConcurrentDictionary<string, Viewer> Viewers = new(StringComparer.Ordinal);
        public readonly CancellationTokenSource Cts = new();
        public Thread? Thread;

        public double Now() => Playing ? Math.Min(Duration > 0 ? Duration : double.MaxValue, Position + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - PositionAtMs) / 1000.0) : Position;
        public long ByteAt(double seconds) => Duration <= 0 ? 0 : Math.Clamp((long)(seconds / Duration * Size), 0, Size);
    }

    private readonly CatalogService _catalog;
    private readonly LightCopies _light;
    private readonly PeerService _peers;
    private readonly PeerRegistry _registry;
    private readonly ViewerTracker _tracker;
    private readonly EventHub _events;
    private readonly ILogger<CastHost> _log;
    private readonly object _lock = new();
    private Session? _s;
    private readonly Timer _publish;

    public CastHost(CatalogService catalog, LightCopies light, PeerService peers, PeerRegistry registry, ViewerTracker tracker, EventHub events, ILogger<CastHost> log)
    {
        _catalog = catalog; _light = light; _peers = peers; _registry = registry; _tracker = tracker; _events = events; _log = log;
        _catalog.CastLookup = Info;
        _publish = new Timer(_ => _events.Publish("myCast", Status()), null, Timeout.Infinite, Timeout.Infinite);
        Net.LocalNetworks.Changed += () => { Session? s; lock (_lock) s = _s; if (s is not null) s.NetworkChanged = true; };
    }

    // ------------------------------------------------------------------ for the app

    /// <summary>May one more multicast start on this network?</summary>
    public CastLimitDto Limit()
    {
        var nodes = _registry.OnlineCount + 1;
        var active = _peers.Peers().Count(p => p.Online && p.Cast is not null);
        var max = Math.Max(1, (int)Math.Floor(nodes * MaxShare));
        var allowed = active < max;
        return new CastLimitDto
        {
            Allowed = allowed, Active = active, Nodes = nodes, MaxAllowed = max,
            Reason = allowed ? null
                : $"{active} of the {nodes} PCs on this network {(active == 1 ? "is" : "are")} already multicasting. More than {MaxShare:P0} at once would slow the network down for everyone; try again when one of them stops.",
        };
    }

    public MyCastDto Start(string itemId, bool preferLight)
    {
        lock (_lock)
        {
            if (_s is not null) { var busy = Status(); busy.Error = "You are already multicasting; stop that one first."; return busy; }
            if (!_catalog.TryGetItem(itemId, out var share, out var item)) return new MyCastDto { Error = "This video is no longer in your shares." };
            var limit = Limit();
            if (!limit.Allowed) return new MyCastDto { Error = limit.Reason };

            var lightPath = preferLight ? _light.PathFor(itemId, item) : null;
            var s = new Session
            {
                Id = Guid.NewGuid(), Key = RandomNumberGenerator.GetBytes(32),
                ItemId = itemId, StreamItemId = lightPath is null ? itemId : itemId + LightVariant.Suffix,
                Name = item.Name, ShareId = share.Id, ShareName = share.Name, Light = lightPath is not null,
                Path = lightPath ?? _catalog.FullPath(share, item),
                Duration = (item.DurationMs ?? 0) / 1000.0, ModifiedAt = item.ModifiedAt, StartedAt = DateTimeOffset.UtcNow,
                PositionAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            try { s.Size = new FileInfo(s.Path).Length; }
            catch (Exception ex) { return new MyCastDto { Error = "The video file can't be read: " + ex.Message }; }
            s.Thread = new Thread(() => SendLoop(s), 512 * 1024) { IsBackground = true, Name = "ocean-cast" };
            _s = s;
            s.Thread.Start();
            _log.LogInformation("multicast started: {Name} ({Size}, {Light}) to {Group}:{Port}", s.Name, Format.Bytes(s.Size), s.Light ? "lighter copy" : "original", CastWire.GroupAddress, CastWire.Port);
        }
        _catalog.NotifyCastChanged();
        var st = Status();
        _events.Publish("myCast", st);
        return st;
    }

    /// <summary>The host's player moved (every second while playing, at once on play/pause/seek).</summary>
    public void UpdateState(double position, bool playing)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return;
        var expected = s.Now();
        var jumped = Math.Abs(position - expected) > 3;
        s.Position = position; s.PositionAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); s.Playing = playing;
        if (jumped) s.Cursor = s.ByteAt(Math.Max(0, position - 1)); // seek: the packets start again from just before it
        if (s.Unattended)
        {
            s.Unattended = false; // the host's player is back and drives it again
            _log.LogInformation("multicast: the host's player is back at {Pos:0} s", position);
            _events.Publish("myCast", Status());
        }
    }

    /// <summary>
    /// The host closed the player but keeps the multicast running: it plays on by itself (a pause is lifted) and
    /// ends when the video does. The player can take it back any time (<see cref="UpdateState"/>).
    /// </summary>
    public void Detach()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || s.Ended) return;
        if (!s.Playing) { s.Position = s.Now(); s.PositionAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); s.Playing = true; }
        s.Unattended = true;
        _log.LogInformation("multicast: goes on without the host's player from {Pos:0} s, {Viewers} viewer(s)", s.Now(), LiveViewers(s).Count);
        _events.Publish("myCast", Status());
    }

    public void Stop()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is not null) End(s, "stopped by the host");
    }

    /// <summary>Ends this session (and only this one: a later session is never touched by an old sender thread).</summary>
    private void End(Session s, string why)
    {
        lock (_lock) { if (!ReferenceEquals(_s, s)) return; _s = null; }
        s.Ended = true;
        s.Cts.CancelAfter(1500); // the sender still announces the end a few times
        _log.LogInformation("multicast ended ({Why}): {Name} after {Min:0} min, {Viewers} viewer(s) at the end", why, s.Name, (DateTimeOffset.UtcNow - s.StartedAt).TotalMinutes, LiveViewers(s).Count);
        _catalog.NotifyCastChanged();
        _events.Publish("myCast", Status());
    }

    public MyCastDto Status()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return new MyCastDto();
        var live = LiveViewers(s);
        return new MyCastDto
        {
            Active = true, SessionId = s.Id.ToString("N"), ItemId = s.ItemId, ItemName = s.Name, ShareName = s.ShareName, ShareId = s.ShareId, Light = s.Light,
            Viewers = live.Count, ViewersMulticast = live.Count(v => v.Receiving), Position = s.Now(), Playing = s.Playing, StartedAt = s.StartedAt,
            Unattended = s.Unattended,
        };
    }

    /// <summary>For my catalog: what everyone sees.</summary>
    public CastInfoDto? Info()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return null;
        return new CastInfoDto { SessionId = s.Id.ToString("N"), ItemId = s.ItemId, ItemName = s.Name, ShareName = s.ShareName, Light = s.Light, StartedAt = s.StartedAt, Viewers = LiveViewers(s).Count };
    }

    // ------------------------------------------------------------------ for viewers (peer API)

    /// <summary>The video a session is about (null when there is no such session): the caller checks access to it.</summary>
    public string? ItemIdOf(string sessionId)
    {
        Session? s;
        lock (_lock) s = _s;
        return s is not null && s.Id.ToString("N") == sessionId ? s.ItemId : null;
    }

    public CastJoinDto? Join(string sessionId, string clientId, string clientName)
    {
        var s = Find(sessionId);
        if (s is null) return null;
        var v = s.Viewers.GetOrAdd(clientId, _ => new Viewer());
        v.Name = clientName; v.LastBeat = DateTimeOffset.UtcNow;
        _log.LogInformation("multicast: {Viewer} joined {Name}", clientName, s.Name);
        ViewersChanged();
        return new CastJoinDto
        {
            SessionId = sessionId, ItemId = s.StreamItemId, Name = s.Light ? Path.ChangeExtension(s.Name, ".mp4") : s.Name, ShareId = s.ShareId,
            Size = s.Size, ModifiedAt = s.ModifiedAt, DurationMs = s.Duration > 0 ? (long)(s.Duration * 1000) : null,
            Group = CastWire.GroupAddress.ToString(), Port = CastWire.Port, Key = Convert.ToBase64String(s.Key), State = StateOf(s),
        };
    }

    public CastStateDto? Beat(string sessionId, string clientId, CastBeatDto beat)
    {
        var s = Find(sessionId);
        if (s is null) return new CastStateDto { SessionId = sessionId, Ended = true };
        if (s.Viewers.TryGetValue(clientId, out var v))
        {
            v.LastBeat = DateTimeOffset.UtcNow;
            v.Receiving = beat.BytesMulticast > v.PreviousBytes;
            v.PreviousBytes = v.BytesMulticast; v.BytesMulticast = beat.BytesMulticast;
            ViewersChanged();
        }
        return StateOf(s);
    }

    public void Leave(string sessionId, string clientId)
    {
        var s = Find(sessionId);
        if (s is null || !s.Viewers.TryRemove(clientId, out var v)) return;
        _log.LogInformation("multicast: {Viewer} left {Name}", v.Name, s.Name);
        ViewersChanged();
    }

    public CastStateDto? State(string sessionId)
    {
        var s = Find(sessionId);
        return s is null ? new CastStateDto { SessionId = sessionId, Ended = true } : StateOf(s);
    }

    private Session? Find(string sessionId)
    {
        Session? s;
        lock (_lock) s = _s;
        return s is not null && !s.Ended && s.Id.ToString("N") == sessionId ? s : null;
    }

    private CastStateDto StateOf(Session s) => new()
    {
        SessionId = s.Id.ToString("N"), Active = !s.Ended, Position = s.Now(), Playing = s.Playing,
        HostTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Viewers = LiveViewers(s).Count, Ended = s.Ended,
    };

    private static List<Viewer> LiveViewers(Session s)
    {
        var cutoff = DateTimeOffset.UtcNow - ViewerTimeout;
        return s.Viewers.Values.Where(v => v.LastBeat > cutoff).ToList();
    }

    private void ViewersChanged() => _publish.Change(1000, Timeout.Infinite);

    // ------------------------------------------------------------------ sending

    private void SendLoop(Session s)
    {
        var sockets = new List<Socket>();
        var socketsAt = TimeSpan.Zero;
        // only the real network adapter the other PCs are reached through (never a VPN or virtual adapter); opened again
        // when the PC's networks change (Wi-Fi reconnecting, a new address, a VPN switched on or off) and, while there
        // is none, every 10 s, so a network that comes back carries the multicast again
        void OpenSockets(string? why)
        {
            foreach (var old in sockets) old.Dispose();
            sockets.Clear();
            var peerAddrs = _registry.Snapshot().Where(p => p.Online && !p.Bridged).SelectMany(p => p.Addresses.Keys.Append(p.Address));
            var adapters = LocalNetworks.MulticastAddresses(peerAddrs);
            foreach (var (addr, _) in adapters)
            {
                try
                {
                    var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sock.Bind(new IPEndPoint(addr, 0));
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                    sock.SendBufferSize = 4 * 1024 * 1024;
                    sockets.Add(sock);
                }
                catch (Exception ex) { _log.LogDebug("multicast: no sender on {Addr}: {Msg}", addr, ex.Message); }
            }
            var where = sockets.Count == 0 ? "no adapter" : string.Join(", ", adapters.Select(a => $"{a.Adapter} ({a.Address})"));
            if (why is null || sockets.Count > 0) _log.LogInformation("multicast: sending on {Adapters}{Why}", where, why is null ? "" : $" ({why})");
            if (sockets.Count == 0 && why is null) _log.LogWarning("multicast: no network adapter to send on; viewers get a direct stream");
        }
        try
        {
            var target = new IPEndPoint(CastWire.GroupAddress, CastWire.Port);
            OpenSockets(null);

            using var aes = new AesGcm(s.Key, 16);
            using var file = File.Open(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitrate = s.Duration > 0 ? s.Size / s.Duration : 4 * 1024 * 1024;
            var buffer = new byte[CastWire.DataChunk];
            uint seq = 0;
            var group = new List<(uint Seq, byte[] Sealed)>(CastWire.Group);
            double budget = 0;
            var clock = Stopwatch.StartNew();
            var lastTick = clock.Elapsed.TotalSeconds;
            var lastControl = 0.0;
            var endsSent = 0;
            var finishedAt = 0.0; // unattended: when the video reached its end (the session ends a little later)
            var ct = s.Cts.Token;

            void Send(byte[] packet) { foreach (var sock in sockets) { try { sock.SendTo(packet, target); } catch { } } }

            while (!ct.IsCancellationRequested)
            {
                if (s.NetworkChanged) { s.NetworkChanged = false; socketsAt = clock.Elapsed; OpenSockets("the network changed"); }
                else if (sockets.Count == 0 && clock.Elapsed - socketsAt > TimeSpan.FromSeconds(10)) { socketsAt = clock.Elapsed; OpenSockets("trying again"); }
                var now = clock.Elapsed.TotalSeconds;
                var bytesPerSecond = bitrate * (s.Duration > 0 && s.Cursor < s.ByteAt(s.Now() + 5) ? CatchUpFactor : RateFactor);
                budget = Math.Min(budget + (now - lastTick) * bytesPerSecond, bytesPerSecond / 20); // smooth: bursts of 50 ms at most
                lastTick = now;

                if (now - lastControl >= 0.5 || s.Ended)
                {
                    lastControl = now;
                    var control = CastWire.ControlPlain(s.Now(), s.Playing, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LiveViewers(s).Count, s.Ended);
                    Send(CastWire.Build(s.Id, CastWire.TypeControl, seq, CastWire.Seal(aes, CastWire.TypeControl, seq, control)));
                    seq++;
                    if (s.Ended && ++endsSent >= 3) break;
                }
                if (s.Ended) { Thread.Sleep(300); continue; }

                // without the host's player nobody presses Stop: the session ends by itself once the video is over
                if (s.Unattended)
                {
                    var finished = s.Duration > 0 ? s.Now() >= s.Duration : s.Cursor >= s.Size;
                    if (!finished) finishedAt = 0;
                    else if (finishedAt == 0) finishedAt = now;
                    else if (now - finishedAt >= 10) { End(s, "the video finished"); continue; }
                }

                // data: from the cursor, within the lead, as the budget allows
                var limit = s.Duration > 0 ? s.ByteAt(s.Now() + LeadSeconds) : s.Size;
                var sent = false;
                while (budget >= CastWire.DataChunk && s.Cursor < Math.Min(limit, s.Size))
                {
                    var n = (int)Math.Min(buffer.Length, s.Size - s.Cursor);
                    file.Position = s.Cursor;
                    var read = file.Read(buffer, 0, n);
                    if (read <= 0) break;
                    var sealedData = CastWire.Seal(aes, CastWire.TypeData, seq, CastWire.DataPlain(s.Cursor, buffer.AsSpan(0, read)));
                    Send(CastWire.Build(s.Id, CastWire.TypeData, seq, sealedData));
                    group.Add((seq, sealedData));
                    seq++;
                    s.Cursor += read;
                    budget -= read;
                    sent = true;
                    if (group.Count == CastWire.Group || s.Cursor >= s.Size)
                    {
                        Send(CastWire.Build(s.Id, CastWire.TypeParity, seq, CastWire.Parity(group)));
                        seq++;
                        group.Clear();
                    }
                }
                if (!sent) Thread.Sleep(budget < CastWire.DataChunk && s.Cursor < Math.Min(limit, s.Size) ? 2 : 10);
            }
        }
        catch (Exception ex) when (!s.Cts.IsCancellationRequested) { _log.LogWarning("multicast sender stopped: {Msg}", ex.Message); }
        finally { foreach (var sock in sockets) sock.Dispose(); }
    }

    public void Dispose()
    {
        Stop();
        _publish.Dispose();
    }
}
