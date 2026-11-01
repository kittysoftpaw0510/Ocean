using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Social;

/// <summary>
/// Votes on my videos (owner side), in <c>votes.json</c>: per video, each voting PC's +1 or −1 and the PCs that
/// watched it to the end, with when. One vote per PC (by its node id), changeable and withdrawable; the totals and a
/// score travel in my catalog, who voted never leaves this PC. The score fades older votes (half weight after 30
/// days) so a good new video can climb past old favourites.
/// </summary>
public sealed class Ratings
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(30);
    private const double FinishedWeight = 0.25;

    private sealed class Record
    {
        public Dictionary<string, Vote> Votes { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Finished { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Vote
    {
        public int Value { get; set; }
        public DateTimeOffset At { get; set; }
    }

    private readonly CatalogService _catalog;
    private readonly ILogger<Ratings> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Record> _items = new(StringComparer.Ordinal);
    private readonly Timer _save;
    private readonly Timer _prune;

    public Ratings(CatalogService catalog, ILogger<Ratings> log)
    {
        _catalog = catalog; _log = log;
        _path = Path.Combine(OceanDefaults.DataDir, "votes.json");
        Load();
        _save = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        // votes of videos no longer shared go (after the first scans had time to finish)
        _prune = new Timer(_ => Prune(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        _catalog.RatingLookup = Lookup;
    }

    /// <summary>A PC votes (+1/−1/0 = take it back) and/or reports it watched the video to the end.</summary>
    public RatingDto Rate(string itemId, string voterId, RateDto rate)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(itemId, out var r)) _items[itemId] = r = new Record();
            if (rate.Vote is { } vote)
            {
                if (vote == 0) r.Votes.Remove(voterId);
                else r.Votes[voterId] = new Vote { Value = Math.Sign(vote), At = DateTimeOffset.UtcNow };
            }
            if (rate.Finished) r.Finished.TryAdd(voterId, DateTimeOffset.UtcNow);
            if (r.Votes.Count == 0 && r.Finished.Count == 0) _items.Remove(itemId);
        }
        _save.Change(2000, Timeout.Infinite);
        _catalog.NotifyRatingsChanged();
        return Totals(itemId);
    }

    public RatingDto Totals(string itemId)
    {
        var (up, down, finished, score) = Lookup(itemId);
        return new RatingDto { Up = up, Down = down, Finished = finished, Score = score };
    }

    private (int Up, int Down, int Finished, double Score) Lookup(string itemId)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(itemId, out var r)) return (0, 0, 0, 0);
            var now = DateTimeOffset.UtcNow;
            double Fade(DateTimeOffset at) => Math.Pow(0.5, Math.Max(0, (now - at).TotalDays) / HalfLife.TotalDays);
            var score = r.Votes.Values.Sum(v => v.Value * Fade(v.At)) + FinishedWeight * r.Finished.Values.Sum(Fade);
            return (r.Votes.Values.Count(v => v.Value > 0), r.Votes.Values.Count(v => v.Value < 0), r.Finished.Count, Math.Round(score, 3));
        }
    }

    private void Prune()
    {
        var removed = 0;
        lock (_lock)
            foreach (var id in _items.Keys.ToList())
                if (!_catalog.TryGetItem(id, out _, out _)) { _items.Remove(id); removed++; }
        if (removed == 0) return;
        _log.LogInformation("votes: dropped those of {Count} video(s) no longer shared", removed);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var (id, r) in Json.Deserialize<Dictionary<string, Record>>(File.ReadAllText(_path)) ?? new())
                _items[id] = r;
        }
        catch (Exception ex) { _log.LogWarning(ex, "votes: load failed"); }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = Json.Serialize(_items);
            AtomicFile.Write(_path, json);
        }
        catch (Exception ex) { _log.LogWarning(ex, "votes: save failed"); }
    }
}

/// <summary>
/// My votes on other PCs' videos (voter side), in <c>my-votes.json</c>: so my 👍/👎 shows as pressed, and a vote
/// cast while the owner is offline is delivered when it is back.
/// </summary>
public sealed class MyVotes
{
    public sealed class Entry
    {
        public int Vote { get; set; }
        public bool Finished { get; set; }
        /// <summary>Not delivered to the owner yet.</summary>
        public bool Pending { get; set; }
        public bool FinishedPending { get; set; }
    }

    private readonly ILogger<MyVotes> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public MyVotes(ILogger<MyVotes> log)
    {
        _log = log;
        _path = Path.Combine(OceanDefaults.DataDir, "my-votes.json");
        try
        {
            if (File.Exists(_path))
                foreach (var (k, e) in Json.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new())
                    _entries[k] = e;
        }
        catch (Exception ex) { _log.LogWarning(ex, "my votes: load failed"); }
    }

    private static string Key(string peerId, string itemId) => peerId + "|" + itemId;

    public int VoteOn(string peerId, string itemId)
    {
        lock (_lock) return _entries.TryGetValue(Key(peerId, itemId), out var e) ? e.Vote : 0;
    }

    public bool HasFinished(string peerId, string itemId)
    {
        lock (_lock) return _entries.TryGetValue(Key(peerId, itemId), out var e) && e.Finished;
    }

    /// <summary>Records a vote and/or "finished" as not yet delivered.</summary>
    public void Record(string peerId, string itemId, RateDto rate)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(Key(peerId, itemId), out var e)) _entries[Key(peerId, itemId)] = e = new Entry();
            if (rate.Vote is { } v) { e.Vote = Math.Sign(v); e.Pending = true; }
            if (rate.Finished && !e.Finished) { e.Finished = true; e.FinishedPending = true; }
        }
        Save();
    }

    public void Delivered(string peerId, string itemId, RateDto rate)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(Key(peerId, itemId), out var e)) return;
            if (rate.Vote is not null && e.Vote == Math.Sign(rate.Vote.Value)) e.Pending = false;
            if (rate.Finished) e.FinishedPending = false;
            if (e.Vote == 0 && !e.Pending && !e.FinishedPending && !e.Finished) _entries.Remove(Key(peerId, itemId));
        }
        Save();
    }

    /// <summary>What still has to reach <paramref name="peerId"/>.</summary>
    public List<(string ItemId, RateDto Rate)> PendingFor(string peerId)
    {
        var prefix = peerId + "|";
        lock (_lock)
            return _entries.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && (kv.Value.Pending || kv.Value.FinishedPending))
                .Select(kv => (kv.Key[prefix.Length..], new RateDto { Vote = kv.Value.Pending ? kv.Value.Vote : null, Finished = kv.Value.FinishedPending }))
                .ToList();
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = Json.Serialize(_entries);
            AtomicFile.Write(_path, json);
        }
        catch (Exception ex) { _log.LogWarning(ex, "my votes: save failed"); }
    }
}
