using System.Buffers.Binary;

namespace Ocean.Service.Screen;

/// <summary>
/// The last few megabytes of a screen stream (H.264, Annex B) with the places a viewer can start at: every key
/// picture begins with its parameters (an SPS unit), found here while the bytes pass. Offsets count from the
/// session's first byte and never start again.
/// </summary>
public sealed class ScreenRing
{
    private readonly byte[] _buf;
    private readonly object _lock = new();
    private readonly List<long> _keys = new();
    private long _head;                 // offset after the last byte
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // start code scanner (carried across appends)
    private int _zeros;
    private bool _afterStart;
    private int _startZeros;
    private bool _closed;

    public ScreenRing(int capacity = 8 * 1024 * 1024) { _buf = new byte[capacity]; }

    public long Head { get { lock (_lock) return _head; } }
    public long Base { get { lock (_lock) return Math.Max(0, _head - _buf.Length); } }
    /// <summary>Where the latest key picture starts (-1: none yet).</summary>
    public long LastKey { get { lock (_lock) return _keys.Count > 0 ? _keys[^1] : -1; } }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                var at = _head + i;
                if (_afterStart)
                {
                    _afterStart = false;
                    if ((b & 0x1F) == 7)
                    {
                        // the unit starts at its start code (00 00 01, or 00 00 00 01)
                        var start = at - 3 - (_startZeros >= 3 ? 1 : 0);
                        if (_keys.Count == 0 || start - _keys[^1] > 512) _keys.Add(Math.Max(0, start)); // parameters repeated right away are the same key picture
                    }
                }
                if (b == 0) _zeros++;
                else
                {
                    if (b == 1 && _zeros >= 2) { _afterStart = true; _startZeros = _zeros; }
                    _zeros = 0;
                }
            }
            // into the ring
            var src = bytes;
            if (src.Length > _buf.Length) { _head += src.Length - _buf.Length; src = src[^_buf.Length..]; }
            var pos = (int)(_head % _buf.Length);
            var first = Math.Min(src.Length, _buf.Length - pos);
            src[..first].CopyTo(_buf.AsSpan(pos));
            src[first..].CopyTo(_buf);
            _head += src.Length;
            var floor = Math.Max(0, _head - _buf.Length);
            while (_keys.Count > 0 && _keys[0] < floor) _keys.RemoveAt(0);
            wake = _more;
            _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
    }

    /// <summary>Copies what there is from <paramref name="from"/>; 0: nothing new yet; -1: those bytes are gone (too far behind).</summary>
    public int Read(long from, Span<byte> into)
    {
        lock (_lock)
        {
            if (from < Math.Max(0, _head - _buf.Length)) return -1;
            var n = (int)Math.Min(into.Length, _head - from);
            if (n <= 0) return 0;
            var pos = (int)(from % _buf.Length);
            var first = Math.Min(n, _buf.Length - pos);
            _buf.AsSpan(pos, first).CopyTo(into);
            _buf.AsSpan(0, n - first).CopyTo(into[first..]);
            return n;
        }
    }

    /// <summary>Completes when bytes were appended after this call (or the ring closed).</summary>
    public Task More() { lock (_lock) return _closed ? Task.CompletedTask : _more.Task; }

    public bool Closed { get { lock (_lock) return _closed; } }

    public void Close()
    {
        TaskCompletionSource wake;
        lock (_lock) { _closed = true; wake = _more; }
        wake.TrySetResult();
    }
}

/// <summary>What screen packets add to the multicast's packet format (<see cref="Cast.CastWire"/>): their own port and control payload.</summary>
public static class ScreenWire
{
    public const int Port = 38474;

    /// <summary>Control payload (plain): head offset (8) + viewers (4) + ended (1).</summary>
    public static byte[] ControlPlain(long head, int viewers, bool ended)
    {
        var plain = new byte[13];
        BinaryPrimitives.WriteInt64LittleEndian(plain, head);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8), viewers);
        plain[12] = ended ? (byte)1 : (byte)0;
        return plain;
    }

    public static bool ReadControl(ReadOnlySpan<byte> plain, out long head, out int viewers, out bool ended)
    {
        head = 0; viewers = 0; ended = false;
        if (plain.Length < 13) return false;
        head = BinaryPrimitives.ReadInt64LittleEndian(plain);
        viewers = BinaryPrimitives.ReadInt32LittleEndian(plain[8..]);
        ended = plain[12] == 1;
        return true;
    }
}
