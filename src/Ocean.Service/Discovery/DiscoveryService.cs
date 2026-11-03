using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Discovery;

/// <summary>
/// Presence the IPmsg way: a node speaks when something happens and stays silent otherwise.
/// <list type="bullet">
/// <item>It says hello when it starts (as a question, three times over 15 s: everyone answers, so it knows the network
/// at once), when its network changes (a question too), when its catalog, shares or name change, when it needs a node
/// to connect back, and "bye" when it stops. A change also goes to each node it hears, directly: broadcasts get lost.</item>
/// <item>Between those, one heartbeat every 5 minutes. A node silent for three heartbeats is gone; any request it
/// makes counts as a sign of life (<see cref="PeerRegistry.Touch"/>), and a node that can't be connected to is asked
/// "are you there?" and taken offline when it doesn't answer within 5 s.</item>
/// <item>Answers: to questions, to newcomers, to nodes we ask to connect back, and to a heartbeat once per heartbeat
/// (so a node whose broadcasts don't reach us still knows we're here). A reply is never answered.</item>
/// <item>Older Ocean (up to 1.3.8) takes a node silent for 16 s as gone: while any is heard here, this node keeps the
/// old rhythm, a hello every 4 s, and goes quiet by itself once the last one has left or been updated.</item>
/// </list>
/// Only nodes of the same realm (shared secret) are kept.
/// </summary>
public sealed class DiscoveryService : BackgroundService
{
    /// <summary>The rhythm older Ocean needs (it takes a node silent for 16 s as gone).</summary>
    private static readonly TimeSpan LoudEvery = TimeSpan.FromSeconds(4);
    /// <summary>The heartbeat once every node heard here is quiet-capable (OCEAN_TEST_HEARTBEAT, seconds: developers only).</summary>
    private static readonly TimeSpan QuietEvery = TimeSpan.FromSeconds(OceanDefaults.EnvPort("OCEAN_TEST_HEARTBEAT", 300));
    /// <summary>
    /// Developers only: what would go to one node goes to the broadcast address instead. Several test nodes on one PC
    /// share the discovery port, and a datagram sent to that port reaches only one of their sockets.
    /// </summary>
    private static readonly bool UnicastAsBroadcast = Environment.GetEnvironmentVariable("OCEAN_TEST_UNICAST_AS_BROADCAST") == "1";
    /// <summary>Beacon type of a direct answer to a node's broadcast. Older nodes treat it as a hello.</summary>
    private const string ReplyType = "reply";
    /// <summary>An older node, heard every 4 s, is answered at most this often.</summary>
    private const long ReplyEveryMs = 8000;
    /// <summary>At most one broadcast, and one answer to a node, per second (however many changes ask for one).</summary>
    private const int MinGapMs = 1000;
    /// <summary>"Are you there?" waits this long for an answer.</summary>
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(5);

    private readonly ConfigStore _config;
    private readonly NodeIdentity _identity;
    private readonly PeerRegistry _registry;
    private readonly CatalogService _catalog;
    private readonly EventHub _events;
    private readonly Net.MeshService _mesh;
    private readonly ILogger<DiscoveryService> _log;
    private readonly Net.RelayHub _relay;
    private readonly Net.RelayWorker _relayWorker;
    private UdpClient? _udp;
    private readonly SemaphoreSlim _wake = new(0);
    private long _seq;
    private int _pendingEvent, _pendingQuestion;
    private volatile bool _quiet;
    private readonly ConcurrentDictionary<string, long> _lastReply = new();
    private readonly ConcurrentDictionary<string, long> _lastProbe = new();
    private long _lastNoLanLog = -600_000;
    /// <summary>Quiet only after the startup questions had time to be answered (older nodes would be missed before).</summary>
    private readonly long _started = Environment.TickCount64;

    public DiscoveryService(ConfigStore config, NodeIdentity identity, PeerRegistry registry, CatalogService catalog,
        EventHub events, Net.RelayHub relay, Net.RelayWorker relayWorker, Net.MeshService mesh, Peers.PeerClient client,
        ILogger<DiscoveryService> log)
    {
        _config = config; _identity = identity; _registry = registry; _catalog = catalog; _events = events; _log = log;
        _relay = relay; _relayWorker = relayWorker; _mesh = mesh;
        _relay.WantChanged += () => AnnounceNow();
        // Adapter changes (Wi-Fi switched, VPN connected, cable plugged): ask who is there now.
        Net.LocalNetworks.Changed += () =>
        {
            _log.LogInformation("network changed: {Nets}", Net.LocalNetworks.Describe());
            AnnounceNow(question: true);
        };
        _catalog.Changed += () => AnnounceNow();
        _config.Changed += cfg =>
        {
            var tag = Realm.Tag(cfg.Secret);
            _registry.RemoveWhere(p => p.Realm != tag);
            AnnounceNow();
        };
        client.DirectFailed += Probe;
        _registry.Changed += (p, change) =>
        {
            // with many nodes a beacon arrives several times a second; only what changes something visible reaches the UI
            if (change != PeerChange.Seen)
                _events.Publish("peer", new { id = p.Id, change = change.ToString().ToLowerInvariant(), name = p.Name, user = p.User, version = p.CatalogVersion });
            // an older node arriving or the last one leaving changes our rhythm
            if (change is PeerChange.Added or PeerChange.Online or PeerChange.Offline or PeerChange.Removed) Wake();
        };
        _registry.Changed += (p, change) =>
        {
            if (change == PeerChange.Offline)
                _log.LogInformation("peer offline: {Name} ({User}), last heard {Ago:0}s ago", p.Name, p.User, (DateTimeOffset.UtcNow - p.LastSeen).TotalSeconds);
            if (change is PeerChange.Offline or PeerChange.Removed) { _lastReply.TryRemove(p.Id, out _); _lastProbe.TryRemove(p.Id, out _); }
        };
        _registry.Replaced += (old, now) =>
            _log.LogInformation("peer {Name} ({User}) is back under a new identity {New} (was {Old}): the old entry is removed",
                now.Name, now.User, NodeIdentity.ShortId(now.Id), NodeIdentity.ShortId(old.Id));
    }

    /// <summary>
    /// Says hello now (at most one a second, however many changes ask). <paramref name="question"/>: every node
    /// answers (startup, network change, the app's refresh).
    /// </summary>
    public void AnnounceNow(bool question = false)
    {
        if (question) Interlocked.Exchange(ref _pendingQuestion, 1);
        Interlocked.Exchange(ref _pendingEvent, 1);
        Wake();
    }

    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var port = _config.Current.DiscoveryPort;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            if (BaseSockets.UseBaseProvider)
            {
                // experiment (OCEAN_BASE_PROVIDER=1): a socket on Windows' own provider
                _udp.Client.Dispose();
                _udp.Client = BaseSockets.Create(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            // Windows: don't let ICMP "port unreachable" from a dead peer kill the socket.
            try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
            // Wake the blocking receive now and then, so stopping never waits long on it.
            _udp.Client.ReceiveTimeout = 5000;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "discovery bind failed on UDP {Port}", port);
            return;
        }
        _log.LogInformation("discovery on UDP {Port}, node {Id}; networks: {Nets}", port, NodeIdentity.ShortId(_identity.NodeId), Net.LocalNetworks.Describe());

        // Receiving runs on its own thread with plain blocking calls (see ReceiveLoop).
        var receive = Task.Factory.StartNew(() => ReceiveLoop(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var expire = ExpireLoop(ct);
        var send = SendLoop(ct);
        await Task.WhenAny(receive, send, expire);
    }

    /// <summary>Older nodes are heard here: keep their rhythm, or they would take us for gone after 16 s of quiet.</summary>
    private bool OlderNodesAround(out string names)
    {
        var older = _registry.Snapshot().Where(p => p.Online && p.HeardDirectly && p.Hb == 0).Select(p => p.Name).ToList();
        names = string.Join(", ", older.Take(5)) + (older.Count > 5 ? $" and {older.Count - 5} more" : "");
        return older.Count > 0;
    }

    private async Task SendLoop(CancellationToken ct)
    {
        // At start: ask who is there, three times (a broadcast can be lost; answers come right away).
        _ = Task.Run(async () =>
        {
            foreach (var wait in new[] { 0, 3000, 15000 })
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
                AnnounceNow(question: true);
            }
        }, CancellationToken.None);

        var last = Environment.TickCount64 - (long)QuietEvery.TotalMilliseconds;
        while (!ct.IsCancellationRequested)
        {
            var quiet = !OlderNodesAround(out var older) && Environment.TickCount64 - _started > 20_000;
            if (quiet != _quiet)
            {
                _quiet = quiet;
                if (quiet) _log.LogInformation("discovery: quiet, a heartbeat every {Every} (every node heard here is quiet-capable)",
                    QuietEvery.TotalMinutes >= 1 ? $"{QuietEvery.TotalMinutes:0} min" : $"{QuietEvery.TotalSeconds:0} s");
                else _log.LogInformation("discovery: a hello every 4 s while an older Ocean is heard here ({Names})", older);
                Interlocked.Exchange(ref _pendingEvent, 1); // tell the new rhythm at once
            }
            var every = _quiet ? QuietEvery : LoudEvery;
            var change = Interlocked.Exchange(ref _pendingEvent, 0) == 1;
            if (change || Environment.TickCount64 - last >= every.TotalMilliseconds)
            {
                var gap = MinGapMs - (Environment.TickCount64 - last);
                if (gap > 0) { try { await Task.Delay((int)gap, ct); } catch (OperationCanceledException) { break; } }
                var question = Interlocked.Exchange(ref _pendingQuestion, 0) == 1;
                try
                {
                    var hello = Build("hello", question);
                    Broadcast(hello);
                    // a change or a question also goes to each node we hear, directly (a heartbeat needn't)
                    if (_quiet && (change || question)) SendToKnown(hello);
                }
                catch (Exception ex) { _log.LogDebug(ex, "broadcast failed"); }
                last = Environment.TickCount64;
            }
            var next = every - TimeSpan.FromMilliseconds(Environment.TickCount64 - last);
            if (next < TimeSpan.FromMilliseconds(MinGapMs)) next = TimeSpan.FromMilliseconds(MinGapMs);
            try { await _wake.WaitAsync(next, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Silent nodes go offline, long-offline ones are forgotten (no network traffic).</summary>
    private async Task ExpireLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
            try { _registry.Expire(TimeSpan.FromHours(Math.Max(1, _config.Current.OfflineKeepHours))); }
            catch (Exception ex) { _log.LogDebug(ex, "expire"); }
            var shouldBeQuiet = !OlderNodesAround(out _) && Environment.TickCount64 - _started > 20_000;
            if (_quiet != shouldBeQuiet) Wake(); // rhythm to change
        }
    }

    /// <summary>
    /// A node couldn't be connected to: is it still there? Between quiet heartbeats nothing else would notice for
    /// minutes. Asks it directly; no beacon and no request from it within 5 s means it is gone.
    /// </summary>
    private void Probe(Peer peer)
    {
        if (!_quiet || !peer.Online || !peer.HeardDirectly) return; // every 4 s anyway, or not ours to judge
        var now = Environment.TickCount64;
        if (now - _lastProbe.GetValueOrDefault(peer.Id, -60_000) < 30_000) return;
        _lastProbe[peer.Id] = now;
        var asked = DateTimeOffset.UtcNow;
        try { SendTo(Build("hello", question: true), peer); } catch (Exception ex) { _log.LogDebug(ex, "probe"); }
        _ = Task.Run(async () =>
        {
            await Task.Delay(ProbeWait);
            if (_registry.TryGet(peer.Id, out var p) && p.Online && p.HeardDirectly && p.LastSeen < asked)
            {
                _log.LogInformation("peer offline: {Name} doesn't answer (it couldn't be connected to)", p.Name);
                _registry.MarkOffline(p.Id);
            }
        });
    }

    /// <summary>
    /// Receives beacons with plain blocking calls on a dedicated thread, never overlapped (asynchronous) I/O.
    /// On some PCs, software that hooks into Windows networking (VPN clients, virtual adapters, security tools)
    /// makes an asynchronous UDP receive complete twice; .NET then kills the whole process ("'overlapped' has
    /// already been freed" on an I/O completion thread), right after startup and on every restart. Blocking
    /// receives never go through the I/O completion port, so that cannot happen.
    /// </summary>
    private void ReceiveLoop(CancellationToken ct)
    {
        var myTag = () => Realm.Tag(_config.Current.Secret);
        var buffer = new byte[8192];
        // OCEAN_TEST_DEAF_TO (developers only): node id prefixes whose beacons are ignored, which looks exactly like a
        // PC on another network. Used to test reaching a node through another one (Via) on one machine.
        var deafTo = (Environment.GetEnvironmentVariable("OCEAN_TEST_DEAF_TO") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var n = _udp!.Client.ReceiveFrom(buffer, ref from);
                r = new UdpReceiveResult(buffer.AsSpan(0, n).ToArray(), (IPEndPoint)from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug(ex, "udp receive"); Thread.Sleep(200); continue; }
            catch { break; }
            try
            {
                if (r.Buffer.Length < 32 || r.Buffer.Length > 8192) continue;
                // local network only: a beacon from the internet or through a VPN adapter is never answered
                if (!Net.LocalNetworks.IsLan(r.RemoteEndPoint.Address)) { Net.LanOnly.Refused("beacon from", r.RemoteEndPoint.Address); continue; }
                var b = Json.Deserialize<Beacon>(r.Buffer);
                if (b is null || b.V != OceanDefaults.ProtocolVersion || b.Id == _identity.NodeId) continue;
                if (b.Port <= 0 || b.Port > 65535 || string.IsNullOrEmpty(b.Id)) continue;
                if (deafTo.Length > 0 && deafTo.Any(d => b.Id.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
                if (!b.Verify()) { _log.LogDebug("beacon with bad signature from {Addr}", r.RemoteEndPoint); continue; }
                if (b.Realm != myTag()) continue; // different shared secret: invisible

                if (b.T == "bye")
                {
                    // a fresh "bye" (not a replayed one) puts the peer offline; it stays listed for a while
                    if (_registry.TryGet(b.Id, out var leaving) && leaving.Online && b.Ts >= leaving.LastTs)
                    {
                        _log.LogInformation("peer offline (said goodbye): {Name}", b.Name);
                        _registry.MarkOffline(b.Id);
                    }
                    continue;
                }
                var change = _registry.Upsert(b, r.RemoteEndPoint.Address);

                // Reverse connection, serving side: this node can't reach us and asks us to connect out to it.
                if (b.Want is { Count: > 0 } && b.Want.Contains(_identity.NodeId)) _relayWorker.Wanted(b.Id);

                var isNew = change is PeerChange.Added or PeerChange.Online;
                if (isNew)
                    _log.LogInformation("peer {State}: {Name} ({User}@{Host}) {Addr}", change == PeerChange.Added ? "online" : "back online",
                        b.Name, b.User, b.Host, r.RemoteEndPoint.Address);

                // Answer directly ("reply"), but never a reply: two nodes that each wanted the other to connect back
                // used to answer each other's replies without end (20 Mbps on a busy network). Answered: a question,
                // a newcomer, a node we ask to connect back (it carries our request), and otherwise a node's hello once
                // per its rhythm (every 8 s for an older node, once per heartbeat for a quiet one), so a node whose
                // broadcasts don't reach us still hears from us.
                var since = Environment.TickCount64 - _lastReply.GetValueOrDefault(b.Id);
                var every = b.Hb <= 4 ? ReplyEveryMs : (long)(b.Hb * 1000 * 0.9);
                var due = b.T != ReplyType && since >= MinGapMs
                          && (b.Q == true || isNew || _relay.IsWanted(b.Id) || since >= every);
                if (due)
                {
                    _lastReply[b.Id] = Environment.TickCount64;
                    var reply = Build(ReplyType);
                    try
                    {
                        if (UnicastAsBroadcast) Broadcast(reply);
                        else _udp!.Send(reply, reply.Length, new IPEndPoint(r.RemoteEndPoint.Address, r.RemoteEndPoint.Port));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "beacon parse"); }
        }
    }

    private byte[] Build(string type, bool question = false)
    {
        var cfg = _config.Current;
        var b = new Beacon
        {
            T = type,
            Name = cfg.NodeName,
            User = cfg.UserName,
            Host = Environment.MachineName,
            // OCEAN_TEST_ADVERTISE_PORT (developers only) announces a port nobody listens on, which looks exactly like
            // a PC whose firewall blocks incoming connections: used to test the reverse connection on one machine.
            Port = OceanDefaults.EnvPort("OCEAN_TEST_ADVERTISE_PORT", cfg.PeerPort),
            Realm = Realm.Tag(cfg.Secret),
            Cat = _catalog.Version,
            Shares = cfg.SharingPaused ? 0 : cfg.Shares.Count,
            Want = _relay.WantedPeers() is { Count: > 0 } want ? want : null,
            Ts = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Interlocked.Increment(ref _seq)),
            Hb = (int)(_quiet ? QuietEvery : LoudEvery).TotalSeconds,
            Nrev = _mesh.Revision(),
            Q = question ? true : null,
        };
        _seq = b.Ts;
        b.Sign(_identity);
        return Encoding.UTF8.GetBytes(Json.Serialize(b));
    }

    /// <summary>
    /// Sends the beacon to the broadcast address of each local network (e.g. 192.168.1.255), which Windows sends out
    /// of that network's own adapter. Never to 255.255.255.255: Windows sends that one where its default route
    /// points, which is the VPN adapter while a VPN (Astrill...) is connected. VPN adapters are skipped altogether.
    /// </summary>
    private void Broadcast(byte[] payload)
    {
        var port = _config.Current.DiscoveryPort;
        var targets = Net.LocalNetworks.LanBroadcasts();
        if (targets.Count == 0 && Environment.TickCount64 - _lastNoLanLog > 600_000)
        {
            _lastNoLanLog = Environment.TickCount64;
            _log.LogInformation("discovery: no local network to announce on ({Nets})", Net.LocalNetworks.Describe());
        }
        foreach (var (t, _) in targets)
        {
            try { _udp!.Send(payload, payload.Length, new IPEndPoint(t, port)); }
            catch (Exception ex) { _log.LogTrace(ex, "send to {Target}", t); }
        }
    }

    /// <summary>To one node, on its last heard address (local network only).</summary>
    private void SendTo(byte[] payload, Peer peer)
    {
        if (UnicastAsBroadcast) { Broadcast(payload); return; }
        var to = peer.DisplayAddress;
        if (to.Equals(IPAddress.None) || !Net.LocalNetworks.IsLan(to)) return;
        try { _udp!.Send(payload, payload.Length, new IPEndPoint(to, _config.Current.DiscoveryPort)); }
        catch (Exception ex) { _log.LogTrace(ex, "send to {Peer}", peer.Name); }
    }

    /// <summary>To each node we hear, directly: a change or a question must not depend on one broadcast arriving.</summary>
    private void SendToKnown(byte[] payload)
    {
        if (UnicastAsBroadcast) return; // the broadcast already went to everyone
        foreach (var p in _registry.Snapshot())
            if (p.Online && p.HeardDirectly) SendTo(payload, p);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try
        {
            var bye = Build("bye");
            Broadcast(bye);
            if (_quiet) SendToKnown(bye); // nobody would notice for minutes otherwise
            Broadcast(bye);
        }
        catch { }
        await base.StopAsync(ct);
        _udp?.Dispose();
    }
}
