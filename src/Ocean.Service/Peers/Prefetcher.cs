using System.Buffers.Binary;
using Ocean.Core.Media;
using Ocean.Core.Protocol;

namespace Ocean.Service.Peers;

/// <summary>
/// Gets videos ready before they are clicked, as a video site does: while someone browses another PC's files, the
/// start of each one in view goes into the stream cache (and, for an MP4 that keeps its index at the end, that end
/// too: a player can't start without it), so it starts at once; a short clip comes whole (it also seeks at once).
/// While something plays, the start of the next one in the list is fetched. Quietly: two at a time, asked of the
/// owner as "prefetch" (after anyone watching, never a download slot, not counted as watching), only what the app
/// says is in view now, and nothing when the cache is off.
/// </summary>
public sealed class Prefetcher : IDisposable
{
    /// <summary>A file up to this size comes whole.</summary>
    public const long WholeUpTo = 25L * 1024 * 1024;
    /// <summary>At most this many files of a list are got ready, and at most this many whole.</summary>
    public const int MaxItems = 24, MaxWhole = 8;
    private const long HeadMin = 2L * 1024 * 1024, HeadMax = 8L * 1024 * 1024, TailMax = 16L * 1024 * 1024;
    private const int Workers = 2;

    private sealed record Want(string PeerId, ItemDto Item, bool Whole);
    public sealed record State(string PeerId, string ItemId, string Name, string What, long Bytes);

    private readonly PeerService _peers;
    private readonly StreamCache _cache;
    private readonly ILogger<Prefetcher> _log;
    private readonly object _lock = new();
    // what to get ready, in order: the next one to play first, then the list in view
    private List<Want> _next = new(), _browse = new();
    private readonly HashSet<string> _done = new(StringComparer.Ordinal), _busy = new(StringComparer.Ordinal);
    private readonly List<State> _log2 = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();

    public Prefetcher(PeerService peers, StreamCache cache, ILogger<Prefetcher> log)
    {
        _peers = peers; _cache = cache; _log = log;
        for (var i = 0; i < Workers; i++) _ = Task.Run(WorkAsync);
    }

    /// <summary>
    /// What is in view on another PC's page now (in the order shown; replaces what was asked before), or, with
    /// <paramref name="next"/>, the one to play after the current. An empty list stops it (the page was left).
    /// </summary>
    public void Set(string peerId, IReadOnlyList<string> itemIds, bool next)
    {
        var wants = new List<Want>();
        if (_cache.Enabled)
        {
            var whole = 0;
            foreach (var id in itemIds.Distinct().Take(MaxItems))
            {
                if (_peers.FindItem(peerId, id) is not { Size: > 0 } item) continue;
                if (item.Kind == MediaKinds.File || item.Lock is not null && !item.Unlocked) continue; // (only what Ocean opens; nothing locked)
                var all = item.Size <= WholeUpTo && whole < MaxWhole;
                if (all) whole++;
                wants.Add(new Want(peerId, item, all));
            }
        }
        lock (_lock)
        {
            if (next) _next = wants;
            else _browse = wants;
        }
        _wake.Release(Workers);
    }

    /// <summary>What was got ready lately (for checks).</summary>
    public List<State> Recent() { lock (_lock) return _log2.ToList(); }

    private static string Key(Want w) => $"{w.PeerId}|{w.Item.Id}|{w.Item.Size}|{w.Item.ModifiedAt.UtcTicks}";

    private Want? Take()
    {
        lock (_lock)
        {
            foreach (var w in _next.Concat(_browse))
            {
                var key = Key(w);
                if (_done.Contains(key) || _busy.Contains(key)) continue;
                _busy.Add(key);
                return w;
            }
            return null;
        }
    }

    private bool StillWanted(Want w) { lock (_lock) return _next.Concat(_browse).Any(x => Key(x) == Key(w)); }

    private async Task WorkAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Want? w;
            while ((w = Take()) is null)
            {
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
            }
            var key = Key(w);
            var ok = false;
            try { ok = await ReadyAsync(w, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogDebug("prefetch of {Name}: {Msg}", w.Item.Name, ex.Message); }
            finally
            {
                lock (_lock)
                {
                    _busy.Remove(key);
                    // (done, or refused: not asked again while the list stays; a new list may try again)
                    _done.Add(key);
                    if (_done.Count > 5000) _done.Clear();
                }
            }
            if (!ok) await Task.Delay(500, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>One file got ready: whole, or its start (and an MP4's index at the end).</summary>
    private async Task<bool> ReadyAsync(Want w, CancellationToken ct)
    {
        var item = w.Item;
        if (!_peers.IsOnline(w.PeerId) || !StillWanted(w)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // (left the page: stop this one too)
        using var watch = new Timer(_ => { if (!StillWanted(w)) try { linked.Cancel(); } catch { } }, null, 1000, 1000);
        long bytes;
        string what;
        try
        {
            if (w.Whole)
            {
                await _cache.CopyAsync(w.PeerId, item, 0, item.Size - 1, Stream.Null, fill: true, download: false, linked.Token, prefetch: true);
                bytes = item.Size; what = "whole";
            }
            else
            {
                // the first seconds: about 6 s at its bit rate, 2 to 8 MB
                var rate = item.DurationMs is > 0 ? item.Size * 1000.0 / item.DurationMs.Value : 0;
                var head = rate > 0 ? (long)Math.Clamp(rate * 6, HeadMin, HeadMax) : 4L * 1024 * 1024;
                head = Math.Min(head, item.Size);
                await _cache.CopyAsync(w.PeerId, item, 0, head - 1, Stream.Null, fill: true, download: false, linked.Token, prefetch: true);
                bytes = head; what = "start";
                if (IsMp4(item.Name) && await IndexAtAsync(w, head, linked.Token) is { } at && at < item.Size)
                {
                    var tail = item.Size - at;
                    if (tail <= TailMax)
                    {
                        await _cache.CopyAsync(w.PeerId, item, at, item.Size - 1, Stream.Null, fill: true, download: false, linked.Token, prefetch: true);
                        bytes += tail; what = "start + index at the end";
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        lock (_lock)
        {
            _log2.Add(new State(w.PeerId, item.Id, item.Name, what, bytes));
            if (_log2.Count > 200) _log2.RemoveRange(0, 100);
        }
        _log.LogDebug("prefetch: {Name} ready ({What}, {Bytes} bytes)", item.Name, what, bytes);
        return true;
    }

    private static bool IsMp4(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".3gp" or ".m4a";

    /// <summary>
    /// Where an MP4's index ("moov") begins when it comes after the pictures ("mdat"), read from the top-level boxes
    /// at the start (which are in the cache by now); null when the index is at the start (a "fast start" file).
    /// </summary>
    private async Task<long?> IndexAtAsync(Want w, long have, CancellationToken ct)
    {
        var len = (int)Math.Min(have, 64 * 1024);
        using var ms = new MemoryStream();
        await _cache.CopyAsync(w.PeerId, w.Item, 0, len - 1, ms, fill: true, download: false, ct, prefetch: true);
        return IndexAt(ms.ToArray(), w.Item.Size);
    }

    /// <summary>The top-level boxes of an MP4's start: where the index begins when the pictures come first.</summary>
    internal static long? IndexAt(byte[] start, long fileSize)
    {
        var b = start.AsSpan();
        long off = 0;
        for (var boxes = 0; boxes < 32 && off + 8 <= fileSize; boxes++)
        {
            if (off + 16 > b.Length) return null;             // (beyond what was read: give up, the player will fetch it)
            long size = BinaryPrimitives.ReadUInt32BigEndian(b[(int)off..]);
            var type = System.Text.Encoding.ASCII.GetString(b.Slice((int)off + 4, 4));
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(b[((int)off + 8)..]);
            else if (size == 0) size = fileSize - off;         // to the end of the file
            if (size < 8) return null;
            if (type == "moov") return null;                   // the index first: nothing more needed
            if (type == "mdat") return off + size;             // the pictures first: the index is after them
            off += size;
        }
        return null;
    }

    public void Dispose() => _cts.Cancel();
}
