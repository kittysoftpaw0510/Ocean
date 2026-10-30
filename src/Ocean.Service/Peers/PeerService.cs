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
    private readonly ConcurrentDictionary<string, PeerLocalState> _state = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new();
    private readonly ConcurrentDictionary<string, string> _pendingLocks = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _thumbInflight = new();
    private readonly object _persistLock = new();

    private readonly Catalog.CatalogService _catalog;

    public PeerService(PeerRegistry registry, PeerClient client, ConfigStore config, EventHub events, Catalog.CatalogService catalog, ILogger<PeerService> log)
    {
        _registry = registry; _client = client; _config = config; _events = events; _catalog = catalog; _log = log;
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
                    break;
                case PeerChange.Removed:
                    // forgotten (offline too long, or a different shared secret now): drop its snapshot
                    _catalogs.TryRemove(p.Id, out _);
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
                Addresses = p.Candidates().Select(a => a.ToString()).ToList(), Catalog = cat,
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

    // ------------------------------------------------------------------ peers

    public List<PeerDto> Peers() => _registry.Snapshot().Select(ToDto)
        .OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public PeerDto ToDto(Peer p)
    {
        _catalogs.TryGetValue(p.Id, out var cat);
        return new PeerDto
        {
            Id = p.Id, Name = p.Name, User = p.User, Host = p.Host, Address = p.DisplayAddress.ToString(), Port = p.Port,
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

    public ItemDto? FindItem(string peerId, string itemId)
        => Cached(peerId)?.Shares.SelectMany(s => s.Items).FirstOrDefault(i => i.Id == itemId);

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

    private async Task<CatalogDto?> FetchCatalogAsync(string peerId, CancellationToken ct)
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
            if (hadItems) _events.Publish("peerCatalog", new { peerId, version = empty.Version, newCount = 0, itemCount = 0 });
            return empty;
        }
        var gate = _fetchLocks.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var req = _client.Build(peer, HttpMethod.Get, "/p/catalog", GrantsFor(peerId));
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _log.LogInformation("catalog refused by {Peer} (realm mismatch?)", peer.Name);
                return null;
            }
            if (!resp.IsSuccessStatusCode) { _log.LogDebug("catalog {Status} from {Peer}", resp.StatusCode, peer.Name); return null; }
            var cat = await resp.Content.ReadFromJsonAsync<CatalogDto>(Json.Options, cts.Token);
            if (cat is null) return null;

            Annotate(peerId, cat);
            var previous = Cached(peerId);
            _catalogs[peerId] = cat;
            SaveSnapshot(peer, cat);
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
                    _events.Publish("newVideos", new { peerId, peerName = cat.NodeName, count = fresh.Count, names = fresh.Take(3).Select(i => i.Name).ToList() });
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
        finally { gate.Release(); }
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
                item.IsMine = mine.Contains(EchoKey(item.Name, item.Size));
                item.IsNew = !item.IsMine && item.AddedAt > threshold;
            }
        }
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

    private Dictionary<string, string> GrantsFor(string peerId)
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
    public async Task<HttpResponseMessage> OpenStreamAsync(string peerId, string itemId, string? range, CancellationToken ct)
    {
        var peer = OnlinePeer(peerId);
        var req = _client.Build(peer, HttpMethod.Get, "/p/stream/" + Uri.EscapeDataString(itemId), GrantsFor(peerId));
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
