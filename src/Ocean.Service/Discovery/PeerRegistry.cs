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
    /// physical adapters (the real LAN), then other local subnets (virtual switches, VPN), then private ranges,
    /// then the most recently heard.
    /// </summary>
    public List<IPAddress> Candidates()
    {
        var list = Addresses.OrderBy(kv => kv.Key.Equals(Preferred) ? 0 : 1)
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

public enum PeerChange { Added, Updated, VersionChanged, Offline, Online, Removed }

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

    public void MarkOffline(string id)
    {
        if (!_peers.TryGetValue(id, out var p) || !p.Online) return;
        lock (p) { p.Online = false; p.OfflineSince = DateTimeOffset.UtcNow; p.Preferred = null; }
        Changed?.Invoke(p, PeerChange.Offline);
    }

    /// <summary>Applies a verified beacon and reports what changed (Added / Online = newly reachable).</summary>
    public PeerChange Upsert(Beacon b, IPAddress address)
    {
        var now = DateTimeOffset.UtcNow;
        var isNew = false;
        PeerChange change = PeerChange.Updated;
        var peer = _peers.AddOrUpdate(b.Id,
            _ =>
            {
                isNew = true;
                change = PeerChange.Added;
                var p = new Peer
                {
                    Id = b.Id, PublicKey = b.Pk, Name = b.Name, User = b.User, Host = b.Host, Port = b.Port,
                    Realm = b.Realm, CatalogVersion = b.Cat, ShareCount = b.Shares, LastTs = b.Ts, FirstSeen = now, LastSeen = now,
                };
                p.Seen(address, now);
                return p;
            },
            (_, p) =>
            {
                lock (p)
                {
                    // Only fresh beacons count (a replayed old one must neither revive nor change a peer). The same
                    // beacon arrives once per adapter with the same timestamp, so every address is still learned.
                    if (b.Ts < p.LastTs) { change = PeerChange.Updated; return p; }
                    p.Seen(address, now);
                    p.LastSeen = now;
                    if (!p.Online) { p.Online = true; p.OfflineSince = null; change = PeerChange.Online; }
                    else if (p.CatalogVersion != b.Cat) change = PeerChange.VersionChanged;
                    p.Name = b.Name; p.User = b.User; p.Host = b.Host; p.Port = b.Port;
                    p.Realm = b.Realm; p.CatalogVersion = b.Cat; p.ShareCount = b.Shares; p.LastTs = b.Ts;
                }
                return p;
            });
        Changed?.Invoke(peer, change);
        return isNew ? PeerChange.Added : change;
    }

    public void Remove(string id)
    {
        if (_peers.TryRemove(id, out var p)) Changed?.Invoke(p, PeerChange.Removed);
    }

    public void RemoveWhere(Func<Peer, bool> predicate)
    {
        foreach (var p in _peers.Values.ToList())
            if (predicate(p)) Remove(p.Id);
    }

    /// <summary>Silent peers go offline after <paramref name="silence"/>; offline peers are forgotten after <paramref name="keepOffline"/>.</summary>
    public void Expire(TimeSpan silence, TimeSpan keepOffline)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var p in _peers.Values.ToList())
        {
            if (p.Online && p.LastSeen < now - silence) MarkOffline(p.Id);
            else if (!p.Online && (p.OfflineSince ?? p.LastSeen) < now - keepOffline) Remove(p.Id);
        }
    }
}
