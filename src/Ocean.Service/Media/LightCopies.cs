using System.Diagnostics;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Media;

/// <summary>
/// Lighter copies of my videos for watching: 720p at about 1.5 Mbps, made with ffmpeg. A copy is made once someone
/// has been watching a heavy video for a while (the viewer asks through <c>/p/light</c>), one at a time and at low
/// priority; viewers then get it as <c>{itemId}@light</c>. Copies live under <c>cache\light</c>, are kept for a
/// number of days after the last viewing and within a disk limit, and go when the original changes or leaves the
/// share. The original is never touched; downloads always get the original.
/// </summary>
public sealed class LightCopies : BackgroundService
{
    /// <summary>Sources lighter than this (and not above 720p) gain little: left alone.</summary>
    public const double MinSourceMbps = 2.0;
    /// <summary>A copy is kept only when it is at most this fraction of the original.</summary>
    public const double KeepIfSmallerThan = 0.6;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(30);

    public sealed class Entry
    {
        public string Key { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastWatched { get; set; }
        /// <summary>ready | failed | skipped</summary>
        public string State { get; set; } = "";
        public string? Reason { get; set; }
    }

    private readonly CatalogService _catalog;
    private readonly ConfigStore _config;
    private readonly ILogger<LightCopies> _log;
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private readonly List<string> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private string? _current;
    private int _percent;
    private CancellationTokenSource? _currentCts;

    /// <summary>ffmpeg.exe next to the service (shipped by the installer), in the repo's .tools folder, or on PATH.</summary>
    public static string? Ffmpeg { get; } = FindFfmpeg();

    public LightCopies(CatalogService catalog, ConfigStore config, ILogger<LightCopies> log)
    {
        _catalog = catalog; _config = config; _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "cache", "light");
        _indexPath = Path.Combine(_dir, "index.json");
        Directory.CreateDirectory(_dir);
        Load();
        _catalog.LightLookup = Lookup;
        _config.Changed += cfg =>
        {
            if (cfg.LightCopies) return;
            lock (_lock) { _queue.Clear(); _currentCts?.Cancel(); }
        };
    }

    public int ReadyCount { get { lock (_lock) return _index.Values.Count(e => e.State == "ready"); } }
    public long UsedBytes { get { lock (_lock) return _index.Values.Where(e => e.State == "ready").Sum(e => e.Size); } }

    // ------------------------------------------------------------------ what viewers and the catalog see

    /// <summary>How the copy of <paramref name="itemId"/> is doing; with <paramref name="enqueue"/>, asks for one.</summary>
    public LightStatusDto Request(string itemId, bool enqueue)
    {
        if (!_catalog.TryGetItem(itemId, out _, out var item)) return new LightStatusDto { State = "none" };
        var key = KeyOf(item);
        int pos;
        lock (_lock)
        {
            if (_index.TryGetValue(itemId, out var e) && e.Key == key)
            {
                if (e.State == "ready")
                {
                    if (enqueue) e.LastWatched = DateTimeOffset.UtcNow;
                    return new LightStatusDto { State = LightVariant.Ready, Percent = 100, Size = e.Size };
                }
                return new LightStatusDto { State = "none", Message = e.Reason };
            }
            if (_current == itemId) return new LightStatusDto { State = LightVariant.Preparing, Percent = _percent };
            pos = _queue.IndexOf(itemId);
            if (pos >= 0) return new LightStatusDto { State = "queued", Queue = pos + 1 };
            if (!enqueue) return new LightStatusDto { State = "none" };
            if (!_config.Current.LightCopies) return new LightStatusDto { State = "off" };
            if (Ffmpeg is null) return new LightStatusDto { State = "off", Message = "ffmpeg is not installed with Ocean on that PC" };
            _queue.Add(itemId);
            pos = _queue.Count;
        }
        _wake.Release();
        _catalog.NotifyLightChanged();
        return new LightStatusDto { State = "queued", Queue = pos };
    }

    /// <summary>For the catalog: "ready" with the size, "preparing", or nothing.</summary>
    private (string? State, long Size) Lookup(string itemId, ItemRecord item)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(itemId, out var e) && e.Key == KeyOf(item) && e.State == "ready") return (LightVariant.Ready, e.Size);
            if (_current == itemId || _queue.Contains(itemId)) return (LightVariant.Preparing, 0);
            return (null, 0);
        }
    }

    /// <summary>The copy's file when it is ready for this version of the video (and counts as watched now).</summary>
    public string? PathFor(string itemId, ItemRecord item)
    {
        var key = KeyOf(item);
        lock (_lock)
        {
            if (!_index.TryGetValue(itemId, out var e) || e.Key != key || e.State != "ready") return null;
            e.LastWatched = DateTimeOffset.UtcNow;
        }
        var path = PathOf(itemId, key);
        return File.Exists(path) ? path : null;
    }

    // ------------------------------------------------------------------ the work

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (Ffmpeg is null) _log.LogInformation("lighter copies: ffmpeg.exe not found, so none are made on this PC");
        else _log.LogInformation("lighter copies: ffmpeg at {Path}", Ffmpeg);
        Sweep();
        var lastSweep = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            string? next = null;
            lock (_lock)
            {
                if (_queue.Count > 0 && _config.Current.LightCopies && Ffmpeg is not null)
                {
                    next = _queue[0]; _queue.RemoveAt(0);
                    _current = next; _percent = 0;
                    _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
            }
            if (next is null)
            {
                try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
                if (DateTimeOffset.UtcNow - lastSweep > SweepEvery) { Sweep(); lastSweep = DateTimeOffset.UtcNow; }
                continue;
            }
            try { await ConvertAsync(next, _currentCts!.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { _log.LogInformation("lighter copy of {Item}: stopped", next); }
            catch (Exception ex)
            {
                _log.LogWarning("lighter copy of {Item} failed: {Msg}", next, ex.Message);
                if (_catalog.TryGetItem(next, out _, out var item)) Mark(next, KeyOf(item), "failed", 0, ex.Message);
            }
            finally
            {
                lock (_lock) { _current = null; _percent = 0; _currentCts?.Dispose(); _currentCts = null; }
                _catalog.NotifyLightChanged();
            }
        }
    }

    private async Task ConvertAsync(string itemId, CancellationToken ct)
    {
        if (!_catalog.TryGetItem(itemId, out var share, out var item)) return;
        var key = KeyOf(item);
        var src = _catalog.FullPath(share, item);
        var seconds = (item.DurationMs ?? 0) / 1000.0;
        if (seconds > 0)
        {
            var mbps = item.Size * 8 / seconds / 1e6;
            if (mbps < MinSourceMbps && (item.Height ?? 0) <= 720)
            {
                Mark(itemId, key, "skipped", 0, $"already light ({mbps:0.0} Mbps)");
                return;
            }
        }
        var dst = PathOf(itemId, key);
        var tmp = dst + ".part.mp4";
        try { File.Delete(tmp); } catch { }
        _log.LogInformation("lighter copy of {Name}: starting ({Size}, {Res})", item.Name, Format.Bytes(item.Size), Format.Resolution(item.Width, item.Height));

        var psi = new ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
                     "-i", src, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn",
                     "-vf", "scale=-2:'min(720,ih)'",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "25", "-maxrate", "1800k", "-bufsize", "3600k",
                     "-profile:v", "main", "-level", "4.0", "-pix_fmt", "yuv420p", "-g", "120",
                     "-c:a", "aac", "-b:a", "96k", "-ac", "2",
                     "-movflags", "+faststart", tmp,
                 }) psi.ArgumentList.Add(a);
        var clock = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var errors = new Queue<string>();
        var readErr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                if (errors.Count > 5) errors.Dequeue();
                errors.Enqueue(line);
            }
        });
        await using (ct.Register(() => { try { p.Kill(true); } catch { } }))
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (seconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us))
                    _percent = (int)Math.Clamp(us / 1e6 / seconds * 100, 0, 99);
            }
            await p.WaitForExitAsync(ct);
        }
        await readErr;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException(errors.Count > 0 ? string.Join(" | ", errors) : $"ffmpeg exit code {p.ExitCode}");
        }
        var size = new FileInfo(tmp).Length;
        if (size > item.Size * KeepIfSmallerThan)
        {
            try { File.Delete(tmp); } catch { }
            Mark(itemId, key, "skipped", 0, $"no real saving ({100.0 * size / item.Size:0}% of the original)");
            _log.LogInformation("lighter copy of {Name}: dropped, {Pct:0}% of the original", item.Name, 100.0 * size / item.Size);
            return;
        }
        File.Move(tmp, dst, true);
        Mark(itemId, key, "ready", size, null);
        _log.LogInformation("lighter copy of {Name}: {Size} ({Pct:0}% of the original) in {Sec:0} s ({Speed:0.0}x real time)",
            item.Name, Format.Bytes(size), 100.0 * size / item.Size, clock.Elapsed.TotalSeconds, seconds > 0 ? seconds / clock.Elapsed.TotalSeconds : 0);
        EnforceLimit();
    }

    private void Mark(string itemId, string key, string state, long size, string? reason)
    {
        lock (_lock)
        {
            _index[itemId] = new Entry { Key = key, Size = size, State = state, Reason = reason, CreatedAt = DateTimeOffset.UtcNow, LastWatched = DateTimeOffset.UtcNow };
            Save();
        }
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Drops copies whose video changed or left the share, old ones, and keeps the folder within its limit.</summary>
    private void Sweep()
    {
        var removed = 0;
        try
        {
            var keep = TimeSpan.FromDays(Math.Max(1, _config.Current.LightKeepDays));
            lock (_lock)
            {
                foreach (var (id, e) in _index.ToList())
                {
                    var stale = !_catalog.TryGetItem(id, out _, out var item) || e.Key != KeyOf(item)
                                || (e.State == "ready" && DateTimeOffset.UtcNow - e.LastWatched > keep);
                    if (!stale) continue;
                    try { File.Delete(PathOf(id, e.Key)); } catch { }
                    _index.Remove(id);
                    removed++;
                }
                var known = _index.Select(kv => Path.GetFileName(PathOf(kv.Key, kv.Value.Key))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(_dir, "*.mp4"))
                {
                    if (known.Contains(Path.GetFileName(f)) || (_current is not null && f.StartsWith(PathOf(_current, ""), StringComparison.OrdinalIgnoreCase))) continue;
                    try { File.Delete(f); removed++; } catch { }
                }
                if (removed > 0) Save();
            }
            EnforceLimit();
        }
        catch (Exception ex) { _log.LogDebug(ex, "lighter copies: sweep"); }
        if (removed > 0) _catalog.NotifyLightChanged();
    }

    /// <summary>The least recently watched copies go first when the folder is over its limit.</summary>
    private void EnforceLimit()
    {
        var cap = Math.Max(1, _config.Current.LightCacheGB) * 1024L * 1024 * 1024;
        var removed = 0;
        lock (_lock)
        {
            var ready = _index.Where(kv => kv.Value.State == "ready").OrderBy(kv => kv.Value.LastWatched).ToList();
            var total = ready.Sum(kv => kv.Value.Size);
            foreach (var (id, e) in ready)
            {
                if (total <= cap) break;
                try { File.Delete(PathOf(id, e.Key)); } catch { }
                _index.Remove(id);
                total -= e.Size;
                removed++;
            }
            if (removed > 0) Save();
        }
        if (removed > 0) _catalog.NotifyLightChanged();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            foreach (var kv in Json.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_indexPath)) ?? new())
                _index[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log.LogWarning(ex, "lighter copies: index load failed"); }
    }

    private void Save()
    {
        try { AtomicFile.Write(_indexPath, Json.Serialize(_index)); }
        catch (Exception ex) { _log.LogWarning(ex, "lighter copies: index save failed"); }
    }

    private static string KeyOf(ItemRecord item) => $"{item.ModifiedAt.UtcTicks}-{item.Size}";
    private string PathOf(string itemId, string key) => Path.Combine(_dir, $"{Safe(itemId)}-{key}.mp4");
    private static string Safe(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    private static string? FindFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            // a development build: the repo's .tools folder is a few levels up from bin\...
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var tools = Path.Combine(dir.FullName, ".tools", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(tools)) return tools;
            }
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }
}
