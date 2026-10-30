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
        _registry.Changed += (p, change) => _events.Publish("peer", new { id = p.Id, change = change.ToString().ToLowerInvariant(), name = p.Name, user = p.User, version = p.CatalogVersion });
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
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            // Windows: don't let ICMP "port unreachable" from a dead peer kill the socket.
            try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "discovery bind failed on UDP {Port}", port);
            return;
        }
        _log.LogInformation("discovery on UDP {Port}, node {Id}; networks: {Nets}", port, NodeIdentity.ShortId(_identity.NodeId), Net.LocalNetworks.Describe());

        var receive = ReceiveLoop(ct);
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

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var myTag = () => Realm.Tag(_config.Current.Secret);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "udp receive"); await Task.Delay(200, ct); continue; }
            try
            {
                if (r.Buffer.Length < 32 || r.Buffer.Length > 8192) continue;
                var b = Json.Deserialize<Beacon>(r.Buffer);
                if (b is null || b.V != OceanDefaults.ProtocolVersion || b.Id == _identity.NodeId) continue;
                if (b.Port <= 0 || b.Port > 65535 || string.IsNullOrEmpty(b.Id)) continue;
                if (!b.Verify()) { _log.LogDebug("beacon with bad signature from {Addr}", r.RemoteEndPoint); continue; }
                if (b.Realm != myTag()) continue; // different shared secret: invisible

                if (b.T == "bye")
                {
                    // a fresh "bye" (not a replayed one) puts the peer offline; it stays listed for a while
                    if (_registry.TryGet(b.Id, out var leaving) && b.Ts >= leaving.LastTs)
                    {
                        _log.LogInformation("peer offline (said goodbye): {Name}", b.Name);
                        _registry.MarkOffline(b.Id);
                    }
                    continue;
                }
                var change = _registry.Upsert(b, r.RemoteEndPoint.Address);

                // Reverse connection, serving side: this node can't reach us and asks us to connect out to it.
                if (b.Want is { Count: > 0 } && b.Want.Contains(_identity.NodeId)) _relayWorker.Wanted(b.Id);

                // Reverse connection, requesting side: we can't reach this node. Answer its broadcast right away:
                // Windows Firewall lets a unicast reply to a broadcast in (for ~3 s) even when it blocks everything
                // else, so our request to connect back arrives even through a strict firewall.
                if (_relay.IsWanted(b.Id))
                {
                    var reply = Build("hello");
                    try { _udp!.Send(reply, reply.Length, new IPEndPoint(r.RemoteEndPoint.Address, r.RemoteEndPoint.Port)); } catch { }
                }
                if (change is PeerChange.Added or PeerChange.Online)
                {
                    _log.LogInformation("peer {State}: {Name} ({User}@{Host}) {Addr}", change == PeerChange.Added ? "online" : "back online",
                        b.Name, b.User, b.Host, r.RemoteEndPoint.Address);
                    // answer directly so the newcomer learns about us without waiting for our next broadcast
                    var hello = Build("hello");
                    try { _udp!.Send(hello, hello.Length, new IPEndPoint(r.RemoteEndPoint.Address, _config.Current.DiscoveryPort)); } catch { }
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
