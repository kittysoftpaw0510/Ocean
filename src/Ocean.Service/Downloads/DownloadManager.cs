using System.Diagnostics;
using System.Net;
using Ocean.Core;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Downloads;

internal sealed class DownloadJob
{
    public string Id { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long Received { get; set; }
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }
    public string Destination { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NotBefore { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public double SpeedBps { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? Cts { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long WindowStart { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long WindowBytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long LastNotify { get; set; }

    public DownloadJobDto ToDto() => new()
    {
        Id = Id, PeerId = PeerId, PeerName = PeerName, ItemId = ItemId, Name = Name, RelPath = RelPath, Size = Size,
        Received = Received, Status = Status, Error = Error, Destination = Destination, CreatedAt = CreatedAt,
        CompletedAt = CompletedAt, SpeedBps = Status == DownloadStatus.Running ? SpeedBps : 0,
    };
}

/// <summary>Resumable, bandwidth-friendly downloads (HTTP ranges over the encrypted peer channel).</summary>
public sealed class DownloadManager : BackgroundService
{
    private const string PartExt = ".oceanpart";
    private readonly PeerService _peers;
    private readonly ConfigStore _config;
    private readonly EventHub _events;
    private readonly ILogger<DownloadManager> _log;
    private readonly string _path;
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, Task> _running = new();

    private readonly StreamCache _cache;

    public DownloadManager(PeerService peers, StreamCache cache, ConfigStore config, EventHub events, ILogger<DownloadManager> log)
    {
        _peers = peers; _cache = cache; _config = config; _events = events; _log = log;
        _path = Path.Combine(OceanDefaults.DataDir, "downloads.json");
        try
        {
            if (File.Exists(_path))
            {
                var saved = Json.Deserialize<List<DownloadJob>>(File.ReadAllText(_path)) ?? new();
                foreach (var j in saved)
                {
                    if (j.Status == DownloadStatus.Running) j.Status = DownloadStatus.Queued;
                    _jobs.Add(j);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "downloads load failed"); }
    }

    private void Persist()
    {
        lock (_lock)
        {
            try
            {
                var keep = _jobs.OrderByDescending(j => j.CreatedAt).Take(500).ToList();
                AtomicFile.Write(_path, Json.Serialize(keep));
            }
            catch (Exception ex) { _log.LogWarning(ex, "downloads persist failed"); }
        }
    }

    private void Wake() { if (_wake.CurrentCount == 0) try { _wake.Release(); } catch { } }

    private void Notify(DownloadJob job) => _events.Publish("download", job.ToDto());

    public List<DownloadJobDto> List()
    {
        lock (_lock) return _jobs.OrderByDescending(j => j.CreatedAt).Select(j => j.ToDto()).ToList();
    }

    public async Task<List<DownloadJobDto>> EnqueueAsync(DownloadRequestDto req, CancellationToken ct)
    {
        var cat = _peers.Cached(req.PeerId) ?? await _peers.GetCatalogAsync(req.PeerId, true, ct)
                  ?? throw new PeerUnavailableException("Peer catalog is not available");
        IEnumerable<ItemDto> items;
        if (!string.IsNullOrEmpty(req.ShareId))
        {
            var folder = FolderPath.Normalize(req.Folder);
            items = cat.Shares.Where(s => s.Id == req.ShareId).SelectMany(s => s.Items)
                              .Where(i => FolderPath.IsUnder(i.Folder, folder));
        }
        else
        {
            var wanted = (req.ItemIds ?? new()).ToHashSet();
            items = cat.Shares.SelectMany(s => s.Items).Where(i => wanted.Contains(i.Id));
        }
        items = items.Where(i => i.Unlocked).ToList();

        var shareNames = cat.Shares.ToDictionary(s => s.Id, s => s.Name);
        // Folder segments use the folder rules; the file name is appended as-is (SafeNames handles it later,
        // keeping the extension even for very long names).
        string LocalRelPath(ItemDto i)
            => FolderPath.Combine(shareNames.GetValueOrDefault(i.ShareId, "Shared"), i.Folder) + "/" + i.Name.Replace('/', '_').Replace('\\', '_');

        var baseDir = string.IsNullOrWhiteSpace(req.Destination) ? _config.Current.DownloadDir : req.Destination!;
        var peerDir = Path.Combine(baseDir, SafeNames.FileName(cat.NodeName, "peer"));
        var created = new List<DownloadJobDto>();
        var skipped = 0;
        lock (_lock)
        {
            foreach (var item in items)
            {
                if (_jobs.Any(j => j.PeerId == req.PeerId && j.ItemId == item.Id && j.Status is DownloadStatus.Queued or DownloadStatus.Running))
                    continue;
                // Same structure as shown in Ocean: <node>\<share>\<folders...>\<file>
                var destination = Path.Combine(peerDir, SafeNames.RelPathToLocal(LocalRelPath(item)));
                // Already downloaded (same place, same size): don't fetch it again as "name (2)".
                try { if (File.Exists(destination) && new FileInfo(destination).Length == item.Size) { skipped++; continue; } } catch { }
                var job = new DownloadJob
                {
                    Id = Core.Crypto.Passwords.ShortId(6), PeerId = req.PeerId, PeerName = DisplayNames.Of(cat.UserName, cat.NodeName), ItemId = item.Id,
                    Name = item.Name, RelPath = LocalRelPath(item), Size = item.Size, Status = DownloadStatus.Queued,
                    Destination = destination,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _jobs.Add(job);
                created.Add(job.ToDto());
            }
        }
        Persist();
        foreach (var dto in created) _events.Publish("download", dto);
        Wake();
        if (skipped > 0) _log.LogInformation("{Skipped} video(s) already downloaded; not fetched again", skipped);
        return created;
    }

    public bool Cancel(string id)
    {
        DownloadJob? job;
        lock (_lock) job = _jobs.FirstOrDefault(j => j.Id == id);
        if (job is null || job.Status is not (DownloadStatus.Queued or DownloadStatus.Running)) return false;
        if (job.Status == DownloadStatus.Running) job.Cts?.Cancel();
        else { job.Status = DownloadStatus.Canceled; DeletePart(job); Persist(); Notify(job); }
        return true;
    }

    public bool Remove(string id)
    {
        DownloadJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.Status == DownloadStatus.Running) return false;
            _jobs.Remove(job);
        }
        DeletePart(job);
        Persist();
        _events.Publish("downloadRemoved", new { id });
        return true;
    }

    public bool Retry(string id)
    {
        DownloadJob? job;
        lock (_lock) job = _jobs.FirstOrDefault(j => j.Id == id);
        if (job is null || job.Status is not (DownloadStatus.Failed or DownloadStatus.Canceled)) return false;
        job.Status = DownloadStatus.Queued; job.Error = null; job.Attempts = 0; job.NotBefore = DateTimeOffset.MinValue;
        Persist(); Notify(job); Wake();
        return true;
    }

    public int ClearCompleted()
    {
        List<DownloadJob> removed;
        lock (_lock)
        {
            removed = _jobs.Where(j => j.Status is DownloadStatus.Done or DownloadStatus.Failed or DownloadStatus.Canceled).ToList();
            foreach (var j in removed) _jobs.Remove(j);
        }
        foreach (var j in removed) { if (j.Status != DownloadStatus.Done) DeletePart(j); _events.Publish("downloadRemoved", new { id = j.Id }); }
        Persist();
        return removed.Count;
    }

    private static void DeletePart(DownloadJob job)
    {
        try { var p = job.Destination + PartExt; if (File.Exists(p)) File.Delete(p); } catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { StartPending(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "download scheduler"); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
        }
        Task[] running;
        lock (_lock) running = _running.Values.ToArray();
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private void StartPending(CancellationToken ct)
    {
        var max = Math.Clamp(_config.Current.MaxConcurrentDownloads, 1, 8);
        lock (_lock)
        {
            foreach (var done in _running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList()) _running.Remove(done);
            if (_running.Count >= max) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var job in _jobs.Where(j => j.Status == DownloadStatus.Queued && j.NotBefore <= now).OrderBy(j => j.CreatedAt))
            {
                if (_running.Count >= max) break;
                if (_running.ContainsKey(job.Id)) continue;
                if (!_peers.IsOnline(job.PeerId))
                {
                    // Wait (without using up retries) until the node is back; it continues automatically.
                    const string waiting = "Waiting for the node to come back online";
                    if (job.Error != waiting) { job.Error = waiting; Notify(job); }
                    continue;
                }
                if (job.Error is not null && job.Error.StartsWith("Waiting", StringComparison.Ordinal)) job.Error = null;
                job.Status = DownloadStatus.Running;
                job.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _running[job.Id] = Task.Run(() => RunJob(job, ct), CancellationToken.None);
            }
        }
    }

    private async Task RunJob(DownloadJob job, CancellationToken serviceCt)
    {
        var token = job.Cts!.Token;
        var sw = Stopwatch.StartNew();
        job.WindowStart = 0; job.WindowBytes = 0; job.LastNotify = 0;
        Notify(job);
        try
        {
            var dir = Path.GetDirectoryName(job.Destination)!;
            Directory.CreateDirectory(dir);
            var part = job.Destination + PartExt;
            long received = 0;
            if (File.Exists(part)) received = new FileInfo(part).Length;
            if (job.Size > 0 && received > job.Size) { File.Delete(part); received = 0; }

            var item = _peers.FindItem(job.PeerId, job.ItemId);
            if (item is { Size: > 0 } && (job.Size <= 0 || job.Size == item.Size))
            {
                // Known size: whatever was already watched (stream cache) is copied from disk, only the rest comes
                // over the network, marked as a download so the owner lets people who are watching go first.
                job.Size = item.Size;
                if (received == job.Size) { Finish(job, part); return; }
                job.Received = received;
                await using (var fs = new FileStream(part, received > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var progress = new ProgressStream(fs, n => Progress(job, n)))
                    await _cache.CopyAsync(job.PeerId, item, received, job.Size - 1, progress, fill: false, download: true, token);
            }
            else
            {
                using var resp = await _peers.OpenStreamAsync(job.PeerId, job.ItemId, received > 0 ? $"bytes={received}-" : null, token, download: true);
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && job.Size > 0 && received == job.Size)
                {
                    Finish(job, part); return;
                }
                if (!resp.IsSuccessStatusCode) throw await PeerStatusException.FromAsync(resp);

                var append = resp.StatusCode == HttpStatusCode.PartialContent && received > 0;
                if (!append) received = 0;
                if (resp.Content.Headers.ContentRange?.Length is { } total) job.Size = total;
                else if (!append && resp.Content.Headers.ContentLength is { } len) job.Size = len;
                job.Received = received;

                await using (var fs = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var progress = new ProgressStream(fs, n => Progress(job, n)))
                await using (var src = await resp.Content.ReadAsStreamAsync(token))
                    await src.CopyToAsync(progress, 1 << 18, token);
            }
            if (job.Size > 0 && job.Received != job.Size)
                throw new IOException($"Transfer ended early ({Format.Bytes(job.Received)} of {Format.Bytes(job.Size)})");
            Finish(job, part);
            _cache.Forget(job.PeerId, job.ItemId); // the downloaded file replaces the cached bytes (and plays from disk)
            _log.LogInformation("downloaded '{Name}' from {Peer} ({Size}) in {Sec:0.0}s", job.Name, job.PeerName, Format.Bytes(job.Size), sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (serviceCt.IsCancellationRequested)
        {
            job.Status = DownloadStatus.Queued; // resume after restart
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadStatus.Canceled;
            DeletePart(job);
        }
        catch (PeerStatusException ex) when (ex.Status == HttpStatusCode.ServiceUnavailable && ex.Error == PeerHeaders.Busy)
        {
            // The owner is already sending enough downloads: wait our turn (doesn't count as a failed attempt).
            job.Status = DownloadStatus.Queued;
            job.NotBefore = DateTimeOffset.UtcNow + (ex.RetryAfter ?? TimeSpan.FromSeconds(15));
            job.Error = $"Waiting: {job.PeerName} is busy sending other downloads";
        }
        catch (PeerStatusException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            job.Status = DownloadStatus.Failed;
            job.Error = ex.Status == HttpStatusCode.Forbidden ? "Access denied (the item is locked)" : "The file is no longer shared";
            _log.LogWarning("download failed '{Name}': {Msg}", job.Name, job.Error);
        }
        catch (Exception ex)
        {
            job.Attempts++;
            var transient = ex is HttpRequestException or IOException or PeerUnavailableException
                            or PeerStatusException { Status: >= HttpStatusCode.InternalServerError };
            if (transient && job.Attempts < 4)
            {
                job.Status = DownloadStatus.Queued;
                job.NotBefore = DateTimeOffset.UtcNow.AddSeconds(5 * job.Attempts);
                job.Error = ex.Message + " (retrying)";
            }
            else
            {
                job.Status = DownloadStatus.Failed;
                job.Error = ex.Message;
                _log.LogWarning("download failed '{Name}': {Msg}", job.Name, ex.Message);
            }
        }
        finally
        {
            job.SpeedBps = 0;
            job.Cts?.Dispose(); job.Cts = null;
            Persist();
            Notify(job);
            Wake();
        }
    }

    /// <summary>Bytes written to the partial file: received total, smoothed speed, a UI update twice a second.</summary>
    private void Progress(DownloadJob job, int n)
    {
        job.Received += n;
        job.WindowBytes += n;
        var now = Stopwatch.GetTimestamp();
        if (job.WindowStart == 0) job.WindowStart = now;
        var winSec = (now - job.WindowStart) / (double)Stopwatch.Frequency;
        if (winSec >= 1)
        {
            var inst = job.WindowBytes / winSec;
            job.SpeedBps = job.SpeedBps <= 0 ? inst : job.SpeedBps * 0.6 + inst * 0.4;
            job.WindowStart = now; job.WindowBytes = 0;
        }
        if ((now - job.LastNotify) / (double)Stopwatch.Frequency >= 0.5) { job.LastNotify = now; Notify(job); }
    }

    /// <summary>The finished download of this video, if it is still on disk unchanged: played from there instead of the network.</summary>
    public string? CompletedCopy(string peerId, string itemId, long size)
    {
        string? path;
        lock (_lock) path = _jobs.LastOrDefault(j => j.Status == DownloadStatus.Done && j.PeerId == peerId && j.ItemId == itemId)?.Destination;
        if (path is null) return null;
        try { return new FileInfo(path) is { Exists: true } f && f.Length == size ? path : null; }
        catch { return null; }
    }

    private static void Finish(DownloadJob job, string part)
    {
        var final = SafeNames.Unique(job.Destination);
        File.Move(part, final, overwrite: false);
        job.Destination = final;
        job.Status = DownloadStatus.Done;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Error = null;
    }
}

/// <summary>Write-through stream that reports how many bytes went through.</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<int> _progress;
    public ProgressStream(Stream inner, Action<int> progress) { _inner = inner; _progress = progress; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); _progress(count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        _progress(buffer.Length);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    // the wrapped file stream is owned (and disposed) by the caller
}
