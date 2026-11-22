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
/// The computer's sound (Opus frames from the app, while the user has it on) goes the same ways: its own packets,
/// each with the frame before it, and the direct connection (<c>/p/screen/{id}/sound</c>) for viewers without UDP.
/// </summary>
public sealed class ScreenHost : IDisposable
{
    /// <summary>
    /// A viewer that goes silent (no report, no stream) counts as gone after this long; one that leaves says so at
    /// once. Long enough for its path to come back (a PC in between restarting, Wi-Fi roaming) without ending the
    /// sharing; the viewer itself gives up after 40 s.
    /// </summary>
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(45);
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
        public volatile bool Sound;
        public DateTimeOffset StartedAt;
        public readonly ScreenRing Ring = new();
        public readonly ScreenSoundRing SoundRing = new();
        public ScreenBoard Board = null!;
        // the sound goes out on its own (under SoundLock), never waiting behind a picture: its own numbers (the
        // nonce has the packet's type in it, so they never meet the picture's) and its own cipher
        public readonly object SoundLock = new();
        public AesGcm? SoundAes;
        public uint SoundSeq;
        public ScreenSoundFrames.Frame? LastSound;
        public long SoundSent;
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
        // (replaced whole when the network changes: the sound sends on them without SendLock)
        public volatile Socket[] Multicast = Array.Empty<Socket>();
        public volatile Socket? Unicast;
        /// <summary>Pacing a large picture out (a key picture is hundreds of packets): when the current burst began and its bytes.</summary>
        public long PaceStart, PaceBytes;
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
                Everyone = req.Everyone, Sound = req.Sound, StartedAt = DateTimeOffset.UtcNow,
            };
            s.Aes = new AesGcm(s.Key, 16);
            s.SoundAes = new AesGcm(s.Key, 16);
            s.Board = new ScreenBoard(s.Sid);
            _s = s;
        }
        _log.LogInformation("screen sharing started: {Title} ({W}x{H}, {Fps} fps{Sound}) for {Who}", s.Title, s.Width, s.Height, s.Fps, s.Sound ? ", with sound" : "", s.Everyone ? "everyone" : "invited PCs");
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

    /// <summary>The computer's sound on or off: viewers hear of it at once (a control packet) and on their next report.</summary>
    public MyScreenDto SetSound(bool on)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null) return new MyScreenDto { Error = "You are not sharing your screen." };
        if (s.Sound != on)
        {
            s.Sound = on;
            lock (s.SoundLock) s.LastSound = null;
            _log.LogInformation("screen sharing: sound {OnOff}", on ? "on" : "off");
            SendControl(s);
        }
        var st = Status();
        _events.Publish("myScreen", st);
        return st;
    }

    private void End(Session s, string why)
    {
        lock (_lock) { if (!ReferenceEquals(_s, s)) return; _s = null; }
        s.Ended = true;
        _log.LogInformation("screen sharing ended ({Why}): {Title} after {Min:0.0} min, {Sent} sent{Sound}", why, s.Title, (DateTimeOffset.UtcNow - s.StartedAt).TotalMinutes, Format.Bytes(s.Sent),
            s.SoundSent > 0 ? $" (and {Format.Bytes(s.SoundSent)} of sound)" : "");
        // viewers: told by packets (a few times), by their next report and by the stream ending
        for (var i = 0; i < 3; i++) SendControl(s);
        s.Ring.Close();
        s.SoundRing.Close();
        s.Board.Close();
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
            var multi = s.Multicast; s.Multicast = Array.Empty<Socket>();
            foreach (var sock in multi) sock.Dispose();
            s.Unicast?.Dispose(); s.Unicast = null;
            s.Aes?.Dispose(); s.Aes = null;
        }
        lock (s.SoundLock) { s.SoundAes?.Dispose(); s.SoundAes = null; }
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
            Viewers = live.Count, People = people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), StartedAt = s.StartedAt, Wanted = Wanted(s), Sound = s.Sound,
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

    /// <summary>The sound (Opus frames, see <see cref="ScreenSoundFrames"/>), as it comes. Returns when the app stops sending or the session ends.</summary>
    public async Task SoundFeedAsync(string sessionId, Stream body, CancellationToken ct)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || s.Sid != sessionId) throw new InvalidOperationException("No such screen session.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        var header = new byte[ScreenSoundFrames.HeaderSize];
        try
        {
            while (await ScreenSoundFrames.ReadAsync(body, header, linked.Token) is { } f)
            {
                if (!s.Sound) continue; // switched off: whatever was still on its way stays here
                s.SoundRing.Add(f);
                SendSound(s, f);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
    }

    // ------------------------------------------------------------------ packets out

    /// <summary>Under SendLock. The new sockets replace the old ones whole (the sound may be sending on them meanwhile).</summary>
    private void OpenSockets(Session s)
    {
        var oldMulti = s.Multicast; var oldUni = s.Unicast;
        s.Multicast = Array.Empty<Socket>(); s.Unicast = null;
        foreach (var old in oldMulti) old.Dispose();
        oldUni?.Dispose();
        try
        {
            var uni = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            uni.Bind(new IPEndPoint(IPAddress.Any, 0));
            uni.SendBufferSize = 2 * 1024 * 1024;
            s.Unicast = uni;
        }
        catch (Exception ex) { _log.LogDebug("screen sharing: no UDP socket: {Msg}", ex.Message); }
        if (!s.Everyone) return;
        var multi = new List<Socket>();
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
                multi.Add(sock);
            }
            catch (Exception ex) { _log.LogDebug("screen sharing: no sender on {Addr}: {Msg}", addr, ex.Message); }
        }
        s.Multicast = multi.ToArray();
        _log.LogInformation("screen sharing: packets go out on {Adapters}", multi.Count == 0 ? "no adapter (viewers get it directly)" : string.Join(", ", adapters.Select(a => $"{a.Adapter} ({a.Address})")));
    }

    private static readonly IPEndPoint GroupTarget = new(CastWire.GroupAddress, ScreenWire.Port);

    /// <summary>To the group, or straight to each viewer that gets packets. (A UDP socket may be sent on from two threads at once.)</summary>
    private static void Emit(Session s, byte[] packet)
    {
        foreach (var sock in s.Multicast) { try { sock.SendTo(packet, GroupTarget); } catch { } }
        if (s.Unicast is { } uni && !s.Everyone)
            foreach (var v in s.Viewers.Values) { if (v.Udp is { } ep) { try { uni.SendTo(packet, ep); } catch { } } }
    }

    /// <summary>
    /// A large picture goes out at most this fast (bytes a second) after a first burst: hundreds of packets at
    /// once overrun a Wi-Fi access point and are lost. 20 MB/s (160 Mbit/s) is far above any picture's rate, so a
    /// key picture of 300 KB takes about 15 ms. The wait is exact (a sleep of 1 ms is up to 15.6 ms on Windows).
    /// </summary>
    private const double PaceRate = 20_000_000;
    private const int PaceBurst = 32 * 1024;

    private static void Pace(Session s, int bytes)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        // a new picture after a pause starts a new burst
        if ((now - s.PaceStart) / freq > Math.Max(0.02, s.PaceBytes / PaceRate + 0.005)) { s.PaceStart = now; s.PaceBytes = 0; }
        s.PaceBytes += bytes;
        if (s.PaceBytes <= PaceBurst) return;
        var due = s.PaceStart + (long)((s.PaceBytes - PaceBurst) / PaceRate * freq);
        while (true)
        {
            var left = (due - System.Diagnostics.Stopwatch.GetTimestamp()) / freq;
            if (left <= 0) return;
            if (left > 0.003) Thread.Sleep(1);
            else if (left > 0.0002) Thread.Yield();
            else Thread.SpinWait(20);
        }
    }

    private void SendData(Session s, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (s.SendLock)
        {
            if (s.Aes is not { } aes) return;
            if (s.NetworkChanged) { s.NetworkChanged = false; OpenSockets(s); }
            while (bytes.Length > 0)
            {
                var n = Math.Min(CastWire.DataChunk, bytes.Length);
                var sealedData = CastWire.Seal(aes, CastWire.TypeData, s.Seq, CastWire.DataPlain(offset, bytes[..n]));
                var packet = CastWire.Build(s.Id, CastWire.TypeData, s.Seq, sealedData);
                // a key picture is hundreds of packets: not all in one burst (Wi-Fi drops those)
                Pace(s, packet.Length);
                Emit(s, packet);
                s.Group.Add((s.Seq, sealedData));
                s.Seq++;
                offset += n; bytes = bytes[n..]; s.Sent += n;
                if (s.Group.Count == CastWire.Group) FlushGroup(s);
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

    /// <summary>
    /// A sound frame as one packet, with the frame before it (no parity: a lost packet is in the next one). It goes
    /// at once, never behind a picture being paced out (its own lock, numbers and cipher).
    /// </summary>
    private void SendSound(Session s, ScreenSoundFrames.Frame f)
    {
        lock (s.SoundLock)
        {
            if (s.SoundAes is not { } aes) return;
            var before = s.LastSound is { } b && b.Seq + 1 == f.Seq ? b : (ScreenSoundFrames.Frame?)null;
            var plain = ScreenWire.SoundPlain(f, before);
            var seq = s.SoundSeq++;
            Emit(s, CastWire.Build(s.Id, ScreenWire.TypeSound, seq, CastWire.Seal(aes, ScreenWire.TypeSound, seq, plain)));
            s.LastSound = f;
            s.SoundSent += f.Data.Length;
        }
    }

    private void SendControl(Session s)
    {
        lock (s.SendLock)
        {
            if (s.Aes is not { } aes) return;
            if (s.NetworkChanged) { s.NetworkChanged = false; OpenSockets(s); }
            var plain = ScreenWire.ControlPlain(s.Ring.Head, Live(s).Count, s.Ended, s.Sound, s.SoundRing.Newest);
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
            Group = s.Everyone ? CastWire.GroupAddress.ToString() : null, Port = ScreenWire.Port, Unicast = v.Udp is not null, Sound = s.Sound, Board = true,
        };
    }

    public ScreenStateDto Beat(string sessionId, string clientId, ScreenBeatDto beat)
    {
        var s = Find(sessionId);
        if (s is null || !s.Viewers.TryGetValue(clientId, out var v)) return new ScreenStateDto { SessionId = sessionId, Ended = true };
        var was = v.Direct;
        v.LastBeat = DateTimeOffset.UtcNow; v.Direct = beat.Direct; v.Last = beat;
        if (was != beat.Direct) _log.LogInformation("screen sharing: {Viewer} now gets it {How}", v.Name, beat.Direct ? "directly (no usable UDP)" : "by UDP");
        return new ScreenStateDto { SessionId = sessionId, Viewers = Live(s).Count, Sound = s.Sound };
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
    /// A viewer whose stream is this far behind the live picture (a slow way, a PC in between) is moved on to the
    /// latest key picture rather than falling further behind: a live picture that comes ever later is worse than
    /// one that skips a moment.
    /// </summary>
    private static readonly TimeSpan BehindLimit = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The stream from <paramref name="from"/> on, live, until the caller goes or the session ends. -1: from the
    /// latest key picture (waits for one when the encoder is just starting). <paramref name="started"/> gets the
    /// offset it really starts at, before the first byte. Returns false when those bytes are gone already, or are
    /// so far behind (<see cref="BehindLimit"/>) that the viewer should start again at the latest key picture; a
    /// stream that falls that far behind ends (the viewer asks again from where it was and is told so).
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
            else if (to <= 0 && FarBehind(s, from)) return false;
            await started(from);
            var buffer = new byte[64 * 1024];
            var checkedAt = DateTime.UtcNow;
            while (!linked.IsCancellationRequested && (to <= 0 || from < to))
            {
                if (to <= 0 && DateTime.UtcNow - checkedAt > TimeSpan.FromMilliseconds(250))
                {
                    checkedAt = DateTime.UtcNow;
                    if (FarBehind(s, from)) { _log.LogInformation("screen sharing: a viewer's picture is {Behind:0.0} s behind; on to the latest key picture", s.Ring.AgeOf(from).TotalSeconds); return true; }
                }
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

    /// <summary>Behind the live picture by more than <see cref="BehindLimit"/>, with a later key picture to go on from.</summary>
    private static bool FarBehind(Session s, long from) => s.Ring.LastKey > from && s.Ring.AgeOf(from) > BehindLimit;

    /// <summary>A sound frame without sound: "still here" while the sharing PC is silent (no frames come then).</summary>
    public static readonly ScreenSoundFrames.Frame KeepAlive = new(0, 0, 0, Array.Empty<byte>());

    /// <summary>
    /// The sound over the peer connection, from the frame after <paramref name="after"/> (-1: from about now), live,
    /// until the caller goes or the session ends. Every viewer has it (alongside the packets, when they come: each
    /// frame then arrives twice, and the first counts). <paramref name="keepAlive"/>: a frame without sound every
    /// second while nothing is played (the sharing PC sends no frames then), so silence isn't taken for a broken way.
    /// </summary>
    public async Task<bool> StreamSoundAsync(string sessionId, string clientId, long after, bool keepAlive, Func<IReadOnlyList<ScreenSoundFrames.Frame>, Task> write, CancellationToken ct)
    {
        var s = Find(sessionId);
        if (s is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        // from about now: the last 100 ms (the viewer drops what is too late to play)
        var last = after >= 0 ? after : Math.Max(-1, s.SoundRing.Newest - 5);
        try
        {
            while (!linked.IsCancellationRequested && !s.SoundRing.Closed)
            {
                var wait = s.SoundRing.More();
                var frames = s.SoundRing.After(last);
                if (frames.Count > 0)
                {
                    await write(frames);
                    last = frames[^1].Seq;
                    if (s.Viewers.TryGetValue(clientId, out var v)) v.LastBeat = DateTimeOffset.UtcNow;
                    continue;
                }
                var idle = Task.Delay(keepAlive ? 1000 : 2000, linked.Token);
                await Task.WhenAny(wait, idle);
                if (keepAlive && !wait.IsCompleted && idle.IsCompleted) await write(new[] { KeepAlive });
            }
        }
        catch (OperationCanceledException) { }
        return true;
    }

    // ------------------------------------------------------------------ the board and pointing

    /// <summary>The board of my session (<paramref name="sessionId"/> null: whatever runs now). Null: no such session.</summary>
    public BoardDto? BoardState(string? sessionId)
    {
        Session? s;
        lock (_lock) s = _s;
        return s is null || s.Ended || (sessionId is not null && s.Sid != sessionId) ? null : s.Board.State();
    }

    /// <summary>A change to my session's board, by me (host) or by a viewer. Everybody hears of it (my app at once, viewers on their stream).</summary>
    public BoardOpDto BoardApply(string? sessionId, BoardOpDto op, string byId, string byName, bool host)
    {
        Session? s;
        lock (_lock) s = _s;
        if (s is null || s.Ended || (sessionId is not null && s.Sid != sessionId)) return new BoardOpDto { Op = op.Op, Error = "the sharing is over" };
        var done = s.Board.Apply(op, byId, byName, host);
        if (done.Error is null) _events.Publish("screenBoard", done);
        else _log.LogDebug("screen board: refused from {Name}: {Why}", byName, done.Error);
        return done;
    }

    /// <summary>
    /// The board's changes after version <paramref name="since"/>, live, until the caller goes or the session ends.
    /// False when those changes are no longer kept (the viewer starts over from the whole board).
    /// </summary>
    public async Task<bool> StreamBoardAsync(string sessionId, string clientId, long since, Func<IReadOnlyList<BoardOpDto>, Task> write, CancellationToken ct)
    {
        var s = Find(sessionId);
        if (s is null) return true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token);
        try
        {
            while (!linked.IsCancellationRequested && !s.Board.Closed)
            {
                var wait = s.Board.More();
                var ops = s.Board.Since(since);
                if (ops is null) return false;
                if (ops.Count > 0)
                {
                    await write(ops);
                    since = ops[^1].Rev;
                    continue;
                }
                await Task.WhenAny(wait, Task.Delay(5_000, linked.Token));
                if (!wait.IsCompleted) await write(Array.Empty<BoardOpDto>()); // (a keep-alive every 5 s: an empty line; the viewer counts on it)
            }
        }
        catch (OperationCanceledException) { }
        return true;
    }

    /// <summary>A viewer points at my shared picture: my app shows it on my screen.</summary>
    public bool Point(string sessionId, string clientId, string clientName, ScreenPointDto point)
    {
        var s = Find(sessionId);
        if (s is null || !s.Viewers.ContainsKey(clientId)) return false;
        point.PeerId = clientId; point.PeerName = clientName;
        point.X = Math.Clamp(point.X, 0, 1); point.Y = Math.Clamp(point.Y, 0, 1);
        if (point.Kind is not (ScreenPointDto.Click or ScreenPointDto.Move or ScreenPointDto.Up)) point.Kind = ScreenPointDto.Click;
        _events.Publish("screenPoint", point);
        return true;
    }

    public void Dispose()
    {
        Stop();
        _tick.Dispose();
        _publish.Dispose();
    }
}
