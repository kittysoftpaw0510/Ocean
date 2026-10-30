using System.Diagnostics;

namespace Ocean.Service.Net;

/// <summary>Global token bucket for outgoing video bytes (0 = unlimited).</summary>
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set { lock (_lock) { _bytesPerSecond = Math.Max(0, value); _tokens = Math.Min(_tokens, _bytesPerSecond); } }
    }

    public bool Enabled => BytesPerSecond > 0;

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var rate = _bytesPerSecond;
                if (rate <= 0) return;
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _lastTicks = now;
                _tokens = Math.Min(rate, _tokens + elapsed * rate); // burst at most 1 second
                if (_tokens >= bytes) { _tokens -= bytes; return; }
                wait = TimeSpan.FromSeconds((bytes - _tokens) / rate);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait, ct);
        }
    }
}

/// <summary>Seekable read-only stream wrapper that paces reads through the limiter.</summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly BandwidthLimiter _limiter;
    private const int Chunk = 64 * 1024;

    public ThrottledStream(Stream inner, BandwidthLimiter limiter) { _inner = inner; _limiter = limiter; }

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
        _limiter.WaitAsync(count, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length > Chunk) buffer = buffer[..Chunk];
        await _limiter.WaitAsync(buffer.Length, ct);
        return await _inner.ReadAsync(buffer, ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
