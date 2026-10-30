using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Media;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;
using Ocean.Service.State;

namespace Ocean.Service.Catalog;

public sealed class ItemRecord
{
    public string Id { get; set; } = "";
    public int Root { get; set; }
    /// <summary>Path relative to the root directory (backslashes). Empty for single-file roots.</summary>
    public string SubPath { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public long? DurationMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Probed { get; set; }
}

public sealed class ShareIndex
{
    public string ShareId { get; set; } = "";
    public List<ItemRecord> Items { get; set; } = new();
    public DateTimeOffset ScannedAt { get; set; }
    public bool FirstScanDone { get; set; }
}

internal sealed class ShareRuntime : IDisposable
{
    public required string ShareId { get; init; }
    public ConcurrentDictionary<string, ItemRecord> Items { get; } = new();
    public DateTimeOffset ScannedAt { get; set; }
    public bool FirstScanDone { get; set; }
    public SemaphoreSlim ScanLock { get; } = new(1, 1);
    public volatile bool Scanning;
    public List<FileSystemWatcher> Watchers { get; } = new();
    public Timer? Debounce { get; set; }

    public void Dispose()
    {
        foreach (var w in Watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        Watchers.Clear();
        Debounce?.Dispose();
    }
}

/// <summary>
/// Owns the local share definitions and their file indexes. Produces the catalog served to peers.
/// </summary>
public sealed class CatalogService : IDisposable
{
    private readonly ConfigStore _config;
    private readonly EventHub _events;
    private readonly ILogger<CatalogService> _log;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, ShareRuntime> _shares = new();
    private readonly ConcurrentDictionary<string, string> _itemShare = new();
    private long _version;
    private readonly string _versionPath;

    /// <summary>Raised whenever the catalog version changes (used by discovery to announce immediately).</summary>
    public event Action? Changed;

    public CatalogService(ConfigStore config, EventHub events, ILogger<CatalogService> log)
    {
        _config = config;
        _events = events;
        _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "catalog");
        Directory.CreateDirectory(_dir);
        _versionPath = Path.Combine(_dir, "version.txt");
        _version = 1;
        try { if (File.Exists(_versionPath) && long.TryParse(File.ReadAllText(_versionPath), out var v)) _version = v; } catch { }

        foreach (var share in config.Current.Shares) LoadRuntime(share);
    }

    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyList<ShareDefinition> Shares => _config.Current.Shares;

    public ShareDefinition? GetShare(string id) => Shares.FirstOrDefault(s => s.Id == id);

    public bool IsScanning(string shareId) => _shares.TryGetValue(shareId, out var rt) && rt.Scanning;

    public int ItemCount(string shareId) => _shares.TryGetValue(shareId, out var rt) ? rt.Items.Count : 0;

    public long TotalBytes(string shareId) => _shares.TryGetValue(shareId, out var rt) ? rt.Items.Values.Sum(i => i.Size) : 0;

    public IEnumerable<ItemRecord> Items(string shareId)
        => _shares.TryGetValue(shareId, out var rt) ? rt.Items.Values : Array.Empty<ItemRecord>();

    public bool TryGetItem(string itemId, out ShareDefinition share, out ItemRecord item)
    {
        share = null!; item = null!;
        if (!_itemShare.TryGetValue(itemId, out var shareId)) return false;
        var def = GetShare(shareId);
        if (def is null || !_shares.TryGetValue(shareId, out var rt) || !rt.Items.TryGetValue(itemId, out var rec)) return false;
        share = def; item = rec;
        return true;
    }

    public string FullPath(ShareDefinition share, ItemRecord item)
    {
        if (item.Root < 0 || item.Root >= share.Roots.Count) return "";
        var root = share.Roots[item.Root];
        return item.SubPath.Length == 0 ? root : Path.Combine(root, item.SubPath);
    }

    public string RelPath(ShareDefinition share, ItemRecord item)
    {
        if (item.SubPath.Length == 0) return item.Name;
        var root = item.Root < share.Roots.Count ? share.Roots[item.Root] : "";
        return RootName(root) + "/" + item.SubPath.Replace('\\', '/');
    }

    // ------------------------------------------------------------------ folders

    /// <summary>
    /// The folder an item lives in on disk, relative to the share: for a single shared folder its sub-folders
    /// ("Beach day"), for several shared folders each one becomes a top-level folder ("Family 2024/Beach day").
    /// Individually shared files sit in the share root.
    /// </summary>
    public string DiskFolder(ShareDefinition share, ItemRecord item) => DiskFolder(share.Roots, item);

    private static string DiskFolder(IReadOnlyList<string> roots, ItemRecord item)
    {
        if (item.SubPath.Length == 0) return "";
        var dir = Path.GetDirectoryName(item.SubPath) ?? "";
        var folderRoots = roots.Count(r => !VideoTypes.IsVideo(r) || Directory.Exists(r));
        if (folderRoots > 1)
        {
            var root = item.Root < roots.Count ? roots[item.Root] : "";
            dir = Path.Combine(RootName(root), dir);
        }
        return FolderPath.Normalize(dir);
    }

    /// <summary>
    /// Adds disk folders/files to a share. Videos already in the share keep the folder they are shown in
    /// (adding a second folder would otherwise nest the first one under its name). New videos go into
    /// <paramref name="folder"/> ("" = root), keeping their own sub-folders.
    /// </summary>
    public bool AddRoots(string shareId, IEnumerable<string> paths, string folder)
    {
        var target = FolderPath.Normalize(folder);
        var added = paths.Select(p => p.Trim()).Where(p => p.Length > 0)
                         .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } })
                         .Where(p => Directory.Exists(p) || (File.Exists(p) && VideoTypes.IsVideo(p)))
                         .ToList();
        if (added.Count == 0) return false;
        var items = Items(shareId).ToList();
        ShareDefinition? def = null;
        _config.Update(cfg =>
        {
            def = cfg.Shares.FirstOrDefault(s => s.Id == shareId);
            if (def is null) return;
            var oldRoots = def.Roots.ToList();
            var newRoots = oldRoots.Concat(added.Where(a => !oldRoots.Contains(a, StringComparer.OrdinalIgnoreCase)))
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            def.ItemFolders = new Dictionary<string, string>(def.ItemFolders);
            foreach (var i in items)
            {
                if (def.ItemFolders.ContainsKey(i.Id)) continue;
                var before = DiskFolder(oldRoots, i);
                if (!string.Equals(before, DiskFolder(newRoots, i), StringComparison.OrdinalIgnoreCase))
                    def.ItemFolders[i.Id] = before; // freeze where it is shown today
            }
            def.PendingPlacement = new Dictionary<string, string>(def.PendingPlacement, StringComparer.OrdinalIgnoreCase);
            if (target.Length > 0)
                foreach (var a in added) def.PendingPlacement[a] = target;
            if (target.Length > 0) def.Folders = def.Folders.Append(target).ToList();
            def.Roots = newRoots;
            def.UpdatedAt = DateTimeOffset.UtcNow;
        });
        if (def is null) return false;
        RebuildWatchers(def);
        Bump("videos added");
        _ = RescanAsync(shareId);
        return true;
    }

    /// <summary>After a scan: put videos of newly added roots into the folder they were added to.</summary>
    private void ApplyPendingPlacement(ShareDefinition def, ShareRuntime rt)
    {
        if (def.PendingPlacement.Count == 0) return;
        _config.Update(cfg =>
        {
            var d = cfg.Shares.FirstOrDefault(s => s.Id == def.Id);
            if (d is null) return;
            d.ItemFolders = new Dictionary<string, string>(d.ItemFolders);
            foreach (var (root, folder) in d.PendingPlacement)
            {
                var idx = d.Roots.FindIndex(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) continue;
                foreach (var i in rt.Items.Values.Where(i => i.Root == idx && !d.ItemFolders.ContainsKey(i.Id)))
                    d.ItemFolders[i.Id] = FolderPath.Combine(folder, DiskFolder(d, i));
            }
            d.PendingPlacement = new();
        });
    }

    /// <summary>Where the item is shown: the owner's choice, or its folder on disk.</summary>
    public string FolderOf(ShareDefinition share, ItemRecord item)
        => share.ItemFolders.TryGetValue(item.Id, out var f) ? f : DiskFolder(share, item);

    /// <summary>All folders of a share (with every ancestor), sorted.</summary>
    public List<string> AllFolders(ShareDefinition share, IEnumerable<ItemRecord> items)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in share.Folders.Concat(items.Select(i => FolderOf(share, i))))
            foreach (var a in FolderPath.SelfAndAncestors(FolderPath.Normalize(f)))
                set.Add(a);
        return set.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private bool Organize(string shareId, string reason, Action<ShareDefinition, List<ItemRecord>> change)
    {
        var ok = false;
        var items = Items(shareId).ToList();
        _config.Update(cfg =>
        {
            var def = cfg.Shares.FirstOrDefault(s => s.Id == shareId);
            if (def is null) return;
            def.ItemFolders = new Dictionary<string, string>(def.ItemFolders);
            def.Folders = new List<string>(def.Folders);
            change(def, items);
            // Keep the stored organization minimal: drop overrides equal to the disk folder and duplicate folders.
            foreach (var i in items)
                if (def.ItemFolders.TryGetValue(i.Id, out var f) && string.Equals(f, DiskFolder(def, i), StringComparison.OrdinalIgnoreCase))
                    def.ItemFolders.Remove(i.Id);
            def.Folders = def.Folders.Select(FolderPath.Normalize).Where(f => f.Length > 0)
                                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            def.UpdatedAt = DateTimeOffset.UtcNow;
            ok = true;
        });
        if (ok) Bump(reason);
        return ok;
    }

    /// <summary>Moves items into a folder (created if needed). The source folders are kept, even if now empty.</summary>
    public bool MoveItems(string shareId, IEnumerable<string> itemIds, string folder)
    {
        var target = FolderPath.Normalize(folder);
        var ids = itemIds.ToHashSet();
        return Organize(shareId, "items moved", (def, items) =>
        {
            foreach (var i in items.Where(i => ids.Contains(i.Id)))
            {
                var from = FolderOf(def, i);
                if (from.Length > 0) def.Folders.Add(from);
                def.ItemFolders[i.Id] = target;
            }
            if (target.Length > 0) def.Folders.Add(target);
        });
    }

    public bool CreateFolder(string shareId, string path)
    {
        var p = FolderPath.Normalize(path);
        return p.Length > 0 && Organize(shareId, "folder created", (def, _) => def.Folders.Add(p));
    }

    /// <summary>Renames/moves a folder with everything inside it.</summary>
    public bool RenameFolder(string shareId, string from, string to)
    {
        from = FolderPath.Normalize(from); to = FolderPath.Normalize(to);
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;
        if (FolderPath.IsUnder(to, from)) return false; // cannot move a folder into itself
        return Organize(shareId, "folder renamed", (def, items) =>
        {
            foreach (var i in items)
            {
                var f = FolderOf(def, i);
                if (FolderPath.IsUnder(f, from)) def.ItemFolders[i.Id] = FolderPath.Rebase(f, from, to);
            }
            def.Folders = def.Folders.Select(f => FolderPath.IsUnder(f, from) ? FolderPath.Rebase(f, from, to) : f).ToList();
            def.Folders.Add(to);
        });
    }

    /// <summary>Removes a folder; its videos and sub-folders move up one level. Files on disk are untouched.</summary>
    public bool DeleteFolder(string shareId, string path)
    {
        path = FolderPath.Normalize(path);
        if (path.Length == 0) return false;
        var parent = FolderPath.Parent(path);
        return Organize(shareId, "folder removed", (def, items) =>
        {
            foreach (var i in items)
            {
                var f = FolderOf(def, i);
                if (FolderPath.IsUnder(f, path)) def.ItemFolders[i.Id] = FolderPath.Rebase(f, path, parent);
            }
            def.Folders = def.Folders.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
                                     .Select(f => FolderPath.IsUnder(f, path) ? FolderPath.Rebase(f, path, parent) : f).ToList();
        });
    }

    public static string RootName(string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) name = trimmed.Replace(":", "").Replace("\\", "").Replace("/", "");
        return string.IsNullOrEmpty(name) ? "root" : name;
    }

    public static string ShareLock(string shareId) => "s:" + shareId;
    public static string ItemLock(string itemId) => "i:" + itemId;

    // ------------------------------------------------------------------ versioning

    private void Bump(string reason)
    {
        var v = Interlocked.Increment(ref _version);
        try { AtomicFile.Write(_versionPath, v.ToString()); } catch (Exception ex) { _log.LogWarning(ex, "version persist failed"); }
        _log.LogDebug("catalog version {Version} ({Reason})", v, reason);
        _events.Publish("catalog", new { version = v, reason });
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ share management

    public ShareDefinitionDto Upsert(ShareUpsertDto dto)
    {
        var name = string.IsNullOrWhiteSpace(dto.Name) ? SuggestName(dto.Roots) : dto.Name.Trim();
        var roots = dto.Roots.Select(r => r.Trim()).Where(r => r.Length > 0)
            .Select(r => { try { return Path.GetFullPath(r); } catch { return r; } })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ShareDefinition def = null!;
        var isNew = false;
        _config.Update(cfg =>
        {
            var list = new List<ShareDefinition>(cfg.Shares);
            var existing = dto.Id is null ? null : list.FirstOrDefault(s => s.Id == dto.Id);
            if (existing is null)
            {
                isNew = true;
                def = new ShareDefinition { Id = Passwords.ShortId(6), Name = name, Mode = dto.Mode, Roots = roots };
                if (dto.Mode == AccessMode.Password && !string.IsNullOrEmpty(dto.Password))
                    (def.PasswordHash, def.PasswordSalt) = Passwords.Hash(dto.Password);
                list.Add(def);
            }
            else
            {
                def = existing;
                def.Name = name;
                def.Roots = roots;
                var passwordChanged = false;
                if (dto.Password is not null)
                {
                    if (dto.Password.Length == 0) { def.PasswordHash = null; def.PasswordSalt = null; }
                    else (def.PasswordHash, def.PasswordSalt) = Passwords.Hash(dto.Password);
                    passwordChanged = true;
                }
                if (def.Mode != dto.Mode || passwordChanged) LockChanged?.Invoke(ShareLock(def.Id));
                def.Mode = dto.Mode;
                def.UpdatedAt = DateTimeOffset.UtcNow;
            }
            cfg.Shares = list;
        });
        if (isNew) LoadRuntime(def);
        else RebuildWatchers(def);
        Bump(isNew ? "share added" : "share updated");
        _ = RescanAsync(def.Id);
        return ToDto(def);
    }

    /// <summary>Raised when a lock's policy or password changed so grants can be revoked.</summary>
    public event Action<string>? LockChanged;

    public bool Remove(string id)
    {
        var found = false;
        _config.Update(cfg =>
        {
            var list = new List<ShareDefinition>(cfg.Shares);
            found = list.RemoveAll(s => s.Id == id) > 0;
            cfg.Shares = list;
        });
        if (!found) return false;
        if (_shares.TryRemove(id, out var rt))
        {
            foreach (var itemId in rt.Items.Keys) _itemShare.TryRemove(itemId, out _);
            rt.Dispose();
        }
        try { File.Delete(IndexPath(id)); } catch { }
        LockChanged?.Invoke(ShareLock(id));
        Bump("share removed");
        return true;
    }

    public bool SetOverride(string shareId, string itemId, ItemOverrideUpsertDto dto)
    {
        var ok = false;
        _config.Update(cfg =>
        {
            var def = cfg.Shares.FirstOrDefault(s => s.Id == shareId);
            if (def is null) return;
            var map = new Dictionary<string, ItemOverride>(def.Overrides);
            if (dto.Mode == ItemAccessMode.Inherit) map.Remove(itemId);
            else
            {
                map.TryGetValue(itemId, out var prev);
                var ov = new ItemOverride { Mode = dto.Mode, PasswordHash = prev?.PasswordHash, PasswordSalt = prev?.PasswordSalt };
                if (dto.Password is not null)
                {
                    if (dto.Password.Length == 0) { ov.PasswordHash = null; ov.PasswordSalt = null; }
                    else (ov.PasswordHash, ov.PasswordSalt) = Passwords.Hash(dto.Password);
                }
                map[itemId] = ov;
            }
            def.Overrides = map;
            def.UpdatedAt = DateTimeOffset.UtcNow;
            ok = true;
        });
        if (!ok) return false;
        LockChanged?.Invoke(ItemLock(itemId));
        Bump("item policy changed");
        return true;
    }

    public static string SuggestName(IEnumerable<string> roots)
    {
        var first = roots.FirstOrDefault();
        if (first is null) return "My videos";
        return File.Exists(first) ? Path.GetFileNameWithoutExtension(first) : RootName(first);
    }

    public ShareDefinitionDto ToDto(ShareDefinition s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Mode = s.Mode,
        HasPassword = s.PasswordHash is not null,
        Roots = s.Roots.ToList(),
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        ItemCount = ItemCount(s.Id),
        TotalBytes = TotalBytes(s.Id),
        Scanning = IsScanning(s.Id),
        Folders = AllFolders(s, Items(s.Id)),
        Overrides = s.Overrides.ToDictionary(k => k.Key, v => new ItemOverrideDto { Mode = v.Value.Mode, HasPassword = v.Value.PasswordHash is not null }),
    };

    public List<LocalItemDto> LocalItems(string shareId)
    {
        var def = GetShare(shareId);
        if (def is null) return new();
        return Items(shareId).Select(i =>
        {
            def.Overrides.TryGetValue(i.Id, out var ov);
            var dto = new LocalItemDto
            {
                FullPath = FullPath(def, i),
                OverrideMode = ov?.Mode ?? ItemAccessMode.Inherit,
            };
            Fill(dto, def, i, _ => true);
            return dto;
        }).OrderByDescending(i => i.AddedAt).ToList();
    }

    // ------------------------------------------------------------------ catalog for peers

    public bool IsShareUnlocked(ShareDefinition share, Func<string, bool> hasGrant)
        => share.Mode == AccessMode.Public || hasGrant(ShareLock(share.Id));

    public bool IsItemAccessible(ShareDefinition share, ItemRecord item, Func<string, bool> hasGrant)
    {
        if (!IsShareUnlocked(share, hasGrant)) return false;
        if (!share.Overrides.TryGetValue(item.Id, out var ov)) return true;
        return ov.Mode is ItemAccessMode.Inherit or ItemAccessMode.Public || hasGrant(ItemLock(item.Id));
    }

    private void Fill(ItemDto dto, ShareDefinition share, ItemRecord i, Func<string, bool> hasGrant)
    {
        dto.Id = i.Id;
        dto.ShareId = share.Id;
        dto.Name = i.Name;
        dto.RelPath = RelPath(share, i);
        dto.Folder = FolderOf(share, i);
        dto.Size = i.Size;
        dto.ModifiedAt = i.ModifiedAt;
        dto.AddedAt = i.AddedAt;
        dto.DurationMs = i.DurationMs;
        dto.Width = i.Width;
        dto.Height = i.Height;
        if (share.Overrides.TryGetValue(i.Id, out var ov) && ov.Mode is ItemAccessMode.Password or ItemAccessMode.Ask)
        {
            dto.Lock = new LockInfo { Id = ItemLock(i.Id), Mode = ov.Mode == ItemAccessMode.Password ? AccessMode.Password : AccessMode.Ask };
            dto.Unlocked = hasGrant(dto.Lock.Id);
        }
        else dto.Unlocked = true;
    }

    public CatalogDto BuildCatalog(string nodeId, string nodeName, string userName, bool paused, Func<string, bool> hasGrant)
    {
        var cat = new CatalogDto
        {
            NodeId = nodeId, NodeName = nodeName, UserName = userName, Host = Environment.MachineName,
            Version = Version, GeneratedAt = DateTimeOffset.UtcNow,
        };
        if (paused) return cat;
        foreach (var share in Shares)
        {
            var items = Items(share.Id).ToList();
            var unlocked = IsShareUnlocked(share, hasGrant);
            var dto = new ShareDto
            {
                Id = share.Id, Name = share.Name, Mode = share.Mode,
                Lock = share.Mode == AccessMode.Public ? null : new LockInfo { Id = ShareLock(share.Id), Mode = share.Mode },
                Unlocked = unlocked,
                ItemCount = items.Count,
                TotalBytes = items.Sum(i => i.Size),
                CreatedAt = share.CreatedAt,
                UpdatedAt = share.UpdatedAt,
                NewestItemAt = items.Count == 0 ? null : items.Max(i => i.AddedAt),
            };
            if (unlocked)
            {
                foreach (var i in items)
                {
                    var idto = new ItemDto();
                    Fill(idto, share, i, hasGrant);
                    dto.Items.Add(idto);
                }
                dto.Folders = AllFolders(share, items);
            }
            cat.Shares.Add(dto);
        }
        return cat;
    }

    public (AccessMode Mode, string? Hash, string? Salt, string Label, string ShareName, string? ItemName)? ResolveLock(string lockId)
    {
        if (lockId.StartsWith("s:", StringComparison.Ordinal))
        {
            var s = GetShare(lockId[2..]);
            if (s is null) return null;
            return (s.Mode, s.PasswordHash, s.PasswordSalt, s.Name, s.Name, null);
        }
        if (lockId.StartsWith("i:", StringComparison.Ordinal))
        {
            if (!TryGetItem(lockId[2..], out var share, out var item)) return null;
            if (!share.Overrides.TryGetValue(item.Id, out var ov) || ov.Mode is ItemAccessMode.Inherit or ItemAccessMode.Public)
                return (AccessMode.Public, null, null, item.Name, share.Name, item.Name);
            var mode = ov.Mode == ItemAccessMode.Password ? AccessMode.Password : AccessMode.Ask;
            return (mode, ov.PasswordHash, ov.PasswordSalt, item.Name, share.Name, item.Name);
        }
        return null;
    }

    // ------------------------------------------------------------------ indexing

    private string IndexPath(string shareId) => Path.Combine(_dir, shareId + ".json");

    private void LoadRuntime(ShareDefinition share)
    {
        var rt = new ShareRuntime { ShareId = share.Id };
        try
        {
            var p = IndexPath(share.Id);
            if (File.Exists(p))
            {
                var idx = Json.Deserialize<ShareIndex>(File.ReadAllText(p));
                if (idx is not null)
                {
                    rt.ScannedAt = idx.ScannedAt;
                    rt.FirstScanDone = idx.FirstScanDone;
                    foreach (var i in idx.Items) { rt.Items[i.Id] = i; _itemShare[i.Id] = share.Id; }
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "index load failed for {Share}", share.Id); }
        _shares[share.Id] = rt;
        RebuildWatchers(share);
    }

    private void PersistIndex(ShareRuntime rt)
    {
        try
        {
            var idx = new ShareIndex { ShareId = rt.ShareId, Items = rt.Items.Values.ToList(), ScannedAt = rt.ScannedAt, FirstScanDone = rt.FirstScanDone };
            AtomicFile.Write(IndexPath(rt.ShareId), Json.Serialize(idx));
        }
        catch (Exception ex) { _log.LogWarning(ex, "index persist failed for {Share}", rt.ShareId); }
    }

    private void RebuildWatchers(ShareDefinition share)
    {
        if (!_shares.TryGetValue(share.Id, out var rt)) return;
        foreach (var w in rt.Watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        rt.Watchers.Clear();
        foreach (var root in share.Roots)
        {
            try
            {
                var dir = Directory.Exists(root) ? root : Path.GetDirectoryName(root);
                if (dir is null || !Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = Directory.Exists(root),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                FileSystemEventHandler h = (_, e) => { if (IsInteresting(e.FullPath)) ScheduleRescan(share.Id); };
                w.Created += h; w.Deleted += h; w.Changed += h;
                w.Renamed += (_, e) => { if (IsInteresting(e.FullPath) || IsInteresting(e.OldFullPath)) ScheduleRescan(share.Id); };
                w.Error += (_, _) => ScheduleRescan(share.Id);
                w.EnableRaisingEvents = true;
                rt.Watchers.Add(w);
            }
            catch (Exception ex) { _log.LogDebug(ex, "watcher failed for {Root}", root); }
        }
    }

    // Video files, or extension-less paths (folders being created/renamed/deleted).
    private static bool IsInteresting(string path)
        => VideoTypes.IsVideo(path) || Path.GetExtension(path).Length == 0;

    private void ScheduleRescan(string shareId)
    {
        if (!_shares.TryGetValue(shareId, out var rt)) return;
        rt.Debounce ??= new Timer(_ => _ = RescanAsync(shareId), null, Timeout.Infinite, Timeout.Infinite);
        rt.Debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    public Task RescanAllAsync() => Task.WhenAll(Shares.Select(s => RescanAsync(s.Id)));

    public Task RescanAsync(string shareId)
    {
        var def = GetShare(shareId);
        if (def is null || !_shares.TryGetValue(shareId, out var rt)) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            if (!await rt.ScanLock.WaitAsync(0)) return; // scan already running; the watcher will reschedule if needed
            rt.Scanning = true;
            _events.Publish("shareScan", new { shareId, scanning = true });
            try { Scan(rt, def); }
            catch (Exception ex) { _log.LogWarning(ex, "scan failed for {Share}", shareId); }
            finally
            {
                rt.Scanning = false;
                rt.ScanLock.Release();
                _events.Publish("shareScan", new { shareId, scanning = false });
            }
        });
    }

    private void Scan(ShareRuntime rt, ShareDefinition def)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>();
        var added = 0; var updated = 0;
        var firstScan = !rt.FirstScanDone;

        for (var r = 0; r < def.Roots.Count; r++)
        {
            var root = def.Roots[r];
            IEnumerable<(string full, string sub)> files;
            try
            {
                if (File.Exists(root))
                {
                    files = VideoTypes.IsVideo(root) ? new[] { (root, "") } : Array.Empty<(string, string)>();
                }
                else if (Directory.Exists(root))
                {
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
                        MaxRecursionDepth = 48,
                    };
                    files = Directory.EnumerateFiles(root, "*", opts)
                        .Where(VideoTypes.IsVideo)
                        .Select(f => (f, Path.GetRelativePath(root, f)));
                }
                else continue;
            }
            catch (Exception ex) { _log.LogDebug(ex, "enumerate failed {Root}", root); continue; }

            foreach (var (full, sub) in files)
            {
                FileInfo fi;
                try { fi = new FileInfo(full); if (!fi.Exists) continue; }
                catch { continue; }
                var id = ItemId(def.Id, r, sub.Length == 0 ? Path.GetFileName(full) : sub);
                seen.Add(id);
                var mtime = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                if (rt.Items.TryGetValue(id, out var existing))
                {
                    if (existing.Size != fi.Length || existing.ModifiedAt != mtime || existing.Name != fi.Name)
                    {
                        existing.Size = fi.Length; existing.ModifiedAt = mtime; existing.Name = fi.Name;
                        existing.Probed = false; existing.DurationMs = null; existing.Width = null; existing.Height = null;
                        updated++;
                    }
                }
                else
                {
                    DateTimeOffset addedAt = now;
                    if (firstScan)
                    {
                        var created = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero);
                        addedAt = created < now ? (created > mtime ? created : mtime) : now;
                        if (addedAt > now) addedAt = now;
                    }
                    var rec = new ItemRecord
                    {
                        Id = id, Root = r, SubPath = sub, Name = fi.Name, Size = fi.Length,
                        ModifiedAt = mtime, AddedAt = addedAt,
                    };
                    rt.Items[id] = rec;
                    _itemShare[id] = def.Id;
                    added++;
                }
            }
        }

        var removed = 0;
        foreach (var id in rt.Items.Keys.ToList())
        {
            if (!seen.Contains(id) && rt.Items.TryRemove(id, out _)) { _itemShare.TryRemove(id, out _); removed++; }
        }

        rt.ScannedAt = now;
        rt.FirstScanDone = true;
        PersistIndex(rt);
        ApplyPendingPlacement(def, rt);
        _log.LogInformation("scan '{Name}': {Count} items (+{Added} ~{Updated} -{Removed}) in {Ms} ms",
            def.Name, rt.Items.Count, added, updated, removed, sw.ElapsedMilliseconds);
        if (added + updated + removed > 0 || firstScan) Bump("scan");
        else _events.Publish("shareScan", new { shareId = def.Id, scanning = false });
    }

    private static string ItemId(string shareId, int root, string subPath)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{shareId}|{root}|{subPath.ToLowerInvariant()}"));
        return Convert.ToHexString(h.AsSpan(0, 10)).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ probing (duration / resolution)

    public List<(ShareDefinition Share, ItemRecord Item)> UnprobedItems(int max)
    {
        var list = new List<(ShareDefinition, ItemRecord)>();
        foreach (var share in Shares)
        {
            foreach (var i in Items(share.Id))
            {
                if (i.Probed) continue;
                list.Add((share, i));
                if (list.Count >= max) return list;
            }
        }
        return list;
    }

    public void ApplyProbes(IEnumerable<(string ItemId, ShellMedia.Probe Probe)> results)
    {
        var touched = new HashSet<string>();
        foreach (var (itemId, probe) in results)
        {
            if (!TryGetItem(itemId, out var share, out var item)) continue;
            item.DurationMs = probe.DurationMs; item.Width = probe.Width; item.Height = probe.Height; item.Probed = true;
            touched.Add(share.Id);
        }
        foreach (var id in touched) if (_shares.TryGetValue(id, out var rt)) PersistIndex(rt);
        if (touched.Count > 0) Bump("probe");
    }

    public void Dispose()
    {
        foreach (var rt in _shares.Values) rt.Dispose();
    }
}
