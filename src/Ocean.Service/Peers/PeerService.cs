using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Discovery;
using Ocean.Service.State;

namespace Ocean.Service.Peers;

public sealed class GrantToken
{
    public string Token { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsValid => ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow;
}

public sealed class PeerLocalState
{
    public string Name { get; set; } = "";
    public DateTimeOffset? LastVisitedAt { get; set; }
    /// <summary>Its Ocean version, as last seen (its requests to us, or its video list), kept while it is offline.</summary>
    public string? Version { get; set; }
    public DateTimeOffset? VersionSeenAt { get; set; }
    public Dictionary<string, GrantToken> Grants { get; set; } = new();
}

public sealed class PeerUnavailableException : Exception
{
    public PeerUnavailableException(string msg) : base(msg) { }
}

/// <summary>
/// Browsing side: keeps a cached catalog per visible peer (refreshed only when its version changes),
/// remembers grants and "last visited" per peer, proxies thumbnails and streams.
/// </summary>
public sealed class PeerService : IDisposable
{
    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly ConfigStore _config;
    private readonly EventHub _events;
    private readonly ILogger<PeerService> _log;
    private readonly string _statePath;
    private readonly string _thumbDir;
    private readonly ConcurrentDictionary<string, CatalogDto> _catalogs = new();
    /// <summary>ETag of the catalog we hold per node (sent back so the owner can answer 304 or a delta).</summary>
    private readonly ConcurrentDictionary<string, string> _etags = new();
    private readonly ConcurrentDictionary<string, PeerLocalState> _state = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new();
    private readonly ConcurrentDictionary<string, string> _pendingLocks = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _thumbInflight = new();
    private readonly object _persistLock = new();

    private readonly Catalog.CatalogService _catalog;

    private readonly Social.MyVotes _myVotes;

    public PeerService(PeerRegistry registry, PeerClient client, ConfigStore config, EventHub events, Catalog.CatalogService catalog, Social.MyVotes myVotes, ILogger<PeerService> log)
    {
        _registry = registry; _client = client; _config = config; _events = events; _catalog = catalog; _myVotes = myVotes; _log = log;
        _statePath = Path.Combine(OceanDefaults.DataDir, "peers.json");
        _thumbDir = Path.Combine(OceanDefaults.DataDir, "cache", "thumbs");
        Directory.CreateDirectory(_thumbDir);
        try
        {
            if (File.Exists(_statePath))
                foreach (var kv in Json.Deserialize<Dictionary<string, PeerLocalState>>(File.ReadAllText(_statePath)) ?? new())
                    _state[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log.LogWarning(ex, "peer state load failed"); }

        _snapshotDir = Path.Combine(OceanDefaults.DataDir, "cache", "peers");
        Directory.CreateDirectory(_snapshotDir);
        RestoreSnapshots();

        _registry.Changed += (p, change) =>
        {
            switch (change)
            {
                case PeerChange.Added or PeerChange.VersionChanged or PeerChange.Online:
                    ScheduleFetch(p.Id);
                    if (change != PeerChange.VersionChanged) _ = DeliverPendingVotesAsync(p.Id);
                    break;
                case PeerChange.Removed:
                    // forgotten (offline too long, or a different shared secret now): drop its snapshot
                    _catalogs.TryRemove(p.Id, out _);
                    _etags.TryRemove(p.Id, out _);
                    try { File.Delete(SnapshotPath(p.Id)); } catch { }
                    break;
            }
        };
    }

    // ------------------------------------------------------------------ offline snapshots

    /// <summary>What we remember about a node so it can be shown (offline) after it disappears or after a restart.</summary>
    private sealed class PeerSnapshot
    {
        public string Id { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string User { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Realm { get; set; } = "";
        public long CatalogVersion { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public List<string> Addresses { get; set; } = new();
        public CatalogDto? Catalog { get; set; }
        public string? ETag { get; set; }
    }

    private readonly string _snapshotDir;

    private string SnapshotPath(string peerId) => Path.Combine(_snapshotDir, peerId[..Math.Min(32, peerId.Length)] + ".json");

    private void SaveSnapshot(Peer p, CatalogDto cat)
    {
        try
        {
            var snap = new PeerSnapshot
            {
                Id = p.Id, PublicKey = p.PublicKey, Name = p.Name, User = p.User, Host = p.Host, Port = p.Port, Realm = p.Realm,
                CatalogVersion = cat.Version, FirstSeen = p.FirstSeen, LastSeen = p.LastSeen,
                Addresses = p.Candidates().Select(a => a.ToString()).ToList(), Catalog = cat, ETag = _etags.TryGetValue(p.Id, out var etag) ? etag : null,
            };
            AtomicFile.Write(SnapshotPath(p.Id), Json.Serialize(snap));
        }
        catch (Exception ex) { _log.LogDebug(ex, "snapshot save failed for {Peer}", p.Name); }
    }

    /// <summary>At startup: list recently seen nodes (same secret) as offline, with their last known shares.</summary>
    private void RestoreSnapshots()
    {
        var keep = TimeSpan.FromHours(Math.Max(1, _config.Current.OfflineKeepHours));
        var realm = Realm.Tag(_config.Current.Secret);
        foreach (var file in Directory.EnumerateFiles(_snapshotDir, "*.json"))
        {
            try
            {
                var s = Json.Deserialize<PeerSnapshot>(File.ReadAllText(file));
                if (s?.Catalog is null || s.Realm != realm || s.LastSeen < DateTimeOffset.UtcNow - keep)
                {
                    File.Delete(file);
                    continue;
                }
                var peer = new Peer
                {
                    Id = s.Id, PublicKey = s.PublicKey, Name = s.Name, User = s.User, Host = s.Host, Port = s.Port, Realm = s.Realm,
                    CatalogVersion = s.CatalogVersion, FirstSeen = s.FirstSeen, LastSeen = s.LastSeen, OfflineSince = s.LastSeen,
                };
                foreach (var a in s.Addresses) if (IPAddress.TryParse(a, out var ip)) peer.Seen(ip, s.LastSeen);
                Annotate(s.Id, s.Catalog);
                _catalogs[s.Id] = s.Catalog;
                if (s.ETag is not null) _etags[s.Id] = s.ETag;
                _registry.AddKnownOffline(peer);
            }
            catch (Exception ex) { _log.LogDebug(ex, "snapshot restore failed: {File}", file); }
        }
    }

    public bool IsOnline(string peerId) => _registry.TryGetOnline(peerId, out _);

    /// <summary>Manual refresh: re-check every reachable node's shares now.</summary>
    public void RefreshAll()
    {
        foreach (var p in _registry.Snapshot().Where(p => p.Online)) ScheduleFetch(p.Id);
    }

    private Peer OnlinePeer(string peerId)
    {
        if (_registry.TryGetOnline(peerId, out var p)) return p;
        var name = _registry.TryGet(peerId, out var known) ? (string.IsNullOrEmpty(known.User) ? known.Name : known.User) : "This node";
        throw new PeerUnavailableException($"{name} is offline right now.");
    }

    private void Persist()
    {
        lock (_persistLock)
        {
            try { AtomicFile.Write(_statePath, Json.Serialize(_state.ToDictionary(k => k.Key, v => v.Value))); }
            catch (Exception ex) { _log.LogWarning(ex, "peer state persist failed"); }
        }
    }

    private PeerLocalState State(string peerId) => _state.GetOrAdd(peerId, _ => new PeerLocalState());

    private static readonly System.Text.RegularExpressions.Regex VersionPattern = new(@"^(?:Ocean/)?(\d+\.\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A node's Ocean version: every Ocean sends "Ocean/x.y.z" with its requests (older ones too), and newer ones put
    /// it in their video list. Called for every request, so it only writes to disk when the version changes.
    /// </summary>
    public void NoteVersion(string peerId, string? versionOrUserAgent)
    {
        if (string.IsNullOrEmpty(versionOrUserAgent) || peerId.Length == 0) return;
        var m = VersionPattern.Match(versionOrUserAgent.Trim());
        if (!m.Success) return;
        var st = State(peerId);
        st.VersionSeenAt = DateTimeOffset.UtcNow;
        if (st.Version == m.Groups[1].Value) return;
        _log.LogInformation("{Peer} runs Ocean {Version}{Was}", _registry.TryGet(peerId, out var p) ? p.Name : NodeIdentity.ShortId(peerId),
            m.Groups[1].Value, st.Version is null ? "" : $" (was {st.Version})");
        st.Version = m.Groups[1].Value;
        Persist();
        // it may just have been updated to an Ocean with votes: what was kept for it can go out now
        if (KnowsVotes(peerId) == true && _myVotes.PendingFor(peerId).Count > 0) _ = DeliverPendingVotesAsync(peerId);
        // an Ocean without votes can't have counted any: votes an older version of this PC gave up on wait for its update
        if (KnowsVotes(peerId) == false && _myVotes.Requeue(peerId) is > 0 and var n)
            _log.LogInformation("{Peer}: {Count} vote(s) kept until it is updated to an Ocean with votes", p?.Name ?? NodeIdentity.ShortId(peerId), n);
    }

    /// <summary>Votes came with Ocean 1.3.2. True/false when the node's version is known, null otherwise.</summary>
    private bool? KnowsVotes(string peerId)
        => _state.TryGetValue(peerId, out var st) && st.Version is { } v && System.Version.TryParse(v, out var ver) ? ver >= new System.Version(1, 3, 2) : null;

    /// <summary>Everything this PC knows about one node, for the "Node info" window.</summary>
    /// <summary>
    /// <see cref="Info"/>, but first fetches the node's whole list once when the Windows edition is missing although
    /// its Ocean reports it (1.3.5 on): until 1.4.4 a delta update dropped it, so the list we keep may have lost it.
    /// </summary>
    public async Task<PeerInfoDto?> InfoAsync(string peerId, CancellationToken ct)
    {
        if (_registry.TryGetOnline(peerId, out _) && _catalogs.TryGetValue(peerId, out var cat) && cat.Os is null
            && ReportsWindows(peerId) == true && _osRefetched.TryAdd(peerId, 0))
        {
            try { await FetchCatalogAsync(peerId, ct, fullOnly: true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "catalog for node info"); }
        }
        return Info(peerId);
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _osRefetched = new();
    private bool? ReportsWindows(string peerId)
        => _state.TryGetValue(peerId, out var st) && st.Version is { } v && System.Version.TryParse(v, out var ver) ? ver >= new System.Version(1, 3, 5) : null;

    /// <summary>How a node is called in logs (its user's name, else the PC's).</summary>
    public string NameOf(string peerId) => _registry.TryGet(peerId, out var p) ? DisplayNames.Of(p.User, p.Name) : NodeIdentity.ShortId(peerId);

    public PeerInfoDto? Info(string peerId)
    {
        if (!_registry.TryGet(peerId, out var p)) return null;
        _catalogs.TryGetValue(peerId, out var cat);
        _state.TryGetValue(peerId, out var st);
        var preferred = p.Bridged ? null : p.DisplayAddress;
        var info = new PeerInfoDto
        {
            Id = p.Id, Name = p.Name, User = p.User, Host = p.Host,
            Online = p.Online, FirstSeen = p.FirstSeen, LastSeen = p.LastSeen, OfflineSince = p.OfflineSince,
            AppVersion = st?.Version ?? cat?.AppVersion, AppVersionSeenAt = st?.VersionSeenAt, MyVersion = OceanDefaults.AppVersion,
            Os = cat?.Os,
            Connection = p.Bridged ? "bridge" : p.PreferRelay ? "reverse" : "direct",
            Via = p.Via, Bridges = p.LiveBridges().Select(b => b.Name).ToList(),
            Address = preferred is null || preferred.Equals(IPAddress.None) ? null : preferred.ToString(), Port = p.Port,
            Addresses = p.Addresses.OrderByDescending(kv => kv.Value).Select(kv => new PeerAddressDto
            {
                Address = kv.Key.ToString(), LastHeard = kv.Value, Preferred = kv.Key.Equals(preferred), Local = Net.LocalNetworks.IsLan(kv.Key),
            }).ToList(),
            CatalogLoaded = cat is not null, CatalogAt = cat?.GeneratedAt,
            ShareCount = cat?.Shares.Count ?? p.ShareCount,
            ItemCount = cat?.Shares.Sum(s => s.ItemCount) ?? 0,
            TotalBytes = cat?.Shares.Sum(s => s.TotalBytes) ?? 0,
            CastItem = p.Online ? cat?.Cast?.ItemName : null,
        };
        return info;
    }

    // ------------------------------------------------------------------ peers

    public List<PeerDto> Peers() => _registry.Snapshot().Select(ToDto)
        .OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public PeerDto ToDto(Peer p)
    {
        _catalogs.TryGetValue(p.Id, out var cat);
        return new PeerDto
        {
            Id = p.Id, Name = p.Name, User = p.User, Host = p.Host, Port = p.Port,
            Address = p.Bridged ? "" : p.DisplayAddress.ToString(), Via = p.Via, ViaCount = p.Bridged ? p.LiveBridges().Count : 0,
            Cast = p.Online ? cat?.Cast : null,
            CastPlan = p.Online ? cat?.CastPlan : null,
            CatalogVersion = p.CatalogVersion, LastSeen = p.LastSeen, FirstSeen = p.FirstSeen, Online = p.Online, OfflineSince = p.OfflineSince,
            ShareCount = p.ShareCount,
            ItemCount = cat?.Shares.Sum(s => s.ItemCount) ?? 0,
            NewCount = cat?.Shares.Sum(s => s.Items.Count(i => i.IsNew)) ?? 0,
            CatalogLoaded = cat is not null,
        };
    }

    public void MarkVisited(string peerId)
    {
        var st = State(peerId);
        st.LastVisitedAt = DateTimeOffset.UtcNow;
        if (_registry.TryGet(peerId, out var p)) st.Name = p.Name;
        Persist();
        if (_catalogs.TryGetValue(peerId, out var cat))
        {
            Annotate(peerId, cat);
            _events.Publish("peerCatalog", new { peerId, version = cat.Version, newCount = 0, itemCount = cat.Shares.Sum(s => s.ItemCount) });
        }
    }

    // ------------------------------------------------------------------ catalog

    public CatalogDto? Cached(string peerId) => _catalogs.TryGetValue(peerId, out var c) ? c : null;

    // the item a multicast I'm watching streams (its size and time may be newer than the catalog I hold)
    private readonly ConcurrentDictionary<string, ItemDto> _castItems = new(StringComparer.Ordinal);
    private static string? PlanKey(CastPlanDto? p) => p is null ? null : $"{p.ItemId}|{p.At.ToUnixTimeSeconds()}";

    public void RegisterCastItem(string peerId, ItemDto item) => _castItems[peerId + "|" + item.Id] = item;
    public void UnregisterCastItem(string peerId, string itemId) => _castItems.TryRemove(peerId + "|" + itemId, out _);

    /// <summary>An item of a node's catalog; "{id}@light" gives its lighter copy as an item when the owner has one.</summary>
    public ItemDto? FindItem(string peerId, string itemId)
    {
        if (_castItems.TryGetValue(peerId + "|" + itemId, out var casting)) return casting;
        var light = LightVariant.Split(itemId, out var baseId);
        var item = Cached(peerId)?.Shares.SelectMany(s => s.Items).FirstOrDefault(i => i.Id == baseId);
        if (item is null || !light) return item;
        return item.Light == LightVariant.Ready && item.LightSize > 0 ? LightVariant.Of(item) : null;
    }

    /// <summary>Every node's last known catalog (online or not), for "Top on the network".</summary>
    public IEnumerable<(string PeerId, CatalogDto Catalog)> AllCatalogs() => _catalogs.Select(kv => (kv.Key, kv.Value));

    /// <summary>
    /// My vote (+1/−1/0) and/or "watched it to the end" on another node's video. Kept here first, so it shows at once
    /// and reaches the owner later if it is offline now.
    /// </summary>
    public async Task<MyRatingDto> RateAsync(string peerId, string itemId, RateDto rate, CancellationToken ct)
    {
        _myVotes.Record(peerId, itemId, rate);
        var (outcome, totals) = await SendRatingAsync(peerId, itemId, rate, ct);
        if (totals is not null)
        {
            // keep my copy of its catalog in step until the owner's next version arrives
            if (Cached(peerId)?.Shares.SelectMany(s => s.Items).FirstOrDefault(i => i.Id == itemId) is { } item)
            {
                item.Up = totals.Up; item.Down = totals.Down; item.Finished = totals.Finished; item.Score = totals.Score;
                item.MyVote = _myVotes.VoteOn(peerId, itemId);
            }
        }
        return new MyRatingDto
        {
            MyVote = _myVotes.VoteOn(peerId, itemId), Totals = totals,
            Pending = outcome is VoteOutcome.Offline or VoteOutcome.Unreachable or VoteOutcome.Old or VoteOutcome.Locked,
            Reason = outcome switch
            {
                VoteOutcome.Delivered => null,
                VoteOutcome.Offline => "offline",
                VoteOutcome.Unreachable => "unreachable",
                VoteOutcome.Old => "old",
                VoteOutcome.Locked => "locked",
                VoteOutcome.Gone => "gone",
                _ => "rejected",
            },
        };
    }

    /// <summary>What became of a vote sent to the video's owner.</summary>
    private enum VoteOutcome
    {
        Delivered,
        /// <summary>Not online, or no answer: kept, sent when it is back.</summary>
        Offline, Unreachable,
        /// <summary>Its Ocean has no votes yet (before 1.3.2): kept, sent once it is updated.</summary>
        Old,
        /// <summary>No access to the video right now (approval expired or revoked): kept, sent once approved again.</summary>
        Locked,
        /// <summary>The video is no longer shared there, or the vote was refused as invalid: dropped.</summary>
        Gone, Rejected,
    }

    private readonly ConcurrentDictionary<string, byte> _oldVoteLogged = new(StringComparer.Ordinal);

    private async Task<(VoteOutcome Outcome, RatingDto? Totals)> SendRatingAsync(string peerId, string itemId, RateDto rate, CancellationToken ct)
    {
        if (!_registry.TryGetOnline(peerId, out var peer)) return (VoteOutcome.Offline, null);
        try
        {
            var req = _client.Build(peer, HttpMethod.Post, "/p/rate/" + Uri.EscapeDataString(itemId), GrantsFor(peerId));
            req.Content = JsonContent.Create(rate, options: Json.Options);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // An Ocean before 1.3.2 has no votes and says "not found" to every vote; one with votes says it only for a
                // video it no longer shares. Told apart by its version when known, else by whether it still lists the video.
                var old = KnowsVotes(peerId) is { } knows ? !knows
                    : Cached(peerId)?.Shares.Any(s => s.Items.Any(i => i.Id == itemId)) == true;
                if (old)
                {
                    if (_oldVoteLogged.TryAdd(peerId, 0))
                        _log.LogInformation("{Peer} runs an Ocean without votes (before 1.3.2); votes for it are kept until it is updated", peer.Name);
                    return (VoteOutcome.Old, null);
                }
                _myVotes.Delivered(peerId, itemId, rate); // the video is gone: nothing left to vote on
                return (VoteOutcome.Gone, null);
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _log.LogInformation("{Peer} refused a vote for {Item}: no access to it right now; kept until approved again", peer.Name, itemId);
                return (VoteOutcome.Locked, null);
            }
            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                _myVotes.Delivered(peerId, itemId, rate); // refused as invalid: sending it again would not help
                return (VoteOutcome.Rejected, null);
            }
            if (!resp.IsSuccessStatusCode) return (VoteOutcome.Unreachable, null);
            var totals = await resp.Content.ReadFromJsonAsync<RatingDto>(Json.Options, cts.Token);
            _myVotes.Delivered(peerId, itemId, rate);
            return (VoteOutcome.Delivered, totals);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            _log.LogDebug("vote for {Item} on {Peer} not delivered yet: {Msg}", itemId, peer.Name, ex.Message);
            return (VoteOutcome.Unreachable, null);
        }
    }

    /// <summary>Votes cast while a node was offline go out when it is back.</summary>
    private async Task DeliverPendingVotesAsync(string peerId)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var (itemId, rate) in _myVotes.PendingFor(peerId))
            await SendRatingAsync(peerId, itemId, rate, CancellationToken.None);
    }

    /// <summary>How the lighter copy of another node's video is doing; with <paramref name="request"/>, asks for one.</summary>
    public async Task<LightStatusDto> LightAsync(string peerId, string itemId, bool request, CancellationToken ct)
    {
        try
        {
            var peer = OnlinePeer(peerId);
            var req = _client.Build(peer, request ? HttpMethod.Post : HttpMethod.Get, "/p/light/" + Uri.EscapeDataString(itemId), GrantsFor(peerId));
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new LightStatusDto { State = "off" }; // an older Ocean there
            if (!resp.IsSuccessStatusCode) return new LightStatusDto { State = "none" };
            return await resp.Content.ReadFromJsonAsync<LightStatusDto>(Json.Options, ct) ?? new LightStatusDto();
        }
        catch (Exception ex) when (ex is PeerUnavailableException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new LightStatusDto { State = "none" };
        }
    }

    private void ScheduleFetch(string peerId)
    {
        var cts = new CancellationTokenSource();
        var prev = _debounce.AddOrUpdate(peerId, cts, (_, old) => { old.Cancel(); return cts; });
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (OperationCanceledException) { return; }
            try { await FetchCatalogAsync(peerId, CancellationToken.None); }
            catch (Exception ex) { _log.LogDebug(ex, "catalog fetch failed for {Peer}", peerId); }
        });
    }

    public async Task<CatalogDto?> GetCatalogAsync(string peerId, bool refresh, CancellationToken ct)
    {
        if (!IsOnline(peerId)) return Cached(peerId); // offline: the last known list
        if (!refresh && _catalogs.TryGetValue(peerId, out var cached))
        {
            if (_registry.TryGet(peerId, out var p) && p.CatalogVersion == cached.Version) return cached;
        }
        return await FetchCatalogAsync(peerId, ct) ?? Cached(peerId);
    }

    private async Task<CatalogDto?> FetchCatalogAsync(string peerId, CancellationToken ct, bool fullOnly = false)
    {
        if (!_registry.TryGetOnline(peerId, out var peer)) return null;
        // Its announcements say it shares nothing: no need to connect (and nothing can go wrong).
        if (peer.ShareCount == 0)
        {
            var empty = new CatalogDto
            {
                NodeId = peer.Id, NodeName = peer.Name, UserName = peer.User, Host = peer.Host,
                Version = peer.CatalogVersion, GeneratedAt = DateTimeOffset.UtcNow,
            };
            var hadItems = Cached(peerId)?.Shares.Count > 0;
            _catalogs[peerId] = empty;
            _etags.TryRemove(peerId, out _);
            if (hadItems) _events.Publish("peerCatalog", new { peerId, version = empty.Version, newCount = 0, itemCount = 0 });
            return empty;
        }
        var gate = _fetchLocks.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var released = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var previous = Cached(peerId);
            var req = _client.Build(peer, HttpMethod.Get, "/p/catalog", GrantsFor(peerId));
            // Tell the owner which version we hold: it answers 304 (unchanged), only the changes, or everything.
            if (!fullOnly && previous is not null && _etags.TryGetValue(peerId, out var held)
                && CatalogTags.TryParse(held, out var heldVersion, out _) && heldVersion == previous.Version)
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", held);
                req.Headers.TryAddWithoutValidation(CatalogTags.BaseHeader, held);
            }
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _log.LogInformation("catalog refused by {Peer} (realm mismatch?)", peer.Name);
                return null;
            }
            if (resp.StatusCode == HttpStatusCode.NotModified && previous is not null)
            {
                _log.LogDebug("catalog v{Version} from {Peer}: unchanged", previous.Version, peer.Name);
                return previous;
            }
            if (!resp.IsSuccessStatusCode) { _log.LogDebug("catalog {Status} from {Peer}", resp.StatusCode, peer.Name); return null; }
            var cat = await resp.Content.ReadFromJsonAsync<CatalogDto>(Json.Options, cts.Token);
            if (cat is null) return null;
            var tag = resp.Headers.ETag?.ToString();

            if (cat.DeltaFrom is { } from)
            {
                if (previous is null || previous.Version != from)
                {
                    // Our copy changed meanwhile (or is gone): ask for the whole catalog instead.
                    gate.Release(); released = true;
                    return await FetchCatalogAsync(peerId, ct, fullOnly: true);
                }
                var changedItems = cat.Shares.Sum(s => s.Items.Count);
                var deltaCast = cat.Cast; var deltaPlan = cat.CastPlan;
                cat = MergeDelta(previous, cat);
                cat.Cast = deltaCast; cat.CastPlan = deltaPlan;
                _log.LogDebug("catalog v{Version} from {Peer}: {Changed} changed, {Removed} removed since v{From}",
                    cat.Version, peer.Name, changedItems, cat.RemovedItems?.Count ?? 0, from);
                cat.RemovedItems = null;
            }
            if (tag is not null) _etags[peerId] = tag; else _etags.TryRemove(peerId, out _);

            Annotate(peerId, cat);
            _catalogs[peerId] = cat;
            SaveSnapshot(peer, cat);
            NoteVersion(peerId, cat.AppVersion);
            if (previous?.Cast?.SessionId != cat.Cast?.SessionId || PlanKey(previous?.CastPlan) != PlanKey(cat.CastPlan))
                _events.Publish("peerCast", new { peerId, peerName = DisplayNames.Of(cat.UserName, cat.NodeName), cast = cat.Cast, plan = cat.CastPlan });
            var itemCount = cat.Shares.Sum(s => s.ItemCount);
            var newCount = cat.Shares.Sum(s => s.Items.Count(i => i.IsNew));
            _events.Publish("peerCatalog", new { peerId, version = cat.Version, newCount, itemCount });

            if (previous is not null && _config.Current.NotifyNewVideos)
            {
                // Only shares we could already see count: items that merely became visible because we
                // unlocked a share (or an owner opened one up) were not "added".
                var visibleBefore = previous.Shares.Where(s => s.Lock is null || s.Unlocked).Select(s => s.Id).ToHashSet();
                var known = previous.Shares.SelectMany(s => s.Items).Select(i => i.Id).ToHashSet();
                var fresh = cat.Shares.Where(s => visibleBefore.Contains(s.Id))
                    .SelectMany(s => s.Items).Where(i => !known.Contains(i.Id) && i.IsNew && i.Unlocked).ToList();
                if (fresh.Count > 0)
                    _events.Publish("newVideos", new { peerId, peerName = DisplayNames.Of(cat.UserName, cat.NodeName), count = fresh.Count, names = fresh.Take(3).Select(i => i.Name).ToList() });
            }
            _log.LogInformation("catalog v{Version} from {Peer}: {Shares} shares, {Items} items, {New} new", cat.Version, peer.Name, cat.Shares.Count, itemCount, newCount);
            return cat;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Typical cause: the other PC's firewall lets our discovery packets out but blocks incoming connections.
            _log.LogWarning("catalog fetch timed out for {Peer} at {Addresses} (firewall on that PC?)", peer.Name, string.Join(", ", peer.Candidates()));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("catalog fetch failed for {Peer} at {Url}: {Msg}", peer.Name, peer.BaseUrl, ex.InnerException?.Message ?? ex.Message);
            return null;
        }
        finally { if (!released) gate.Release(); }
    }

    /// <summary>
    /// Applies a delta to the catalog we hold. Share headers in the delta are complete; its items are the ones that
    /// changed. The result is a new catalog object (the previous one is still used to spot newly added videos).
    /// </summary>
    private static CatalogDto MergeDelta(CatalogDto previous, CatalogDto delta)
    {
        var removed = new HashSet<string>(delta.RemovedItems ?? new(), StringComparer.Ordinal);
        var changed = new HashSet<string>(delta.Shares.SelectMany(s => s.Items).Select(i => i.Id), StringComparer.Ordinal);
        var old = previous.Shares.ToDictionary(s => s.Id);
        foreach (var share in delta.Shares)
        {
            var visible = share.Lock is null || share.Unlocked;
            if (!visible) { share.Items = new(); continue; }
            if (old.TryGetValue(share.Id, out var before) && (before.Lock is null || before.Unlocked))
            {
                var kept = before.Items.Where(i => !removed.Contains(i.Id) && !changed.Contains(i.Id));
                share.Items = kept.Concat(share.Items).ToList();
            }
        }
        // Up to 1.4.4 a delta left out the owner's Ocean version and Windows edition: keep what we knew.
        delta.AppVersion ??= previous.AppVersion;
        delta.Os ??= previous.Os;
        delta.DeltaFrom = null;
        return delta;
    }

    private void Annotate(string peerId, CatalogDto cat)
    {
        var st = State(peerId);
        var now = DateTimeOffset.UtcNow;
        var recent = now.AddDays(-Math.Max(1, _config.Current.NewDays));
        var threshold = st.LastVisitedAt is { } v ? (v > recent ? v : recent) : recent;
        // Echo protection: videos I share myself (same name + size) that come back through another PC,
        // typically because it downloaded them from me and shares its downloads folder.
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _catalog.Shares)
            foreach (var i in _catalog.Items(s.Id))
                mine.Add(EchoKey(i.Name, i.Size));
        // Items added since the last visit are "new"; if the last visit is older than NewDays we still only
        // highlight items from the last NewDays so an old peer does not light up entirely.
        foreach (var share in cat.Shares)
        {
            foreach (var item in share.Items)
            {
                item.ShareId = share.Id;
                item.MyVote = _myVotes.VoteOn(peerId, item.Id);
                item.IsMine = mine.Contains(EchoKey(item.Name, item.Size));
                item.IsNew = !item.IsMine && item.AddedAt > threshold;
            }
        }
    }

    private Dictionary<string, string>? _ownCopies;
    private long _ownCopiesVersion = -1;

    /// <summary>
    /// My own shared file that is the same video (same name + size, see <see cref="EchoKey"/>), if any: playing it
    /// from disk costs no network at all.
    /// </summary>
    public string? OwnCopyPath(ItemDto item)
    {
        var version = _catalog.Version;
        var index = _ownCopies;
        if (index is null || _ownCopiesVersion != version)
        {
            index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _catalog.Shares)
                foreach (var i in _catalog.Items(s.Id))
                    index.TryAdd(EchoKey(i.Name, i.Size), _catalog.FullPath(s, i));
            _ownCopies = index; _ownCopiesVersion = version;
        }
        if (!index.TryGetValue(EchoKey(item.Name, item.Size), out var path)) return null;
        try { return new FileInfo(path) is { Exists: true } f && f.Length == item.Size ? path : null; }
        catch { return null; }
    }

    /// <summary>
    /// Same video = same size + same name as it would be saved on disk. Names are normalized the way downloads
    /// are (invalid characters replaced, very long names shortened) and capped at 100 characters, so a copy that
    /// was downloaded and re-shared still matches its original.
    /// </summary>
    private static string EchoKey(string name, long size)
    {
        var safe = Core.Naming.SafeNames.FileName(name);
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (stem.Length > 100) stem = stem[..100];
        return size.ToString() + "|" + stem.ToLowerInvariant() + Path.GetExtension(safe).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ grants / unlocking

    public Dictionary<string, string> GrantsFor(string peerId)
    {
        var st = State(peerId);
        lock (st) return st.Grants.Where(g => g.Value.IsValid).ToDictionary(g => g.Key, g => g.Value.Token);
    }

    private void StoreGrant(string peerId, string lockId, AccessResponseDto resp)
    {
        if (resp.Status != AccessStatus.Granted || string.IsNullOrEmpty(resp.Token)) return;
        var st = State(peerId);
        lock (st) st.Grants[lockId] = new GrantToken { Token = resp.Token, ExpiresAt = resp.ExpiresAt };
        Persist();
        if (_myVotes.PendingFor(peerId).Count > 0) _ = DeliverPendingVotesAsync(peerId); // kept for lack of access
    }

    public void DropGrant(string peerId, string lockId)
    {
        var st = State(peerId);
        bool removed;
        lock (st) removed = st.Grants.Remove(lockId);
        if (removed) Persist();
    }

    public async Task<AccessResponseDto> UnlockAsync(string peerId, string lockId, string? password, CancellationToken ct)
    {
        var peer = OnlinePeer(peerId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var req = _client.Build(peer, HttpMethod.Post, "/p/access", GrantsFor(peerId));
        req.Content = JsonContent.Create(new AccessRequestDto { LockId = lockId, Password = password }, options: Json.Options);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        var result = await resp.Content.ReadFromJsonAsync<AccessResponseDto>(Json.Options, cts.Token)
                     ?? new AccessResponseDto { Status = AccessStatus.Error, Message = "Bad response" };
        if (result.Status == AccessStatus.Granted)
        {
            StoreGrant(peerId, lockId, result);
            await FetchCatalogAsync(peerId, ct);
        }
        else if (result.Status == AccessStatus.Pending && result.RequestId is not null)
        {
            _pendingLocks[result.RequestId] = lockId;
        }
        return result;
    }

    public async Task<AccessResponseDto> PollUnlockAsync(string peerId, string requestId, CancellationToken ct)
    {
        var peer = OnlinePeer(peerId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var req = _client.Build(peer, HttpMethod.Get, "/p/access/" + Uri.EscapeDataString(requestId));
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        var result = await resp.Content.ReadFromJsonAsync<AccessResponseDto>(Json.Options, cts.Token)
                     ?? new AccessResponseDto { Status = AccessStatus.Error, Message = "Bad response" };
        if (result.Status == AccessStatus.Granted && _pendingLocks.TryRemove(requestId, out var lockId))
        {
            StoreGrant(peerId, lockId, result);
            await FetchCatalogAsync(peerId, ct);
        }
        else if (result.Status is AccessStatus.Denied or AccessStatus.NotFound) _pendingLocks.TryRemove(requestId, out _);
        return result;
    }

    // ------------------------------------------------------------------ media

    /// <summary>Opens a (range) stream from the peer. Caller disposes the response.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string peerId, string itemId, string? range, CancellationToken ct, bool download = false)
    {
        var peer = OnlinePeer(peerId);
        var req = _client.Build(peer, HttpMethod.Get, "/p/stream/" + Uri.EscapeDataString(itemId), GrantsFor(peerId));
        if (download) req.Headers.TryAddWithoutValidation(PeerHeaders.Purpose, PeerHeaders.Download); // the owner lets viewers go first
        if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);
        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden) await HandleLockedAsync(peerId, resp);
        return resp;
    }

    private async Task HandleLockedAsync(string peerId, HttpResponseMessage resp)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options);
            if (err?.Lock is not null) DropGrant(peerId, err.Lock.Id); // our token is stale: forget it
        }
        catch { }
    }

    public Task<string?> GetThumbPathAsync(string peerId, string itemId, CancellationToken ct)
    {
        var item = FindItem(peerId, itemId);
        // locked for me (never unlocked, or the approval was withdrawn since): not even a thumbnail fetched earlier
        if (item is { Lock: not null, Unlocked: false }) return Task.FromResult<string?>(null);
        var key = $"{itemId}-{item?.ModifiedAt.UtcTicks ?? 0}";
        var dir = Path.Combine(_thumbDir, peerId[..Math.Min(16, peerId.Length)]);
        var jpeg = Path.Combine(dir, key + ".jpg");
        if (File.Exists(jpeg)) return Task.FromResult<string?>(jpeg);
        var none = Path.Combine(dir, key + ".none");
        if (File.Exists(none) && File.GetLastWriteTimeUtc(none) > DateTime.UtcNow.AddMinutes(-10)) return Task.FromResult<string?>(null);
        if (!IsOnline(peerId)) return Task.FromResult<string?>(null); // offline: only thumbnails cached earlier
        return _thumbInflight.GetOrAdd(key, k => FetchThumbAsync(peerId, itemId, dir, jpeg, none, ct).ContinueWith(t =>
        {
            _thumbInflight.TryRemove(key, out _);
            return t.IsCompletedSuccessfully ? t.Result : null;
        }, TaskScheduler.Default));
    }

    private async Task<string?> FetchThumbAsync(string peerId, string itemId, string dir, string jpeg, string none, CancellationToken ct)
    {
        if (!_registry.TryGetOnline(peerId, out var peer)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var req = _client.Build(peer, HttpMethod.Get, "/p/thumb/" + Uri.EscapeDataString(itemId), GrantsFor(peerId));
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            Directory.CreateDirectory(dir);
            if (resp.StatusCode == HttpStatusCode.Forbidden) { await HandleLockedAsync(peerId, resp); return null; }
            if (!resp.IsSuccessStatusCode)
            {
                try { File.WriteAllBytes(none, Array.Empty<byte>()); } catch { }
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length == 0) return null;
            var tmp = jpeg + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, cts.Token);
            File.Move(tmp, jpeg, overwrite: true);
            return jpeg;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "thumb fetch failed {Peer}/{Item}", peerId, itemId);
            return null;
        }
    }

    /// <summary>Removes cached thumbnails of peers not seen for a long time (bounded disk usage).</summary>
    public void PruneThumbCache(TimeSpan maxAge)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_thumbDir))
            {
                var newest = Directory.EnumerateFiles(dir).Select(File.GetLastAccessTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                if (newest < DateTime.UtcNow - maxAge) Directory.Delete(dir, true);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var c in _debounce.Values) c.Cancel();
    }
}
