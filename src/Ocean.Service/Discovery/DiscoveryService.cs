using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Discovery;

/// <summary>
/// IPmsg-style presence: periodic signed UDP broadcasts, immediate unicast answer to newcomers,
/// "bye" on shutdown, expiry of silent peers. Only peers in the same realm are kept.
/// </summary>
public sealed class DiscoveryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(16);

    private readonly ConfigStore _config;
    private readonly NodeIdentity _identity;
    private readonly PeerRegistry _registry;
    private readonly CatalogService _catalog;
    private readonly EventHub _events;
    private readonly ILogger<DiscoveryService> _log;
    private UdpClient? _udp;
    private readonly SemaphoreSlim _announce = new(0);
    private long _seq;
    /// <summary>Beacon type of a direct answer to a node's broadcast. Older nodes treat it as a hello.</summary>
    private const string ReplyType = "reply";
    private const long ReplyEveryMs = 8000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastReply = new();

    private readonly Net.RelayHub _relay;
    private readonly Net.RelayWorker _relayWorker;

    public DiscoveryService(ConfigStore config, NodeIdentity identity, PeerRegistry registry, CatalogService catalog,
        EventHub events, Net.RelayHub relay, Net.RelayWorker relayWorker, ILogger<DiscoveryService> log)
    {
        _config = config; _identity = identity; _registry = registry; _catalog = catalog; _events = events; _log = log;
        _relay = relay; _relayWorker = relayWorker;
        _relay.WantChanged += AnnounceNow;
        // Adapter changes (Wi-Fi switched, VPN connected, cable plugged): tell everyone right away.
        Net.LocalNetworks.Changed += () =>
        {
            _log.LogInformation("network changed: {Nets}", Net.LocalNetworks.Describe());
            AnnounceNow();
        };
        _catalog.Changed += AnnounceNow;
        _config.Changed += cfg =>
        {
            var tag = Realm.Tag(cfg.Secret);
            _registry.RemoveWhere(p => p.Realm != tag);
            AnnounceNow();
        };
        _registry.Changed += (p, change) =>
        {
            // with many nodes a beacon arrives several times a second; only what changes something visible reaches the UI
            if (change != PeerChange.Seen)
                _events.Publish("peer", new { id = p.Id, change = change.ToString().ToLowerInvariant(), name = p.Name, user = p.User, version = p.CatalogVersion });
        };
        _registry.Changed += (p, change) =>
        {
            if (change == PeerChange.Offline)
                _log.LogInformation("peer offline: {Name} ({User}), last heard {Ago:0}s ago", p.Name, p.User, (DateTimeOffset.UtcNow - p.LastSeen).TotalSeconds);
            if (change is PeerChange.Offline or PeerChange.Removed) _lastReply.TryRemove(p.Id, out _);
        };
    }

    public void AnnounceNow()
    {
        if (_announce.CurrentCount == 0) _announce.Release();
    }

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
            // Wake the blocking receive every second, so stopping never waits on it.
            _udp.Client.ReceiveTimeout = 1000;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "discovery bind failed on UDP {Port}", port);
            return;
        }
        _log.LogInformation("discovery on UDP {Port}, node {Id}; networks: {Nets}", port, NodeIdentity.ShortId(_identity.NodeId), Net.LocalNetworks.Describe());

        // Receiving runs on its own thread with plain blocking calls (see ReceiveLoop).
        var receive = Task.Factory.StartNew(() => ReceiveLoop(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var send = SendLoop(ct);
        await Task.WhenAny(receive, send);
    }

    private async Task SendLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Broadcast(Build("hello")); } catch (Exception ex) { _log.LogDebug(ex, "broadcast failed"); }
            _registry.Expire(MaxAge, TimeSpan.FromHours(Math.Max(1, _config.Current.OfflineKeepHours)));
            try { await _announce.WaitAsync(Interval, ct); } catch (OperationCanceledException) { break; }
            if (ct.IsCancellationRequested) break;
        }
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

                // Answer directly ("reply", never answered itself). Broadcasts are fragile: Wi-Fi drops them without
                // retrying, and a firewall may block them while it still lets a unicast reply to the node's own
                // broadcast in (for ~3 s). Replying to each node's broadcasts (at most every few seconds) means a node
                // only drops out of a list when both directions fail, and a newcomer learns about us right away.
                // Also carries our request to connect back (Want) to a node we can't reach.
                var due = isNew || _relay.IsWanted(b.Id)
                          || (b.T != ReplyType && Environment.TickCount64 - _lastReply.GetValueOrDefault(b.Id) >= ReplyEveryMs);
                if (due)
                {
                    _lastReply[b.Id] = Environment.TickCount64;
                    var reply = Build(ReplyType);
                    try { _udp!.Send(reply, reply.Length, new IPEndPoint(r.RemoteEndPoint.Address, r.RemoteEndPoint.Port)); } catch { }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "beacon parse"); }
        }
    }

    private byte[] Build(string type)
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
        };
        _seq = b.Ts;
        b.Sign(_identity);
        return Encoding.UTF8.GetBytes(Json.Serialize(b));
    }

    private void Broadcast(byte[] payload)
    {
        var port = _config.Current.DiscoveryPort;
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var addr in LocalBroadcastAddresses()) targets.Add(addr);
        foreach (var t in targets)
        {
            try { _udp!.Send(payload, payload.Length, new IPEndPoint(t, port)); }
            catch (Exception ex) { _log.LogTrace(ex, "send to {Target}", t); }
        }
    }

    private static IEnumerable<IPAddress> LocalBroadcastAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(bc));
                }
            }
        }
        catch { }
        return list;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { Broadcast(Build("bye")); } catch { }
        await base.StopAsync(ct);
        _udp?.Dispose();
    }
}
