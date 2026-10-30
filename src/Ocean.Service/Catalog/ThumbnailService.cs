using System.Collections.Concurrent;
using Ocean.Core;
using Ocean.Core.Media;

namespace Ocean.Service.Catalog;

/// <summary>Generates and caches JPEG thumbnails for local items (on demand and in the background).</summary>
public sealed class ThumbnailService
{
    private readonly CatalogService _catalog;
    private readonly StaWorker _sta;
    private readonly ILogger<ThumbnailService> _log;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public ThumbnailService(CatalogService catalog, StaWorker sta, ILogger<ThumbnailService> log)
    {
        _catalog = catalog; _sta = sta; _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "thumbs");
        Directory.CreateDirectory(_dir);
    }

    private string JpegPath(ItemRecord item) => Path.Combine(_dir, $"{item.Id}-{item.ModifiedAt.UtcTicks}.jpg");
    private string NonePath(ItemRecord item) => Path.Combine(_dir, $"{item.Id}-{item.ModifiedAt.UtcTicks}.none");

    public bool Has(ItemRecord item) => File.Exists(JpegPath(item));

    public bool Tried(ItemRecord item) => File.Exists(JpegPath(item)) || File.Exists(NonePath(item));

    /// <summary>Returns the path of the cached JPEG, generating it if necessary; null when unavailable.</summary>
    public Task<string?> GetOrCreateAsync(string itemId, CancellationToken ct = default)
    {
        if (!_catalog.TryGetItem(itemId, out var share, out var item)) return Task.FromResult<string?>(null);
        var jpeg = JpegPath(item);
        if (File.Exists(jpeg)) return Task.FromResult<string?>(jpeg);
        if (File.Exists(NonePath(item))) return Task.FromResult<string?>(null);
        var full = _catalog.FullPath(share, item);
        return _inflight.GetOrAdd(itemId, k => Generate(item, full, jpeg, ct).ContinueWith(t =>
        {
            _inflight.TryRemove(itemId, out _);
            return t.IsCompletedSuccessfully ? t.Result : null;
        }, TaskScheduler.Default));
    }

    private async Task<string?> Generate(ItemRecord item, string fullPath, string jpeg, CancellationToken ct)
    {
        try
        {
            var bytes = await _sta.RunAsync(() => ShellMedia.Thumbnail(fullPath), ct);
            if (bytes is null || bytes.Length == 0)
            {
                try { File.WriteAllBytes(NonePath(item), Array.Empty<byte>()); } catch { }
                return null;
            }
            var tmp = jpeg + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, jpeg, overwrite: true);
            return jpeg;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "thumbnail failed for {Path}", fullPath);
            return null;
        }
    }

    /// <summary>Deletes cached thumbnails whose item no longer exists or whose file changed.</summary>
    public void Prune()
    {
        try
        {
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var share in _catalog.Shares)
                foreach (var item in _catalog.Items(share.Id))
                {
                    valid.Add(Path.GetFileName(JpegPath(item)));
                    valid.Add(Path.GetFileName(NonePath(item)));
                }
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !valid.Contains(name))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "thumbnail prune failed"); }
    }
}
