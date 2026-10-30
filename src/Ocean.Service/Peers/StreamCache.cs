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
                var runEnd = Math.Min(to, (long)k * BlockSize - 1);
                // when storing, fetch whole blocks so they can be marked complete
                var keep = fill && entry is not null;
                var fetchStart = keep ? (long)b * BlockSize : pos;
                var fetchEnd = keep ? Math.Min(size - 1, (long)k * BlockSize - 1) : runEnd;

                using var resp = await _peers.OpenStreamAsync(peerId, item.Id, $"bytes={fetchStart}-{fetchEnd}", ct, download);
                if (resp.StatusCode != HttpStatusCode.PartialContent
                    || resp.Content.Headers.ContentRange is not { From: { } start } || start != fetchStart)
                {
                    if (resp.IsSuccessStatusCode) throw new IOException("The other PC ignored the requested range");
                    throw await PeerStatusException.FromAsync(resp);
                }
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var p = fetchStart;
                var nextMark = b;
                while (p <= fetchEnd)
                {
                    var want = (int)Math.Min(buf.Length, fetchEnd - p + 1);
                    var r = await src.ReadAsync(buf.AsMemory(0, want), ct);
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
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            file?.Dispose();
            if (entry is not null) Close(entry);
        }
    }

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

    private sealed class Entry
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
