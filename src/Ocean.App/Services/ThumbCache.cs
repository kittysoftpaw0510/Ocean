using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace Ocean.App.Services;

/// <summary>In-memory thumbnail cache (decoded at tile size, frozen so it can be created off the UI thread).</summary>
public sealed class ThumbCache
{
    private const int MaxEntries = 1500;
    private readonly ApiClient _api;
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapImage?>>> _cache = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(6);

    public ThumbCache(ApiClient api) { _api = api; }

    public Task<BitmapImage?> GetAsync(string path)
    {
        var lazy = _cache.GetOrAdd(path, p => new Lazy<Task<BitmapImage?>>(() => LoadAsync(p)));
        if (lazy.IsValueCreated == false) _order.Enqueue(path);
        Trim();
        return lazy.Value;
    }

    public void Forget(string path) => _cache.TryRemove(path, out _);

    private async Task<BitmapImage?> LoadAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            var bytes = await _api.GetBytesAsync(path);
            if (bytes is null || bytes.Length == 0) { ScheduleRetry(path); return null; }
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 320;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            ScheduleRetry(path);
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A missing thumbnail may simply not be generated yet: allow a later retry.</summary>
    private void ScheduleRetry(string path)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(t => _cache.TryRemove(path, out var removed));
    }

    private void Trim()
    {
        while (_cache.Count > MaxEntries && _order.TryDequeue(out var old)) _cache.TryRemove(old, out _);
    }
}
