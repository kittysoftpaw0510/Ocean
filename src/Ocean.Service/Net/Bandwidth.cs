using System.Diagnostics;

namespace Ocean.Service.Net;

public enum UploadKind { Stream, Download }

/// <summary>
/// Decides how this PC spends its upload bandwidth when others watch and download its videos:
/// <list type="bullet">
/// <item>Streams (someone watching) go before downloads (someone saving a copy), which can wait.</item>
/// <item>While anyone is watching, downloads together get a modest share: a fifth of the speed limit, or without a
/// limit a quarter of the fastest upload speed seen recently (at least 1 MB/s). Without viewers they run free.</item>
/// <item>Within each kind, connections take turns in arrival order, so one fast client can't crowd out the others.</item>
/// <item>Only a few downloads are served at once (others are told to come back shortly), per node and in total.</item>
/// </list>
/// The optional speed limit from Settings caps everything together.
/// </summary>
public sealed class UploadScheduler
{
    public const int MaxDownloadsServed = 4;
    public const int MaxDownloadsPerNode = 2;
    private static readonly double ActiveWindow = 3.0; // seconds without stream bytes after which nobody counts as watching

    private readonly TokenBucket _all = new();
    private readonly TokenBucket _downloads = new();
    private readonly SemaphoreSlim _streamTurn = new(1, 1);   // FIFO: connections take turns
    private readonly SemaphoreSlim _downloadTurn = new(1, 1);
    private int _streamsWaiting;
    private long _bytesPerSecond;

    private readonly object _meterLock = new();
    private long _lastStreamTicks;
    private long _secStart = Stopwatch.GetTimestamp();
    private long _secBytes;
    private double _peakBps;

    private readonly object _slotLock = new();
    private readonly Dictionary<string, int> _servedPerNode = new();
    private int _servedTotal;

    /// <summary>Upload speed limit from Settings (0 = none).</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    public bool StreamsActive
    {
        get { lock (_meterLock) return _lastStreamTicks != 0 && Seconds(_lastStreamTicks) < ActiveWindow; }
    }

    public int DownloadsServed { get { lock (_slotLock) return _servedTotal; } }

    /// <summary>Total rate allowed for downloads right now (0 = no extra limit).</summary>
    public long DownloadRate()
    {
        if (!StreamsActive) return 0;
        var cap = BytesPerSecond;
        if (cap > 0) return Math.Max(cap / 5, 64 * 1024);
        double peak;
        lock (_meterLock) peak = _peakBps;
        return (long)Math.Max(1024 * 1024, peak / 4);
    }

    /// <summary>Reserves one of the download slots; null when this PC is already serving enough downloads.</summary>
    public IDisposable? TryStartDownload(string nodeId)
    {
        lock (_slotLock)
        {
            _servedPerNode.TryGetValue(nodeId, out var mine);
            if (_servedTotal >= MaxDownloadsServed || mine >= MaxDownloadsPerNode) return null;
            _servedPerNode[nodeId] = mine + 1;
            _servedTotal++;
        }
        return new Slot(() =>
        {
            lock (_slotLock)
            {
                _servedTotal--;
                if (_servedPerNode.TryGetValue(nodeId, out var n) && n > 1) _servedPerNode[nodeId] = n - 1;
                else _servedPerNode.Remove(nodeId);
            }
        });
    }

    /// <summary>Waits until <paramref name="bytes"/> may be sent.</summary>
    public async ValueTask WaitAsync(int bytes, UploadKind kind, CancellationToken ct)
    {
        var cap = BytesPerSecond;
        if (kind == UploadKind.Stream)
        {
            if (cap <= 0) return;
            await _streamTurn.WaitAsync(ct);
            Interlocked.Increment(ref _streamsWaiting);
            try { await _all.TakeAsync(bytes, cap, ct); }
            finally { Interlocked.Decrement(ref _streamsWaiting); _streamTurn.Release(); }
            return;
        }

        var rate = DownloadRate();
        if (rate <= 0 && cap <= 0) return;
        await _downloadTurn.WaitAsync(ct);
        try
        {
            if (rate > 0) await _downloads.TakeAsync(bytes, rate, ct);
            if (cap > 0)
            {
                // viewers first: a download only takes from the shared limit while no stream is waiting for it
                while (Volatile.Read(ref _streamsWaiting) > 0) await Task.Delay(3, ct);
                await _all.TakeAsync(bytes, cap, ct);
            }
        }
        finally { _downloadTurn.Release(); }
    }

    /// <summary>Counts bytes actually sent: tells whether anyone is watching and how fast this PC can upload.</summary>
    public void Sent(int bytes, UploadKind kind)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_meterLock)
        {
            if (kind == UploadKind.Stream) _lastStreamTicks = now;
            _secBytes += bytes;
            var elapsed = (now - _secStart) / (double)Stopwatch.Frequency;
            if (elapsed < 1) return;
            var rate = _secBytes / elapsed;
            // the peak fades slowly (about half after 10 minutes), so it follows a network that got slower
            _peakBps = Math.Max(rate, _peakBps * Math.Pow(0.999, elapsed));
            _secStart = now; _secBytes = 0;
        }
    }

    private static double Seconds(long since) => (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;

    private sealed class Slot : IDisposable
    {
        private Action? _release;
        public Slot(Action release) { _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>Classic token bucket; the rate is passed on each call so it can change on the fly.</summary>
    private sealed class TokenBucket
    {
        private readonly object _lock = new();
        private double _tokens;
        private long _last = Stopwatch.GetTimestamp();

        public async ValueTask TakeAsync(int bytes, long rate, CancellationToken ct)
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = (now - _last) / (double)Stopwatch.Frequency;
                    _last = now;
                    var burst = Math.Max(rate / 2.0, bytes); // at most half a second ahead
                    _tokens = Math.Min(burst, _tokens + elapsed * rate);
                    if (_tokens >= bytes) { _tokens -= bytes; return; }
                    wait = TimeSpan.FromSeconds((bytes - _tokens) / rate);
                }
                await Task.Delay(wait < TimeSpan.FromMilliseconds(2) ? TimeSpan.FromMilliseconds(2) : wait, ct);
            }
        }
    }
}

/// <summary>Read-only stream wrapper that paces reads through the scheduler and releases a download slot when done.</summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly UploadScheduler _scheduler;
    private readonly UploadKind _kind;
    private IDisposable? _slot;
    private const int Chunk = 64 * 1024;

    public ThrottledStream(Stream inner, UploadScheduler scheduler, UploadKind kind, IDisposable? slot = null)
    {
        _inner = inner; _scheduler = scheduler; _kind = kind; _slot = slot;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = Math.Min(count, Chunk);
        _scheduler.WaitAsync(count, _kind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var n = _inner.Read(buffer, offset, count);
        _scheduler.Sent(n, _kind);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length > Chunk) buffer = buffer[..Chunk];
        await _scheduler.WaitAsync(buffer.Length, _kind, ct);
        var n = await _inner.ReadAsync(buffer, ct);
        _scheduler.Sent(n, _kind);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _inner.Dispose(); Interlocked.Exchange(ref _slot, null)?.Dispose(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        Interlocked.Exchange(ref _slot, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
