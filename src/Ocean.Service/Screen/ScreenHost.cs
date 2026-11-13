using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Cast;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Net;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Screen;

/// <summary>
/// My screen sharing (host side). The app captures what the user picked and feeds the encoded stream (H.264) in
/// through the local API; this passes it on:
/// - as UDP packets (the multicast's sealed packet format with parity): to the multicast group when everyone may
///   watch, straight to each viewer's port otherwise;
/// - over the peer connection (<c>/p/screen/{id}/stream</c>) for whatever UDP didn't bring: the start (from the
///   latest key picture), lost packets, and everything where UDP doesn't arrive at all (bridges, some Wi-Fi).
/// One session per PC. A session for everyone is in my catalog; a session for invited PCs ends by itself once
/// nobody is left who watches or may still answer. While nobody watches, the app is told not to capture at all.
/// A session never outlives the app: without it (closed, crashed) the sharing ends within seconds.
/// </summary>
public sealed class ScreenHost : IDisposable
{
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(15);
    /// <summary>An invitation waits this long for an answer.</summary>
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(45);

    private sealed class Viewer
    {
        public string Name = "";
        public DateTimeOffset LastBeat;
        public IPEndPoint? Udp;      // packets straight to it (sessions for invited PCs)
        public bool Direct;
        public ScreenBeatDto Last = new();
    }

    private sealed class Invited
    {
        public string Name = "";
        public string State = "asked";
        public string AskId = "";
        public DateTimeOffset At = DateTimeOffset.UtcNow;
    }

    private sealed class Session
    {
        public Guid Id;
        public string Sid = "";
        public byte[] Key = Array.Empty<byte>();
        public string Title = "";
        public int Width, Height, Fps;
        public bool Everyone;
        public DateTimeOffset StartedAt;
        public readonly ScreenRing Ring = new();
        public readonly ConcurrentDictionary<string, Viewer> Viewers = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, Invited> People = new(StringComparer.Ordinal);
        public readonly CancellationTokenSource Cts = new();
        public volatile bool Ended;
        public volatile bool NetworkChanged = true; // sockets are opened by the first packet
        /// <summary>A viewer starts at a key picture from here on (the encoder was off before).</summary>
        public long MinStart;
        public DateTimeOffset LastJoin;
        public bool FeedOn;
        // sending (all under SendLock)
        public readonly object SendLock = new();
        public AesGcm? Aes;
        public uint Seq;
        public readonly List<(uint Seq, byte[] Sealed)> Group = new(CastWire.Group);
        public readonly List<Socket> Multicast = new();
        public Socket? Unicast;
        public long Sent;
        public bool WantedTold;
    }

    private readonly CatalogService _catalog;
    private readonly PeerService _peers;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly EventHub _events;
    private readonly ILogger<ScreenHost> _log;
    private readonly object _lock = new();
    private Session? _s;
    private readonly Timer _tick;
    private readonly Timer _publish;
    private DateTimeOffset? _noAppSince;

    public ScreenHost(CatalogService catalog, PeerService peers, PeerClient client, PeerRegistry registry, EventHub events, ILogger<ScreenHost> log)
    {
        _catalog = catalog; _peers = peers; _client = client; _registry = registry; _events = events; _log = log;
        _catalog.ScreenLookup = Info;
        _tick = new Timer(_ => Tick(), null, 500, 500);
        _publish = new Timer(_ => _events.Publish("myScreen", Status()), null, Timeout.Infinite, Timeout.Infinite);
        LocalNetworks.Changed += () => { Session? s; lock (_lock) s = _s; if (s is not null) s.NetworkChanged = true; };
    }

    // ------------------------------------------------------------------ for the app

    public MyScreenDto Start(ScreenStartDto req)
    {
        Session s;
        lock (_lock)
        {
            if (_s is not null) { var busy = Status(); busy.Error = "You are already sharing your screen; stop that first."; return busy; }
            if (req.Width < 16 || req.Height < 16 || req.Width > 4096 || req.Height > 4096) return new MyScreenDto { Error = "That picture size can't be shared." };
            if (!req.Everyone && req.Invite.Count == 0 && req.AskId is null && req.OfferAskId is null) return new MyScreenDto { Error = "Nobody was chosen to watch." };
            if (req.Everyone)
            {
                // a screen for everyone loads the network like a multicast: the same limit
                var nodes = _registry.OnlineCount + 1;
                var active = _peers.Peers().Count(p => p.Online && (p.Cast is not null || p.Screen is not null));
                var max = Math.Max(1, (int)Math.Floor(nodes * CastHost.MaxShare));
                if (active >= max)
                    return new MyScreenDto { Error = $"{active} of the {nodes} PCs on this network {(active == 1 ? "is" : "are")} already streaming to everyone. More than {CastHost.MaxShare:P0} at once would slow the network down; try again when one of them stops." };
            }
            var id = Guid.NewGuid();
            s = new Session
            {
                Id = id, Sid = id.ToString("N"), Key = RandomNumberGenerator.GetBytes(32),
                Title = Clean(req.Title), Width = req.Width, Height = req.Height, Fps = Math.Clamp(req.Fps, 1, 60),
                Everyone = req.Everyone, StartedAt = DateTimeOffset.UtcNow,
            };
            s.Aes = new AesGcm(s.Key, 16);
            _s = s;
        }
        _log.LogInformation("screen sharing started: {Title} ({W}x{H}, {Fps} fps) for {Who}", s.Title, s.Width, s.Height, s.Fps, s.Everyone ? "everyone" : "invited PCs");
        if (s.Everyone) _catalog.NotifyCastChanged();
        foreach (var peerId in req.Invite.Distinct()) Invite(s, peerId);
        var st = Status();
        _events.Publish("myScreen", st);
        return st;
    }

    private static string Clean(string? title)
    {
        var t = new string((title ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? "Screen" : t.Length > 120 ? t[..120] : t;
    }

    /// <summary>Asks more PCs to watch the running session.</summary>
    public MyScreenDto InviteMore(IEnumerable<string> peerIds)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return new MyScreenDto { Error = "You are not sharing your screen." };
        foreach (var id in peerIds.Distinct()) Invite(s, id);
        return Status();
    }

    /// <summary>Lets a PC in without asking it (it asked for my screen and the user allowed it). Returns the session id.</summary>
    public string? Admit(string peerId, string name)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return null;
        s.People[peerId] = new Invited { Name = name, State = "accepted" };
        Changed();
        return s.Sid;
    }

    private void Invite(Session s, string peerId)
    {
        if (s.People.TryGetValue(peerId, out var known) && known.State is "asked" or "accepted" or "watching") return;
        var person = new Invited { Name = _peers.NameOf(peerId), AskId = Guid.NewGuid().ToString("N") };
        s.People[peerId] = person;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_registry.TryGetOnline(peerId, out var peer)) { person.State = "unreachable"; Changed(); return; }
                var req = _client.Build(peer, HttpMethod.Post, "/p/screen/ask");
                req.Content = JsonContent.Create(new ScreenAskDto { AskId = person.AskId, Kind = ScreenAskDto.Watch, SessionId = s.Sid, Title = s.Title }, options: Json.Options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
                cts.CancelAfter(10_000);
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (resp.IsSuccessStatusCode) return; // its user is being asked; the answer comes by itself
                var err = resp.StatusCode == HttpStatusCode.NotFound ? null : await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options, cts.Token);
                person.State = resp.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "old",
                    _ => err?.Error is "away" or "muted" or "busy" ? err.Error : "unreachable",
                };
                _log.LogInformation("screen sharing: {Name} can't be asked ({State})", person.Name, person.State);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                person.State = "unreachable";
                _log.LogInformation("screen sharing: {Name} can't be asked: {Msg}", person.Name, ex.Message);
            }
            catch (OperationCanceledException) { if (!s.Ended) person.State = "unreachable"; }
            Changed();
        });
    }

    /// <summary>An invited PC answered. False: the answer is not about my session.</summary>
    public bool Answered(string peerId, ScreenAnswerDto answer)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || !s.People.TryGetValue(peerId, out var person) || person.AskId != answer.AskId) return false;
        if (person.State != "asked") return true;
        person.State = answer.Allow ? "accepted" : answer.Reason is "timeout" or "away" or "muted" or "busy" ? answer.Reason : "declined";
        person.At = DateTimeOffset.UtcNow;
        _log.LogInformation("screen sharing: {Name} {What}", person.Name, answer.Allow ? "will watch" : "does not watch (" + person.State + ")");
        _events.Publish("screenInvite", new { peerId, peerName = person.Name, state = person.State });
        Changed();
        return true;
    }

    public void Stop() { Session? s; lock (_lock) s = _s; if (s is not null) End(s, "stopped"); }

    private void End(Session s, string why)
    {
        lock (_lock) { if (!ReferenceEquals(_s, s)) return; _s = null; }
        s.Ended = true;
        _log.LogInformation("screen sharing ended ({Why}): {Title} after {Min:0.0} min, {Sent} sent", why, s.Title, (DateTimeOffset.UtcNow - s.StartedAt).TotalMinutes, Format.Bytes(s.Sent));
        // viewers: told by packets (a few times), by their next report and by the stream ending
        for (var i = 0; i < 3; i++) SendControl(s);
        s.Ring.Close();
        s.Cts.Cancel();
        // PCs still being asked: the question goes away
        foreach (var (peerId, person) in s.People)
        {
            if (person.State != "asked") continue;
            var askId = person.AskId;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!_registry.TryGetOnline(peerId, out var peer)) return;
                    var req = _client.Build(peer, HttpMethod.Post, "/p/screen/cancel");
                    req.Content = JsonContent.Create(new ScreenAnswerDto { AskId = askId }, options: Json.Options);
                    using var cts = new CancellationTokenSource(5000);
                    using var _ = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                catch { }
            });
        }
        lock (s.SendLock)
        {
            foreach (var sock in s.Multicast) sock.Dispose();
            s.Multicast.Clear();
            s.Unicast?.Dispose(); s.Unicast = null;
            s.Aes?.Dispose(); s.Aes = null;
        }
        if (s.Everyone) _catalog.NotifyCastChanged();
        var st = Status();
        st.Ended = why;
        _events.Publish("myScreen", st);
    }

    public MyScreenDto Status()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return new MyScreenDto();
        var live = Live(s);
        var people = s.People.Select(kv => new ScreenPersonDto { Id = kv.Key, Name = kv.Value.Name, State = kv.Value.State }).ToList();
        // viewers nobody invited (a share with everyone)
        people.AddRange(live.Where(kv => !s.People.ContainsKey(kv.Key)).Select(kv => new ScreenPersonDto { Id = kv.Key, Name = kv.Value.Name, State = "watching" }));
        return new MyScreenDto
        {
            Active = true, SessionId = s.Sid, Title = s.Title, Everyone = s.Everyone, Width = s.Width, Height = s.Height, Fps = s.Fps,
            Viewers = live.Count, People = people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), StartedAt = s.StartedAt, Wanted = Wanted(s),
        };
    }

    public bool Active { get { lock (_lock) return _s is not null; } }

    /// <summary>For my catalog: only a session everyone may watch.</summary>
    public ScreenInfoDto? Info()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || !s.Everyone) return null;
        return new ScreenInfoDto { SessionId = s.Sid, Title = s.Title, StartedAt = s.StartedAt, Viewers = Live(s).Count, Width = s.Width, Height = s.Height };
    }

    private static List<KeyValuePair<string, Viewer>> Live(Session s)
    {
        var cutoff = DateTimeOffset.UtcNow - ViewerTimeout;
        return s.Viewers.Where(kv => kv.Value.LastBeat > cutoff).ToList();
    }

    /// <summary>Is there anybody to capture for?</summary>
    private static bool Wanted(Session s)
        => Live(s).Count > 0 || DateTimeOffset.UtcNow - s.LastJoin < ViewerTimeout
           || s.People.Values.Any(p => p.State == "accepted" && DateTimeOffset.UtcNow - p.At < TimeSpan.FromSeconds(20));

    private void Changed() => _publish.Change(300, Timeout.Infinite);

    /// <summary>Twice a second: the control packet, viewers that went silent, and the end of a session nobody is left in.</summary>
    private void Tick()
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return;
        try
        {
            SendControl(s);
            var now = DateTimeOffset.UtcNow;
            // the app is gone (closed, crashed, ended in Task Manager): nobody could see or stop the sharing any more
            if (_events.SubscriberCount > 0) _noAppSince = null;
            else if (_noAppSince is null) _noAppSince = now;
            else if (now - _noAppSince > TimeSpan.FromSeconds(8)) { _noAppSince = null; End(s, "Ocean was closed"); return; }
            foreach (var (id, v) in s.Viewers)
            {
                if (now - v.LastBeat <= ViewerTimeout) continue;
                if (s.Viewers.TryRemove(id, out _))
                {
                    if (s.People.TryGetValue(id, out var p) && p.State == "watching") { p.State = "left"; p.At = now; }
                    _log.LogInformation("screen sharing: {Name} is gone (no report)", v.Name);
                    Changed();
                }
            }
            foreach (var p in s.People.Values)
            {
                if (p.State == "asked" && now - p.At > AskTimeout + TimeSpan.FromSeconds(10)) { p.State = "timeout"; p.At = now; Changed(); }
                else if (p.State == "accepted" && now - p.At > TimeSpan.FromSeconds(30)) { p.State = "left"; p.At = now; Changed(); } // said yes, never came
            }
            var wanted = Wanted(s);
            if (wanted != s.WantedTold) { s.WantedTold = wanted; _events.Publish("myScreen", Status()); }
            // only for invited PCs, and none of them watches, comes or may still answer: over
            if (!s.Everyone && s.Viewers.IsEmpty && !s.People.Values.Any(p => p.State is "asked" or "accepted")
                && now - s.StartedAt > TimeSpan.FromSeconds(3) && s.People.Values.All(p => now - p.At > TimeSpan.FromSeconds(2)))
            {
                var watched = s.People.Values.Any(p => p.State == "left");
                End(s, watched ? "nobody is watching any more" : "nobody watches");
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "screen tick"); }
    }

    // ------------------------------------------------------------------ the stream in (from my app)

    /// <summary>The encoder's output, as it comes. Returns when the app stops sending or the session ends.</summary>
    public async Task FeedAsync(string sessionId, Stream body, CancellationToken ct)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || s.Sid != sessionId) throw new InvalidOperationException("No such screen session.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        // what the encoder sent before is over: viewers start at a key picture of this run
        s.MinStart = s.Ring.Head;
        s.FeedOn = true;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var n = await body.ReadAsync(buffer, linked.Token);
                if (n <= 0) break;
                var offset = s.Ring.Head;
                s.Ring.Append(buffer.AsSpan(0, n));
                SendData(s, offset, buffer.AsSpan(0, n));
            }
        }
        catch (OperationCanceledException) { }
        finally { s.FeedOn = false; s.MinStart = s.Ring.Head; }
    }

    // ------------------------------------------------------------------ packets out

    private void OpenSockets(Session s)
    {
        foreach (var old in s.Multicast) old.Dispose();
        s.Multicast.Clear();
        s.Unicast?.Dispose(); s.Unicast = null;
        try
        {
            s.Unicast = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Unicast.Bind(new IPEndPoint(IPAddress.Any, 0));
            s.Unicast.SendBufferSize = 2 * 1024 * 1024;
        }
        catch (Exception ex) { _log.LogDebug("screen sharing: no UDP socket: {Msg}", ex.Message); }
        if (!s.Everyone) return;
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
                sock.SendBufferSize = 2 * 1024 * 1024;
                s.Multicast.Add(sock);
            }
            catch (Exception ex) { _log.LogDebug("screen sharing: no sender on {Addr}: {Msg}", addr, ex.Message); }
        }
        _log.LogInformation("screen sharing: packets go out on {Adapters}", s.Multicast.Count == 0 ? "no adapter (viewers get it directly)" : string.Join(", ", adapters.Select(a => $"{a.Adapter} ({a.Address})")));
    }

    private static readonly IPEndPoint GroupTarget = new(CastWire.GroupAddress, ScreenWire.Port);

    /// <summary>Under SendLock.</summary>
    private void Emit(Session s, byte[] packet)
    {
        foreach (var sock in s.Multicast) { try { sock.SendTo(packet, GroupTarget); } catch { } }
        if (s.Unicast is { } uni && !s.Everyone)
            foreach (var v in s.Viewers.Values) { if (v.Udp is { } ep) { try { uni.SendTo(packet, ep); } catch { } } }
    }

    private void SendData(Session s, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (s.SendLock)
        {
            if (s.Aes is not { } aes) return;
            if (s.NetworkChanged) { s.NetworkChanged = false; OpenSockets(s); }
            var burst = 0;
            while (bytes.Length > 0)
            {
                var n = Math.Min(CastWire.DataChunk, bytes.Length);
                var sealedData = CastWire.Seal(aes, CastWire.TypeData, s.Seq, CastWire.DataPlain(offset, bytes[..n]));
                Emit(s, CastWire.Build(s.Id, CastWire.TypeData, s.Seq, sealedData));
                s.Group.Add((s.Seq, sealedData));
                s.Seq++;
                offset += n; bytes = bytes[n..]; s.Sent += n;
                if (s.Group.Count == CastWire.Group) FlushGroup(s);
                // a key picture is a hundred packets and more: not all in one burst (Wi-Fi drops those)
                if (++burst % 24 == 0) Thread.Sleep(1);
            }
            // the end of what came in one go is the end of a picture: its parity goes with it, not with the next picture
            FlushGroup(s);
        }
    }

    private static void FlushGroupPackets(Session s, Action<byte[]> send)
    {
        if (s.Group.Count == 0) return;
        send(CastWire.Build(s.Id, CastWire.TypeParity, s.Seq, CastWire.Parity(s.Group)));
        s.Seq++;
        s.Group.Clear();
    }

    private void FlushGroup(Session s) => FlushGroupPackets(s, p => Emit(s, p));

    private void SendControl(Session s)
    {
        lock (s.SendLock)
        {
            if (s.Aes is not { } aes) return;
            if (s.NetworkChanged) { s.NetworkChanged = false; OpenSockets(s); }
            var plain = ScreenWire.ControlPlain(s.Ring.Head, Live(s).Count, s.Ended);
            Emit(s, CastWire.Build(s.Id, CastWire.TypeControl, s.Seq, CastWire.Seal(aes, CastWire.TypeControl, s.Seq, plain)));
            s.Seq++;
        }
    }

    // ------------------------------------------------------------------ for viewers (peer API)

    private Session? Find(string sessionId)
    {
        Session? s;
        lock (_lock) s = _s;
        return s is not null && !s.Ended && s.Sid == sessionId ? s : null;
    }

    /// <summary>May this PC watch the session? Null: no such session.</summary>
    public bool? Allowed(string sessionId, string clientId)
    {
        var s = Find(sessionId);
        if (s is null) return null;
        return s.Everyone || (s.People.TryGetValue(clientId, out var p) && p.State is not ("declined" or "muted"));
    }

    /// <param name="from">The address the request came from, when it came straight from the viewer (packets can go there).</param>
    public ScreenJoinDto? Join(string sessionId, string clientId, string clientName, IPAddress? from, int udpPort)
    {
        var s = Find(sessionId);
        if (s is null) return null;
        var v = s.Viewers.GetOrAdd(clientId, _ => new Viewer());
        v.Name = clientName; v.LastBeat = DateTimeOffset.UtcNow;
        v.Udp = !s.Everyone && from is not null && udpPort is > 0 and < 65536 && LocalNetworks.IsLan(from) ? new IPEndPoint(from, udpPort) : null;
        s.LastJoin = DateTimeOffset.UtcNow;
        if (s.People.TryGetValue(clientId, out var p)) { p.State = "watching"; p.At = DateTimeOffset.UtcNow; }
        _log.LogInformation("screen sharing: {Viewer} joined ({How})", clientName, s.Everyone ? "multicast" : v.Udp is null ? "direct only" : "packets to " + v.Udp);
        _events.Publish("myScreen", Status());
        return new ScreenJoinDto
        {
            SessionId = s.Sid, Title = s.Title, Width = s.Width, Height = s.Height, Fps = s.Fps, Key = Convert.ToBase64String(s.Key),
            Group = s.Everyone ? CastWire.GroupAddress.ToString() : null, Port = ScreenWire.Port, Unicast = v.Udp is not null,
        };
    }

    public ScreenStateDto Beat(string sessionId, string clientId, ScreenBeatDto beat)
    {
        var s = Find(sessionId);
        if (s is null || !s.Viewers.TryGetValue(clientId, out var v)) return new ScreenStateDto { SessionId = sessionId, Ended = true };
        var was = v.Direct;
        v.LastBeat = DateTimeOffset.UtcNow; v.Direct = beat.Direct; v.Last = beat;
        if (was != beat.Direct) _log.LogInformation("screen sharing: {Viewer} now gets it {How}", v.Name, beat.Direct ? "directly (no usable UDP)" : "by UDP");
        return new ScreenStateDto { SessionId = sessionId, Viewers = Live(s).Count };
    }

    public void Leave(string sessionId, string clientId)
    {
        var s = Find(sessionId);
        if (s is null || !s.Viewers.TryRemove(clientId, out var v)) return;
        if (s.People.TryGetValue(clientId, out var p)) { p.State = "left"; p.At = DateTimeOffset.UtcNow; }
        _log.LogInformation("screen sharing: {Viewer} left ({Udp} by UDP, {Direct} directly, {Lost} lost, {Recovered} recovered)", v.Name,
            Format.Bytes(v.Last.BytesUdp), Format.Bytes(v.Last.BytesDirect), v.Last.Lost, v.Last.Recovered);
        _events.Publish("myScreen", Status());
    }

    /// <summary>
    /// The stream from <paramref name="from"/> on, live, until the caller goes or the session ends. -1: from the
    /// latest key picture (waits for one when the encoder is just starting). <paramref name="started"/> gets the
    /// offset it really starts at, before the first byte. Returns false when those bytes are gone already.
    /// </summary>
    public async Task<bool> StreamAsync(string sessionId, string clientId, long from, long to, Func<long, Task> started, Func<ReadOnlyMemory<byte>, Task> write, CancellationToken ct)
    {
        var s = Find(sessionId);
        if (s is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        try
        {
            if (from < 0)
            {
                s.LastJoin = DateTimeOffset.UtcNow; // the app starts capturing for this viewer
                Changed();
                var until = DateTime.UtcNow.AddSeconds(20);
                while (true)
                {
                    var wait = s.Ring.More();
                    var key = s.Ring.LastKey;
                    if (key >= 0 && key >= s.MinStart) { from = key; break; }
                    if (DateTime.UtcNow > until) return false;
                    await Task.WhenAny(wait, Task.Delay(500, linked.Token));
                    linked.Token.ThrowIfCancellationRequested();
                }
            }
            await started(from);
            var buffer = new byte[64 * 1024];
            while (!linked.IsCancellationRequested && (to <= 0 || from < to))
            {
                var wait = s.Ring.More();
                var want = to > 0 ? (int)Math.Min(buffer.Length, to - from) : buffer.Length;
                var n = s.Ring.Read(from, buffer.AsSpan(0, want));
                if (n < 0) return false;
                if (n == 0)
                {
                    if (s.Ring.Closed) break;
                    await Task.WhenAny(wait, Task.Delay(2000, linked.Token));
                    continue;
                }
                await write(buffer.AsMemory(0, n));
                from += n;
                if (s.Viewers.TryGetValue(clientId, out var v)) v.LastBeat = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        return true;
    }

    public void Dispose()
    {
        Stop();
        _tick.Dispose();
        _publish.Dispose();
    }
}
