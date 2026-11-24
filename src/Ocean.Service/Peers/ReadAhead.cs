using Ocean.Core.Protocol;
using Ocean.Service.Downloads;

namespace Ocean.Service.Peers;

/// <summary>
/// Buffers a little ahead of the player, as a browser does with a video on the web: while a video from another PC is
/// open, about 30 s from the playhead on are fetched into the stream cache (4 to 96 MB), topped up once less than 20 s
/// are left (also while paused, up to that much). A seek starts again from there. The player then reads from the
/// disk; its own requests wait for blocks being fetched instead of fetching them twice. The app tells where the
/// playhead is about once a second (and gets back what is loaded, for the seek bar); when it stops telling (the
/// player closed), reading ahead stops, and what was read ahead but never reached is let go at once (its disk space
/// given back): only what was watched stays in the cache. Nothing is read ahead with the stream cache off.
/// </summary>
public sealed class ReadAhead : IDisposable
{
    private const double AheadSeconds = 30, RefillBelowSeconds = 20;
    private const long MinAhead = 4L * 1024 * 1024, MaxAhead = 96L * 1024 * 1024, Chunk = 4L * 1024 * 1024;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);

    private sealed class Session
    {
        public required string PeerId;
        public required ItemDto Item;
        public long At;
        /// <summary>The blocks the playhead went through (played): those stay in the cache, the rest read ahead goes.</summary>
        public readonly HashSet<int> Played = new();
        /// <summary>The blocks this read-ahead brought (they weren't in the cache before).</summary>
        public readonly HashSet<int> Brought = new();
        public DateTime Touched = DateTime.UtcNow;
        public readonly StreamCache.AheadMark Mark = new();
        public readonly CancellationTokenSource Cts = new();
    }

    private readonly PeerService _peers;
    private readonly StreamCache _cache;
    private readonly DownloadManager _downloads;
    private readonly ILogger<ReadAhead> _log;
    private readonly object _lock = new();
    private Session? _s;

    public ReadAhead(PeerService peers, StreamCache cache, DownloadManager downloads, ILogger<ReadAhead> log)
    {
        _peers = peers; _cache = cache; _downloads = downloads; _log = log;
    }

    /// <summary>The player is at <paramref name="seconds"/> in this video: read ahead from there; what is loaded now.</summary>
    public BufferDto Touch(string peerId, string itemId, double seconds)
    {
        var item = _peers.FindItem(peerId, itemId);
        if (item is not { Size: > 0 } || item.Lock is not null && !item.Unlocked) return new BufferDto();
        // on this PC already (my own copy, a finished download): all of it
        if (_peers.OwnCopyPath(item) is not null || _downloads.CompletedCopy(peerId, item.Id, item.Size) is not null)
            return new BufferDto { Size = item.Size, Ranges = new() { new[] { 0L, item.Size - 1 } }, Local = true };
        var at = Position(item, seconds);
        if (_cache.Enabled)
        {
            Session? old = null, s;
            lock (_lock)
            {
                if (_s is { } cur && cur.PeerId == peerId && cur.Item.Id == item.Id && cur.Item.Size == item.Size && !cur.Cts.IsCancellationRequested) s = cur;
                else { old = _s; _s = s = new Session { PeerId = peerId, Item = item }; }
                // played: from where it was to where it is when it moved on as playing does (forward, not a jump)
                var bs = StreamCache.BlockSize;
                lock (s.Played)
                {
                    var from = at >= s.At && at - s.At <= Window(item) ? s.At : at;
                    for (var b = (int)(from / bs); (long)b * bs <= at; b++) s.Played.Add(b);
                }
                s.At = at; s.Touched = DateTime.UtcNow;
            }
            if (old is not null) Stop(old);
            Start(s); // (once per session)
        }
        var ranges = _cache.Cached(peerId, item).Select(r => new[] { r.From, r.To }).ToList();
        var ahead = ranges.FirstOrDefault(r => r[0] <= at && r[1] >= at) is { } run ? run[1] - at + 1 : 0;
        return new BufferDto { Size = item.Size, Ranges = ranges, AheadSeconds = item.DurationMs is > 0 ? ahead * (item.DurationMs.Value / 1000.0) / item.Size : 0 };
    }

    /// <summary>The byte at a time in the video (its average bit rate: near enough for reading ahead).</summary>
    private static long Position(ItemDto item, double seconds)
        => item.DurationMs is > 0 ? Math.Clamp((long)(item.Size * (seconds * 1000 / item.DurationMs.Value)), 0, item.Size - 1) : 0;

    private long Window(ItemDto item)
    {
        var rate = item.DurationMs is > 0 ? item.Size * 1000.0 / item.DurationMs.Value : 0;
        var want = rate > 0 ? (long)(rate * AheadSeconds) : 16L * 1024 * 1024;
        // (never more than a tenth of the cache: what was watched before stays)
        return Math.Clamp(want, MinAhead, Math.Max(MinAhead, Math.Min(MaxAhead, _cache.Capacity / 10)));
    }

    private readonly HashSet<Session> _running = new();

    private void Start(Session s)
    {
        lock (_lock) { if (!_running.Add(s)) return; }
        _cache.SetAhead(s.PeerId, s.Item, s.Mark);
        _ = Task.Run(() => RunAsync(s));
    }

    private void Stop(Session s)
    {
        try { s.Cts.Cancel(); } catch { }
    }

    private async Task RunAsync(Session s)
    {
        var ct = s.Cts.Token;
        var item = s.Item;
        var window = Window(item);
        // topped up only once less than this is left (fetching in bursts, not a trickle all the time)
        var refill = (long)(window * (RefillBelowSeconds / AheadSeconds));
        var fetched = 0L;
        _log.LogInformation("reading ahead of the player: {Name} from {Peer} (about {MB} MB ahead)", item.Name, _peers.NameOf(s.PeerId), window / (1024 * 1024));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow - s.Touched > Idle) break;   // the player stopped telling: closed
                var at = Interlocked.Read(ref s.At);
                // enough loaded ahead (more than the refill mark, without a gap): nothing to do now
                var gapAt = _cache.FirstMissing(s.PeerId, item, at, at + refill - 1);
                var next = gapAt is null ? null : _cache.FirstMissing(s.PeerId, item, at, at + window - 1);
                if (next is not { } from || !_peers.IsOnline(s.PeerId))
                {
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }
                var to = Math.Min(item.Size - 1, from + Chunk - 1);
                // (only up to the next part already here)
                var gap = _cache.Cached(s.PeerId, item).FirstOrDefault(r => r.From > from);
                if (gap.To > 0 && gap.From - 1 < to) to = gap.From - 1;
                Interlocked.Exchange(ref s.Mark.FetchingFrom, from); Interlocked.Exchange(ref s.Mark.FetchingTo, to);
                try
                {
                    await _cache.CopyAsync(s.PeerId, item, from, to, Stream.Null, fill: true, download: false, ct, ahead: true);
                    fetched += to - from + 1;
                    lock (s.Brought) for (var b = (int)(from / StreamCache.BlockSize); (long)b * StreamCache.BlockSize <= to; b++) s.Brought.Add(b);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (PeerStatusException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Gone)
                {
                    _log.LogInformation("reading ahead of {Name} stopped: {Why}", item.Name, ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogDebug("reading ahead of {Name}: {Msg}", item.Name, ex.Message);
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                }
                finally { Interlocked.Exchange(ref s.Mark.FetchingFrom, -1); Interlocked.Exchange(ref s.Mark.FetchingTo, -1); }
            }
        }
        finally
        {
            _cache.SetAhead(s.PeerId, item, null);
            lock (_lock) { _running.Remove(s); if (ReferenceEquals(_s, s)) _s = null; }
            s.Cts.Cancel();
            // what was read ahead but never reached goes (the block the playhead is in, and the next, stay)
            List<int> unwatched;
            var here = (int)(Interlocked.Read(ref s.At) / StreamCache.BlockSize);
            HashSet<int> played;
            lock (s.Played) played = new HashSet<int>(s.Played) { here, here + 1 };
            lock (s.Brought) unwatched = s.Brought.Where(b => !played.Contains(b)).ToList();
            var freed = _cache.Drop(s.PeerId, item, unwatched);
            _log.LogInformation("reading ahead of {Name} ended ({MB:0} MB read ahead; {Freed:0} MB never reached let go)", item.Name, fetched / (1024.0 * 1024), freed / (1024.0 * 1024));
        }
    }

    public void Dispose() { lock (_lock) if (_s is { } s) Stop(s); }
}
