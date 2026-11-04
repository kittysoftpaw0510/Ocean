using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Ocean.Core.Protocol;

namespace Ocean.Service.Discovery;

public sealed class Peer
{
    private static readonly TimeSpan AddressMaxAge = TimeSpan.FromSeconds(60);

    public string Id { get; init; } = "";
    public string PublicKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Address the most recent beacon came from.</summary>
    public IPAddress Address { get; set; } = IPAddress.None;
    public int Port { get; set; }
    public string Realm { get; set; } = "";
    public long CatalogVersion { get; set; }
    public int ShareCount { get; set; }
    public long LastTs { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>False while the node is silent; it stays listed (with its last known shares) for a while.</summary>
    public bool Online { get; set; } = true;
    public DateTimeOffset? OfflineSince { get; set; }
    /// <summary>Direct connections fail (its firewall): talk through the reverse connection it opens to us.</summary>
    public bool PreferRelay { get; set; }
    /// <summary>While relaying, when to try the direct route again.</summary>
    public DateTimeOffset DirectRetryAt { get; set; }

    /// <summary>When its own beacon was last heard here (a direct route exists).</summary>
    public DateTimeOffset DirectSeen { get; set; }

    /// <summary>Seconds between its heartbeats, as it announced them (0: an Ocean up to 1.3.8, every 4 s).</summary>
    public int Hb { get; set; }
    /// <summary>Fingerprint of the nodes it hears (its bridge list), from its last beacon.</summary>
    public string? Nrev { get; set; }

    /// <summary>
    /// How long it may stay silent before it counts as gone: 16 s for a node announcing every 4 s (and any older
    /// Ocean), three heartbeats and a margin for a quiet one (15.5 min at the usual 5 min).
    /// </summary>
    public TimeSpan Silence => Hb <= 4 ? TimeSpan.FromSeconds(16) : TimeSpan.FromSeconds(3 * Hb + 30);

    /// <summary>
    /// Nodes this one can be reached through when there is no network path to it from here (it is never heard
    /// directly, but nodes we do hear can reach it), by bridge id. Several can be known at once: requests go to the
    /// least busy one and move to another when one fails. Empty for nodes heard directly.
    /// </summary>
    public ConcurrentDictionary<string, BridgeRoute> Bridges { get; } = new();

    /// <summary>Its own beacons arrive: the direct route is what we go by.</summary>
    public bool HeardDirectly => DateTimeOffset.UtcNow - DirectSeen < Silence;

    /// <summary>Reached only through other nodes.</summary>
    public bool Bridged => !HeardDirectly && LiveBridges().Count > 0;

    /// <summary>Bridges confirmed recently, least busy first.</summary>
    public List<BridgeRoute> LiveBridges()
    {
        var now = DateTimeOffset.UtcNow;
        return Bridges.Values.Where(b => now - b.Confirmed < b.Fresh)
                      .OrderBy(b => b.Load).ThenByDescending(b => b.Confirmed).ToList();
    }

    /// <summary>Name of the bridge in use (for display); null for nodes heard directly.</summary>
    public string? Via => Bridged ? LiveBridges()[0].Name : null;

    /// <summary>PCs between us and this node: 0 = heard directly, 1 = through a bridge (never more).</summary>
    public int Hops => Bridged ? 1 : 0;

    /// <summary>
    /// Every address this peer was recently heard on. A PC with several adapters (Wi-Fi + Ethernet, VPN or
    /// virtual switches) is heard on each of them, and some of those addresses may not be reachable from here.
    /// </summary>
    public ConcurrentDictionary<IPAddress, DateTimeOffset> Addresses { get; } = new();

    /// <summary>Address of the last successful connection; tried first.</summary>
    public IPAddress? Preferred { get; set; }

    public void Seen(IPAddress address, DateTimeOffset now)
    {
        Address = address;
        Addresses[address] = now;
        foreach (var kv in Addresses)
            if (now - kv.Value > AddressMaxAge && !kv.Key.Equals(Preferred)) Addresses.TryRemove(kv.Key, out _);
    }

    /// <summary>
    /// Addresses to try, best first: the last one that worked, then addresses on the same subnet as one of our
    /// physical adapters (the real LAN), then other local subnets (virtual switches), then the most recently heard.
    /// Only addresses on the local network (an address can stop being one when this PC changes networks).
    /// </summary>
    public List<IPAddress> Candidates()
    {
        var list = Addresses.Where(kv => Net.LocalNetworks.IsLan(kv.Key))
                            .OrderBy(kv => kv.Key.Equals(Preferred) ? 0 : 1)
                            .ThenByDescending(kv => Net.LocalNetworks.Rank(kv.Key))
                            .ThenBy(kv => IsLikelyLan(kv.Key) ? 0 : 1)
                            .ThenByDescending(kv => kv.Value)
                            .Select(kv => kv.Key).ToList();
        if (list.Count == 0 && !Address.Equals(IPAddress.None)) list.Add(Address);
        return list;
    }

    public string UrlFor(IPAddress a)
        => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"https://[{a}]:{Port}" : $"https://{a}:{Port}";

    public string BaseUrl => UrlFor(Candidates().FirstOrDefault() ?? Address);

    public IPAddress DisplayAddress => Preferred ?? Candidates().FirstOrDefault() ?? Address;

    /// <summary>RFC 1918 home/office ranges; virtual adapters (e.g. 198.18/15 benchmark range used by VPN/TUN tools) rank lower.</summary>
    private static bool IsLikelyLan(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}

/// <summary>One way to reach a node we can't hear: through this bridge.</summary>
public sealed class BridgeRoute
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    /// <summary>When the bridge last listed the node.</summary>
    public DateTimeOffset Confirmed { get; set; }
    /// <summary>Transfers the bridge was passing on at that time.</summary>
    public int Load { get; set; }
    /// <summary>The node's catalog version as the bridge reported it.</summary>
    public long Cat { get; set; }
    /// <summary>
    /// How long the route counts without a new word from the bridge: 75 s for a bridge that is asked every 30 s (an
    /// older Ocean), three of its heartbeats and a margin for a quiet one (it confirms its routes with each heartbeat).
    /// </summary>
    public TimeSpan Fresh { get; set; } = PeerRegistry.BridgeFresh;
}

/// <summary><see cref="Seen"/>: a beacon that changed nothing visible (not worth telling the UI about).</summary>
public enum PeerChange { Added, Updated, VersionChanged, Offline, Online, Removed, Seen }

/// <summary>
/// Known peers of the same realm: online ones (fed by discovery beacons) and recently seen offline ones,
/// which stay listed for a while so their last known shares remain visible.
/// </summary>
public sealed class PeerRegistry
{
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public event Action<Peer, PeerChange>? Changed;

    public IReadOnlyCollection<Peer> Snapshot() => _peers.Values.ToList();

    public bool TryGet(string id, out Peer peer) => _peers.TryGetValue(id, out peer!);

    /// <summary>True only for peers that can be reached right now.</summary>
    public bool TryGetOnline(string id, out Peer peer) => _peers.TryGetValue(id, out peer!) && peer.Online;

    public int Count => _peers.Count;
    public int OnlineCount => _peers.Values.Count(p => p.Online);

    /// <summary>Restores a peer remembered from an earlier run (shown offline until it is heard again).</summary>
    public void AddKnownOffline(Peer peer)
    {
        peer.Online = false;
        peer.OfflineSince ??= peer.LastSeen;
        _peers.TryAdd(peer.Id, peer);
    }

    /// <summary>
    /// The node talked to us (an authenticated request): it is there, whatever its beacons do. Keeps quiet nodes listed
    /// between heartbeats while they are in use, and nodes whose broadcasts don't reach us.
    /// </summary>
    public void Touch(string id)
    {
        if (_peers.TryGetValue(id, out var p) && p.Online) lock (p) p.LastSeen = DateTimeOffset.UtcNow;
    }

    public void MarkOffline(string id)
    {
        if (!_peers.TryGetValue(id, out var p) || !p.Online) return;
        lock (p) { p.Online = false; p.OfflineSince = DateTimeOffset.UtcNow; p.Preferred = null; }
        Changed?.Invoke(p, PeerChange.Offline);
        DropBridge(id); // nodes we reached only through it may now be out of reach too
    }

    /// <summary>Applies a verified beacon and reports what changed (Added / Online = newly reachable).</summary>
    public PeerChange Upsert(Beacon b, IPAddress address)
    {
        var now = DateTimeOffset.UtcNow;
        var isNew = false;
        PeerChange change = PeerChange.Seen;
        var peer = _peers.AddOrUpdate(b.Id,
            _ =>
            {
                isNew = true;
                change = PeerChange.Added;
                var p = new Peer
                {
                    Id = b.Id, PublicKey = b.Pk, Name = b.Name, User = b.User, Host = b.Host, Port = b.Port,
                    Realm = b.Realm, CatalogVersion = b.Cat, ShareCount = b.Shares, LastTs = b.Ts, FirstSeen = now, LastSeen = now,
                    DirectSeen = now, Hb = b.Hb, Nrev = b.Nrev,
                };
                p.Seen(address, now);
                return p;
            },
            (_, p) =>
            {
                // Under contention ConcurrentDictionary may run the add factory, lose the race and then update:
                // whatever the factory claimed ("new") doesn't hold any more.
                isNew = false; change = PeerChange.Seen;
                lock (p)
                {
                    // Only fresh beacons count (a replayed old one must neither revive nor change a peer). The same
                    // beacon arrives once per adapter with the same timestamp, so every address is still learned.
                    if (b.Ts < p.LastTs) { change = PeerChange.Seen; return p; }
                    p.Seen(address, now);
                    p.LastSeen = now;
                    p.DirectSeen = now; // heard directly: bridges (if any) are no longer used
                    if (!p.Online) { p.Online = true; p.OfflineSince = null; change = PeerChange.Online; }
                    else if (p.CatalogVersion != b.Cat) change = PeerChange.VersionChanged;
                    else if (p.Name != b.Name || p.User != b.User || p.Host != b.Host || p.Port != b.Port || p.ShareCount != b.Shares) change = PeerChange.Updated;
                    p.Name = b.Name; p.User = b.User; p.Host = b.Host; p.Port = b.Port;
                    p.Realm = b.Realm; p.CatalogVersion = b.Cat; p.ShareCount = b.Shares; p.LastTs = b.Ts;
                    p.Hb = b.Hb; p.Nrev = b.Nrev;
                }
                return p;
            });
        Changed?.Invoke(peer, change);
        return isNew ? PeerChange.Added : change;
    }

    /// <summary>A node's own beacons count as a direct route as long as it may stay silent (<see cref="Peer.Silence"/>).</summary>
    /// <summary>
    /// A bridge that hasn't listed the node for this long is no longer used for it. Bridges are asked every 30 s
    /// (MeshService), so this tolerates one missed answer. A bridge that goes offline drops its routes at once.
    /// </summary>
    public static readonly TimeSpan BridgeFresh = TimeSpan.FromSeconds(75);

    /// <summary>
    /// <paramref name="via"/> reports that it hears <paramref name="n"/>: remember it as one way to reach that node.
    /// A node heard directly keeps its own data; otherwise the report updates it and brings it online.
    /// </summary>
    public void ConfirmBridge(NeighborDto n, Peer via, int load, string realm)
    {
        var now = DateTimeOffset.UtcNow;
        var route = new BridgeRoute
        {
            Id = via.Id, Name = Core.DisplayNames.Of(via.User, via.Name), Confirmed = now, Load = load, Cat = n.Cat,
            Fresh = via.Hb > 4 ? TimeSpan.FromSeconds(3 * via.Hb + 30) : BridgeFresh,
        };
        PeerChange? change = null;
        var peer = _peers.AddOrUpdate(n.Id,
            _ =>
            {
                change = PeerChange.Added;
                var p = new Peer
                {
                    Id = n.Id, PublicKey = n.Pk, Name = n.Name, User = n.User, Host = n.Host, Realm = realm,
                    CatalogVersion = n.Cat, ShareCount = n.Shares, FirstSeen = now, LastSeen = now,
                };
                p.Bridges[via.Id] = route;
                return p;
            },
            (_, p) =>
            {
                // Bridges answer at the same moment (all asked at start): an add factory that lost the race must not
                // report "added" again, or the app announces the node once per bridge ("joined" toasts in a row).
                change = null;
                lock (p)
                {
                    p.Bridges[via.Id] = route;
                    if (p.HeardDirectly) return p;
                    p.LastSeen = now;
                    if (!p.Online) { p.Online = true; p.OfflineSince = null; p.PreferRelay = false; change = PeerChange.Online; }
                    // bridges lag each other by a few seconds: a newer version counts at once, an older one only when
                    // every bridge reports it (the node started over)
                    else if (n.Cat != p.CatalogVersion && (n.Cat > p.CatalogVersion || p.LiveBridges().All(b => b.Cat == n.Cat)))
                        change = PeerChange.VersionChanged;
                    if (change is PeerChange.Online or PeerChange.VersionChanged) p.CatalogVersion = n.Cat;
                    p.PublicKey = n.Pk; p.Name = n.Name; p.User = n.User; p.Host = n.Host; p.Realm = realm; p.ShareCount = n.Shares;
                }
                return p;
            });
        if (change is { } c) Changed?.Invoke(peer, c);
    }

    /// <summary>
    /// The bridge no longer reaches <paramref name="nodeId"/> (or, with null, anything: it went offline, was
    /// forgotten, or switched bridging off): forget those routes.
    /// </summary>
    public void DropBridge(string bridgeId, string? nodeId = null)
    {
        foreach (var p in _peers.Values)
        {
            if (nodeId is not null && p.Id != nodeId) continue;
            if (!p.Bridges.TryRemove(bridgeId, out _)) continue;
            if (p.Online && !p.HeardDirectly && p.LiveBridges().Count == 0) MarkOffline(p.Id);
        }
    }

    public void Remove(string id)
    {
        if (_peers.TryRemove(id, out var p)) { Changed?.Invoke(p, PeerChange.Removed); DropBridge(id); }
    }

    public void RemoveWhere(Func<Peer, bool> predicate)
    {
        foreach (var p in _peers.Values.ToList())
            if (predicate(p)) Remove(p.Id);
    }

    /// <summary>
    /// Silent peers go offline (each after its own <see cref="Peer.Silence"/>; a bridged one when no bridge confirms
    /// it any more); offline peers are forgotten after <paramref name="keepOffline"/>.
    /// </summary>
    public void Expire(TimeSpan keepOffline)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var p in _peers.Values.ToList())
        {
            foreach (var b in p.Bridges.Values.ToList())
                if (now - b.Confirmed > b.Fresh * 3) p.Bridges.TryRemove(b.Id, out _);
            // a bridged node is confirmed by its bridges' answers, not by its own beacons
            var live = !p.HeardDirectly ? p.LiveBridges() : new List<BridgeRoute>();
            var quiet = live.Count > 0 ? live.Max(b => b.Fresh) + TimeSpan.FromSeconds(5) : p.Silence;
            if (p.Online && p.LastSeen < now - quiet) MarkOffline(p.Id);
            else if (!p.Online && (p.OfflineSince ?? p.LastSeen) < now - keepOffline) Remove(p.Id);
            else if (!p.Online && SuccessorOf(p) is { } successor)
            {
                Replaced?.Invoke(p, successor);
                Remove(p.Id);
            }
        }
    }

    /// <summary>An offline node was replaced by this one (the same PC under a new identity); its entry is gone.</summary>
    public event Action<Peer, Peer>? Replaced;

    /// <summary>
    /// The same PC came back under a new identity (its key file was lost or reset, or Ocean was reinstalled without
    /// its data): an online node with the same computer name and user, on one of the old one's addresses (or, when
    /// either is reached through a bridge, the same bridge-side name), that first appeared after the old one went
    /// quiet. Two different PCs never share a computer name and an address at the same time, and a PC that is
    /// online keeps its entry, so nothing that is really there is merged away.
    /// </summary>
    private Peer? SuccessorOf(Peer old)
    {
        if (string.IsNullOrWhiteSpace(old.Host)) return null;
        foreach (var p in _peers.Values)
        {
            if (p.Id == old.Id || !p.Online) continue;
            if (!string.Equals(p.Host, old.Host, StringComparison.OrdinalIgnoreCase) || !string.Equals(p.User, old.User, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.FirstSeen < old.LastSeen - TimeSpan.FromMinutes(1)) continue; // they were around together: two PCs
            var oldAddrs = old.Addresses.Keys.Append(old.Address).Where(a => !a.Equals(IPAddress.None)).ToHashSet();
            var newAddrs = p.Addresses.Keys.Append(p.Address).Where(a => !a.Equals(IPAddress.None)).ToHashSet();
            var sameAddress = oldAddrs.Overlaps(newAddrs);
            var bridged = oldAddrs.Count == 0 || newAddrs.Count == 0; // reached through a bridge: no address to compare
            if (sameAddress || bridged) return p;
        }
        return null;
    }
}
