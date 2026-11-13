using System.Buffers.Binary;
using Ocean.Core.Protocol;

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

/// <summary>
/// What screen packets add to the multicast's packet format (<see cref="Cast.CastWire"/>): their own port, their
/// control payload, and sound packets (a type older viewers don't know and pass over).
/// </summary>
public static class ScreenWire
{
    public const int Port = 38474;
    public const byte TypeSound = 4;
    /// <summary>A sound packet carries the frame before it too, as long as both fit in this (no packet over one Ethernet frame).</summary>
    private const int SoundRoom = 1300;

    /// <summary>
    /// Control payload (plain): head offset (8) + viewers (4) + ended (1), then (older hosts leave these out) sound on
    /// (1) + the newest sound frame's number (8, -1: none yet; a viewer far behind it gets the sound directly).
    /// </summary>
    public static byte[] ControlPlain(long head, int viewers, bool ended, bool sound, long soundNewest)
    {
        var plain = new byte[22];
        BinaryPrimitives.WriteInt64LittleEndian(plain, head);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8), viewers);
        plain[12] = ended ? (byte)1 : (byte)0;
        plain[13] = sound ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(14), soundNewest);
        return plain;
    }

    public static bool ReadControl(ReadOnlySpan<byte> plain, out long head, out int viewers, out bool ended, out bool sound, out long soundNewest)
    {
        head = 0; viewers = 0; ended = false; sound = false; soundNewest = -1;
        if (plain.Length < 13) return false;
        head = BinaryPrimitives.ReadInt64LittleEndian(plain);
        viewers = BinaryPrimitives.ReadInt32LittleEndian(plain[8..]);
        ended = plain[12] == 1;
        if (plain.Length >= 22) { sound = plain[13] == 1; soundNewest = BinaryPrimitives.ReadInt64LittleEndian(plain[14..]); }
        return true;
    }

    /// <summary>
    /// Sound payload (plain): count (1), then per frame its number (4), host time µs (8), age (2), length (2) and Opus packet.
    /// The newest frame first, then the one before it when it fits: a single lost packet costs nothing.
    /// </summary>
    public static byte[] SoundPlain(in ScreenSoundFrames.Frame now, ScreenSoundFrames.Frame? before)
    {
        var two = before is { } b && 1 + 2 * 16 + now.Data.Length + b.Data.Length <= SoundRoom;
        var plain = new byte[1 + 16 + now.Data.Length + (two ? 16 + before!.Value.Data.Length : 0)];
        plain[0] = (byte)(two ? 2 : 1);
        var at = Put(plain, 1, now);
        if (two) Put(plain, at, before!.Value);
        return plain;
    }

    private static int Put(byte[] into, int at, in ScreenSoundFrames.Frame f)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(into.AsSpan(at), f.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(into.AsSpan(at + 4), f.HostUs);
        BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(at + 12), f.Age);
        BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(at + 14), (ushort)f.Data.Length);
        f.Data.CopyTo(into.AsSpan(at + 16));
        return at + 16 + f.Data.Length;
    }

    public static List<ScreenSoundFrames.Frame> ReadSound(ReadOnlySpan<byte> plain)
    {
        var frames = new List<ScreenSoundFrames.Frame>(2);
        if (plain.Length < 1) return frames;
        var at = 1;
        for (var i = 0; i < plain[0] && at + 16 <= plain.Length; i++)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(plain[(at + 14)..]);
            if (at + 16 + len > plain.Length) break;
            frames.Add(new ScreenSoundFrames.Frame(BinaryPrimitives.ReadUInt32LittleEndian(plain[at..]), BinaryPrimitives.ReadInt64LittleEndian(plain[(at + 4)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(plain[(at + 12)..]), plain.Slice(at + 16, len).ToArray()));
            at += 16 + len;
        }
        return frames;
    }
}

/// <summary>The last few seconds of a shared screen's sound, for viewers that get it over the direct connection.</summary>
public sealed class ScreenSoundRing
{
    private const int Keep = 150; // 3 s
    private readonly Queue<ScreenSoundFrames.Frame> _frames = new();
    private readonly object _lock = new();
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    public bool Closed { get { lock (_lock) return _closed; } }
    /// <summary>The newest frame's number (-1: none yet).</summary>
    public long Newest { get { lock (_lock) return _frames.Count > 0 ? _frames.Last().Seq : -1; } }

    public void Add(in ScreenSoundFrames.Frame f)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            _frames.Enqueue(f);
            while (_frames.Count > Keep) _frames.Dequeue();
            wake = _more; _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
    }

    public List<ScreenSoundFrames.Frame> After(long seq) { lock (_lock) return _frames.Where(f => f.Seq > seq).ToList(); }
    public Task More() { lock (_lock) return _more.Task; }
    public void Close() { TaskCompletionSource wake; lock (_lock) { _closed = true; wake = _more; } wake.TrySetResult(); }
}
