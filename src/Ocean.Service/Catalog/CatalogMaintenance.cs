using Ocean.Core.Media;
using Ocean.Service.Peers;

namespace Ocean.Service.Catalog;

/// <summary>
/// Background housekeeping: initial and periodic rescans, duration/resolution probing,
/// thumbnail pre-generation (so first browsing by a peer is instant) and cache pruning.
/// </summary>
public sealed class CatalogMaintenance : BackgroundService
{
    private readonly CatalogService _catalog;
    private readonly ThumbnailService _thumbs;
    private readonly PeerService _peers;
    private readonly StaWorker _sta;
    private readonly ILogger<CatalogMaintenance> _log;

    public CatalogMaintenance(CatalogService catalog, ThumbnailService thumbs, PeerService peers, StaWorker sta, ILogger<CatalogMaintenance> log)
    {
        _catalog = catalog; _thumbs = thumbs; _peers = peers; _sta = sta; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(1500, ct);
        _ = _catalog.RescanAllAsync();
        var lastFull = DateTimeOffset.UtcNow;
        var lastPrune = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProbeBatchAsync(ct);
                didWork |= await PregenerateThumbsAsync(ct);

                if (DateTimeOffset.UtcNow - lastFull > TimeSpan.FromMinutes(15))
                {
                    lastFull = DateTimeOffset.UtcNow;
                    _ = _catalog.RescanAllAsync();
                }
                if (DateTimeOffset.UtcNow - lastPrune > TimeSpan.FromHours(6))
                {
                    lastPrune = DateTimeOffset.UtcNow;
                    _thumbs.Prune();
                    _peers.PruneThumbCache(TimeSpan.FromDays(30));
                }
                await Task.Delay(didWork ? 50 : 3000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "maintenance loop");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task<bool> ProbeBatchAsync(CancellationToken ct)
    {
        var batch = _catalog.UnprobedItems(25);
        if (batch.Count == 0) return false;
        var results = new List<(string, ShellMedia.Probe)>();
        foreach (var (share, item) in batch)
        {
            if (ct.IsCancellationRequested) break;
            var path = _catalog.FullPath(share, item);
            var probe = await _sta.RunAsync(() => ShellMedia.ProbeFile(path), ct);
            results.Add((item.Id, probe));
        }
        _catalog.ApplyProbes(results);
        return true;
    }

    private async Task<bool> PregenerateThumbsAsync(CancellationToken ct)
    {
        if (_sta.Pending > 2) return false;
        var n = 0;
        foreach (var share in _catalog.Shares)
        {
            foreach (var item in _catalog.Items(share.Id))
            {
                if (_thumbs.Tried(item)) continue;
                await _thumbs.GetOrCreateAsync(item.Id, ct);
                if (++n >= 10) return true;
            }
        }
        return n > 0;
    }
}
