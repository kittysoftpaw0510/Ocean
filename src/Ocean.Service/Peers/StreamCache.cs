using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.State;

namespace Ocean.Service.Peers;

/// <summary>The other PC answered a stream request with an error (locked, gone, busy...).</summary>
public sealed class PeerStatusException : Exception
{
    public PeerStatusException(HttpStatusCode status, string? error, string? message, TimeSpan? retryAfter)
        : base(message ?? $"The other PC answered {(int)status}")
    {
        Status = status; Error = error; RetryAfter = retryAfter;
    }

    public HttpStatusCode Status { get; }
    /// <summary>ErrorDto.Error, e.g. "busy".</summary>
    public string? Error { get; }
    public TimeSpan? RetryAfter { get; }

    public static async Task<PeerStatusException> FromAsync(HttpResponseMessage resp)
    {
        ErrorDto? err = null;
        try { err = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options); } catch { }
        var retry = resp.Headers.RetryAfter?.Delta;
        return new PeerStatusException(resp.StatusCode, err?.Error, err?.Message, retry);
    }
}

/// <summary>
/// Disk cache of video bytes watched from other PCs, so watching again, seeking back, resuming, or downloading a
/// video you just watched doesn't fetch the same bytes over the network twice.
/// <para>
/// Each video is a sparse file filled in 1 MB blocks as they arrive, plus a small map of which blocks are present.
/// A request is answered from cached blocks where possible; missing runs are fetched from the owner (and stored
/// when <c>fill</c> is set). Entries are keyed by node, item, size and modification time, so a changed file never
/// serves stale bytes. The least recently used videos are evicted beyond the size set in Settings.
/// </para>
/// </summary>
public sealed class StreamCache
{
    public const int BlockSize = 1 << 20;
    private const int PersistEvery = 32; // blocks

    // A transfer from the owner that breaks (a network hiccup, a reverse link reconnecting, another bridge, the
    // owner's Ocean restarting) is picked up again from the byte where it stopped, so the player only sees a pause.
    // Before, the player's connection was cut and it showed "this video can't be played".
    private static readonly int[] ResumeWaitsMs = { 500, 1500, 3000, 6000 };
    /// <summary>No byte from the owner for this long: the transfer is taken as broken (a PC that went to sleep never says so).</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Going on is given up this long after the first interruption (the owner really left).</summary>
    private static readonly TimeSpan ResumeBudget = TimeSpan.FromSeconds(25);

    private readonly PeerService _peers;
    private readonly ConfigStore _config;
    private readonly ILogger<StreamCache> _log;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(); // everything on disk (open or not)

    public StreamCache(PeerService peers, ConfigStore config, ILogger<StreamCache> log)
    {
        _peers = peers; _config = config; _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "cache", "streams");
        Directory.CreateDirectory(_dir);
        LoadIndex();
        _config.Changed += _ => { if (UsedBytes > Capacity) Evict(); };
    }

    public long Capacity => Math.Max(0, _config.Current.StreamCacheGB) * 1024L * 1024 * 1024;
    public bool Enabled => Capacity > 0;

    public long UsedBytes { get { lock (_lock) return _entries.Values.Sum(e => e.Bytes); } }

    /// <summary>A video is being streamed through the cache right now (watched here, or passed on as a bridge).</summary>
    public bool InUse { get { lock (_lock) return _entries.Values.Any(e => e.Refs > 0); } }

    // ------------------------------------------------------------------ external feeds (multicast)

    private readonly Dictionary<string, ExternalFeed> _feeds = new();

    /// <summary>Lets a multicast receiver write a video's bytes straight into its cache entry (see <see cref="ExternalFeed"/>).</summary>
    public ExternalFeed? OpenFeed(string peerId, ItemDto item)
    {
        if (!Enabled) return null;
        var entry = Open(Key(peerId, item), item.Size, create: true);
        if (entry is null) return null;
        try
        {
            var feed = new ExternalFeed(this, entry);
            lock (_lock) _feeds[entry.Key] = feed;
            return feed;
        }
        catch (Exception ex) { Close(entry); _log.LogDebug(ex, "stream cache: feed"); return null; }
    }

    /// <summary>
    /// Bytes arriving out of band (a multicast) for one video. Writes land in the sparse file; a block counts as cached
    /// once every byte of it arrived. <see cref="HighWater"/> is the furthest byte written, which tells a reader
    /// whether a missing block is still on its way or was lost.
    /// </summary>
    public sealed class ExternalFeed : IDisposable
    {
        private readonly StreamCache _owner;
        private readonly Entry _entry;
        private readonly SafeFileHandle _file;
        private readonly Dictionary<int, List<(long Start, long End)>> _partial = new();
        private readonly object _lock = new();
        private int _closed;

        internal ExternalFeed(StreamCache owner, Entry entry)
        {
            _owner = owner; _entry = entry;
            _file = File.OpenHandle(entry.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        }

        public long HighWater { get; private set; }
        public bool Closed => Volatile.Read(ref _closed) == 1;

        /// <summary>
        /// Due from the feed within seconds (just ahead of what has arrived, and not passed by more than a block), so a
        /// reader should wait for it rather than fetch it; blocks far ahead or already passed are fetched directly.
        /// </summary>
        public bool ExpectedSoon(int block) => !Closed && _entry.BlockEnd(block) >= HighWater - BlockSize && (long)block * BlockSize <= HighWater + 3 * BlockSize;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (Closed || data.Length == 0 || offset < 0 || offset + data.Length > _entry.Size) return;
            lock (_lock)
            {
                if (Closed) return;
                RandomAccess.Write(_file, data, offset);
                if (offset + data.Length > HighWater) HighWater = offset + data.Length;
                var pos = offset; var end = offset + data.Length - 1;
                while (pos <= end) // a packet may straddle two blocks
                {
                    var b = (int)(pos / BlockSize);
                    var blockEnd = _entry.BlockEnd(b);
                    var pieceEnd = Math.Min(end, blockEnd);
                    if (!_entry.Has(b)) Cover(b, pos, pieceEnd, blockEnd);
                    pos = pieceEnd + 1;
                }
            }
        }

        private void Cover(int b, long start, long end, long blockEnd)
        {
            if (!_partial.TryGetValue(b, out var ranges)) _partial[b] = ranges = new List<(long, long)>();
            ranges.Add((start, end));
            ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
            var merged = new List<(long Start, long End)>();
            foreach (var r in ranges)
            {
                if (merged.Count > 0 && r.Start <= merged[^1].End + 1) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
                else merged.Add(r);
            }
            _partial[b] = merged;
            if (merged.Count == 1 && merged[0].Start <= (long)b * BlockSize && merged[0].End >= blockEnd)
            {
                _partial.Remove(b);
                if (_entry.Mark(b) % PersistEvery == 0) _owner.Save(_entry);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            lock (_lock) _file.Dispose();
            lock (_owner._lock) _owner._feeds.Remove(_entry.Key);
            _owner.Close(_entry); // saves the block map
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Writes bytes <paramref name="from"/>..<paramref name="to"/> (inclusive) of a video to <paramref name="dest"/>:
    /// cached blocks from disk, the rest from the owner. With <paramref name="fill"/> the fetched blocks are kept.
    /// Throws <see cref="PeerStatusException"/> when the owner refuses (nothing written to dest in that case if the
    /// very first bytes had to come from it).
    /// </summary>
    public async Task CopyAsync(string peerId, ItemDto item, long from, long to, Stream dest, bool fill, bool download, CancellationToken ct)
    {
        var size = item.Size;
        if (size <= 0 || from > to || to >= size) throw new ArgumentOutOfRangeException(nameof(from));
        fill &= Enabled;
        var entry = Open(Key(peerId, item), size, create: fill);
        var buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
        SafeFileHandle? file = null;
        try
        {
            if (entry is not null)
                file = File.OpenHandle(entry.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            var pos = from;
            while (pos <= to)
            {
                var b = (int)(pos / BlockSize);
                if (entry is not null && entry.Has(b))
                {
                    // cached: straight from disk
                    var blockEnd = Math.Min(entry.BlockEnd(b), to);
                    while (pos <= blockEnd)
                    {
                        var n = (int)Math.Min(buf.Length, blockEnd - pos + 1);
                        var r = await RandomAccess.ReadAsync(file!, buf.AsMemory(0, n), pos, ct);
                        if (r <= 0) throw new IOException("Cache file is shorter than expected");
                        await dest.WriteAsync(buf.AsMemory(0, r), ct);
                        pos += r;
                    }
                    continue;
                }

                // missing run: up to the next cached block (or the end of the request)
                var last = (int)(to / BlockSize);
                var k = b;
                while (k <= last && !(entry?.Has(k) ?? false)) k++;
                // a multicast feed is filling this video: wait a moment for a block the sender is about to send, and
                // otherwise fetch just one block (the rest keeps arriving on its own)
                ExternalFeed? feed = null;
                if (entry is not null) lock (_lock) _feeds.TryGetValue(entry.Key, out feed);
                if (feed is not null && entry is not null)
                {
                    if (feed.ExpectedSoon(b))
                    {
                        for (var waited = 0; waited < 15 && !entry.Has(b) && !feed.Closed; waited++) await Task.Delay(100, ct); // 1.5 s at most
                        if (entry.Has(b)) continue;
                    }
                    k = Math.Min(k, b + 1);
                }
                var runEnd = Math.Min(to, (long)k * BlockSize - 1);
                // when storing, fetch whole blocks so they can be marked complete
                var keep = fill && entry is not null;
                var fetchStart = keep ? (long)b * BlockSize : pos;
                var fetchEnd = keep ? Math.Min(size - 1, (long)k * BlockSize - 1) : runEnd;

                var p = fetchStart;
                var nextMark = b;
                DateTime? firstFailure = null;
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var resp = await _peers.OpenStreamAsync(peerId, item.Id, $"bytes={p}-{fetchEnd}", ct, download);
                        if (resp.StatusCode != HttpStatusCode.PartialContent
                            || resp.Content.Headers.ContentRange is not { From: { } start } || start != p)
                        {
                            if (resp.IsSuccessStatusCode) throw new IOException("The other PC ignored the requested range");
                            throw await PeerStatusException.FromAsync(resp);
                        }
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        if (attempt > 0) _log.LogInformation("stream of {Item}: going on from byte {Pos} (try {Try})", item.Name, p, attempt + 1);
                        while (p <= fetchEnd)
                        {
                            var want = (int)Math.Min(buf.Length, fetchEnd - p + 1);
                            int r;
                            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                            {
                                stall.CancelAfter(StallTimeout);
                                try { r = await src.ReadAsync(buf.AsMemory(0, want), stall.Token); }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    throw new IOException($"Nothing from the other PC for {StallTimeout.TotalSeconds:0} s");
                                }
                            }
                            if (r <= 0) throw new IOException("The other PC stopped sending");
                            if (keep)
                            {
                                await RandomAccess.WriteAsync(file!, buf.AsMemory(0, r), p, ct);
                                while (nextMark < k && entry!.BlockEnd(nextMark) <= p + r - 1)
                                {
                                    if (entry.Mark(nextMark) % PersistEvery == 0) Save(entry);
                                    nextMark++;
                                }
                            }
                            // the part of this chunk the caller asked for
                            var s = Math.Max(p, pos);
                            var e = Math.Min(p + r - 1, runEnd);
                            if (s <= e)
                            {
                                await dest.WriteAsync(buf.AsMemory((int)(s - p), (int)(e - s + 1)), ct);
                                pos = e + 1;
                            }
                            p += r;
                        }
                        break; // this run is complete
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && attempt < ResumeWaitsMs.Length && Resumable(ex))
                    {
                        firstFailure ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - firstFailure > ResumeBudget) throw;
                        _log.LogInformation("stream of {Item} from {Peer} interrupted at byte {Pos} ({Why}): going on in {Wait} s",
                            item.Name, _peers.NameOf(peerId), p, ex.Message, ResumeWaitsMs[attempt] / 1000.0);
                        await Task.Delay(ResumeWaitsMs[attempt], ct);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            file?.Dispose();
            if (entry is not null) Close(entry);
        }
    }

    /// <summary>Worth trying again: the connection broke or the owner was briefly unreachable or busy (not "locked", "gone").</summary>
    private static bool Resumable(Exception ex) => ex switch
    {
        PeerStatusException ps => ps.Status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        PeerUnavailableException or HttpRequestException or IOException => true,
        _ => false,
    };

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Drops a video's cached bytes (e.g. it was downloaded: the copy on disk replaces them).</summary>
    public void Forget(string peerId, string itemId)
    {
        var prefix = KeyPrefix(peerId, itemId);
        List<Entry> victims;
        lock (_lock) victims = _entries.Values.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var e in victims) Delete(e);
    }

    /// <summary>Empties the cache (videos being played right now are removed when they are closed).</summary>
    public void Clear()
    {
        List<Entry> all;
        lock (_lock) all = _entries.Values.ToList();
        foreach (var e in all) Delete(e);
    }

    private void Evict()
    {
        var cap = Capacity;
        while (true)
        {
            Entry? victim;
            lock (_lock)
            {
                if (_entries.Values.Sum(e => e.Bytes) <= cap) return;
                victim = _entries.Values.Where(e => e.Refs == 0).OrderBy(e => e.LastAccess).FirstOrDefault();
            }
            if (victim is null) return; // everything left is in use
            Delete(victim);
        }
    }

    private void Delete(Entry e)
    {
        lock (_lock)
        {
            if (e.Refs > 0) { e.DeleteWhenClosed = true; return; }
            _entries.Remove(e.Key);
        }
        try { File.Delete(e.DataPath); } catch { }
        try { File.Delete(e.MapPath); } catch { }
    }

    private Entry? Open(string key, long size, bool create)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (e.DeleteWhenClosed) return null; // being removed while still playing: don't reuse or recreate it now
                e.Refs++; e.LastAccess = DateTime.UtcNow;
                return e;
            }
            if (!create) return null;
            e = new Entry(key, Path.Combine(_dir, key + ".bin"), Path.Combine(_dir, key + ".map"), size) { Refs = 1, LastAccess = DateTime.UtcNow };
            try { CreateSparse(e.DataPath, size); }
            catch (Exception ex) { _log.LogDebug(ex, "stream cache: could not create {Key}", key); return null; }
            _entries[key] = e;
            return e;
        }
    }

    private void Close(Entry e)
    {
        bool delete;
        lock (_lock)
        {
            e.Refs--;
            e.LastAccess = DateTime.UtcNow;
            delete = e.Refs == 0 && e.DeleteWhenClosed;
        }
        if (delete) { Delete(e); return; }
        Save(e);
        Evict();
    }

    private void Save(Entry e)
    {
        try
        {
            var map = new MapFile { Size = e.Size, Have = e.Bits(), LastAccess = e.LastAccess };
            AtomicFile.Write(e.MapPath, Json.Serialize(map));
        }
        catch (Exception ex) { _log.LogDebug(ex, "stream cache: map save failed"); }
    }

    private void LoadIndex()
    {
        try
        {
            foreach (var map in Directory.EnumerateFiles(_dir, "*.map"))
            {
                var key = Path.GetFileNameWithoutExtension(map);
                var data = Path.Combine(_dir, key + ".bin");
                try
                {
                    var m = Json.Deserialize<MapFile>(File.ReadAllText(map));
                    if (m is null || !File.Exists(data) || new FileInfo(data).Length != m.Size) throw new InvalidDataException();
                    var e = new Entry(key, data, map, m.Size) { LastAccess = m.LastAccess };
                    e.Load(m.Have);
                    _entries[key] = e;
                }
                catch
                {
                    try { File.Delete(map); } catch { }
                    try { File.Delete(data); } catch { }
                }
            }
            // data without a map can't be trusted
            foreach (var data in Directory.EnumerateFiles(_dir, "*.bin"))
                if (!_entries.ContainsKey(Path.GetFileNameWithoutExtension(data))) try { File.Delete(data); } catch { }
            foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp")) try { File.Delete(tmp); } catch { }
        }
        catch (Exception ex) { _log.LogWarning(ex, "stream cache: index load failed"); }
    }

    private static string KeyPrefix(string peerId, string itemId) => Safe(peerId[..Math.Min(16, peerId.Length)]) + "_" + Safe(itemId) + "_";
    private static string Key(string peerId, ItemDto item) => KeyPrefix(peerId, item.Id) + item.Size + "_" + item.ModifiedAt.UtcTicks;
    private static string Safe(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    // ---- sparse files: only the blocks actually written take disk space (NTFS)

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    private const uint FsctlSetSparse = 0x000900C4;

    private static void CreateSparse(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        fs.SetLength(size);
    }

    private sealed class MapFile
    {
        public long Size { get; set; }
        public string Have { get; set; } = "";
        public DateTime LastAccess { get; set; }
    }

    internal sealed class Entry
    {
        private readonly bool[] _have;
        private int _count;

        public Entry(string key, string dataPath, string mapPath, long size)
        {
            Key = key; DataPath = dataPath; MapPath = mapPath; Size = size;
            _have = new bool[(int)((size + BlockSize - 1) / BlockSize)];
        }

        public string Key { get; }
        public string DataPath { get; }
        public string MapPath { get; }
        public long Size { get; }
        public int Refs { get; set; }
        public bool DeleteWhenClosed { get; set; }
        public DateTime LastAccess { get; set; }

        public long Bytes { get { lock (_have) return Math.Min(Size, (long)_count * BlockSize); } }
        public long BlockEnd(int b) => Math.Min(Size, (long)(b + 1) * BlockSize) - 1;
        public bool Has(int b) { lock (_have) return b >= 0 && b < _have.Length && _have[b]; }

        /// <summary>Marks a block present; returns how many are present now.</summary>
        public int Mark(int b)
        {
            lock (_have)
            {
                if (b >= 0 && b < _have.Length && !_have[b]) { _have[b] = true; _count++; }
                return _count;
            }
        }

        public string Bits()
        {
            lock (_have)
            {
                var bytes = new byte[(_have.Length + 7) / 8];
                for (var i = 0; i < _have.Length; i++) if (_have[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
                return Convert.ToBase64String(bytes);
            }
        }

        public void Load(string bits)
        {
            var bytes = Convert.FromBase64String(bits);
            lock (_have)
                for (var i = 0; i < _have.Length && (i >> 3) < bytes.Length; i++)
                    if ((bytes[i >> 3] & (1 << (i & 7))) != 0) { _have[i] = true; _count++; }
        }
    }
}
