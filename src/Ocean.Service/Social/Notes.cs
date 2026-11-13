using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Social;

/// <summary>
/// Notes on my videos (owner side), in <c>notes.json</c>: per video, the notes others (and I) left, each with its
/// writer, an optional moment of the video, and one 👍/👎 per PC. Others read and write them through the peer API
/// under the video's own access rule; only the number of notes travels in my catalog. A writer edits and deletes
/// their own notes; I may delete any note on my videos. Limits: <see cref="NoteLimits"/>.
/// </summary>
public sealed class Notes : IDisposable
{
    private sealed class Note
    {
        public string Id { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AuthorHost { get; set; } = "";
        public string Text { get; set; } = "";
        public double? AtSeconds { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? EditedAt { get; set; }
        /// <summary>Voter node id → +1/−1.</summary>
        public Dictionary<string, int> Votes { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly CatalogService _catalog;
    private readonly EventHub _events;
    private readonly ILogger<Notes> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Note>> _items = new(StringComparer.Ordinal);
    /// <summary>(video, writer) → when they last added a note: one every few seconds at most.</summary>
    private readonly Dictionary<(string, string), DateTimeOffset> _lastAdd = new();
    private readonly Timer _save;
    private readonly Timer _prune;
    private volatile bool _dirty;

    public Notes(CatalogService catalog, EventHub events, ILogger<Notes> log)
    {
        _catalog = catalog; _events = events; _log = log;
        _path = Path.Combine(OceanDefaults.DataDir, "notes.json");
        Load();
        _save = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _prune = new Timer(_ => Prune(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        _catalog.NoteCountLookup = Count;
    }

    public int Count(string itemId)
    {
        lock (_lock) return _items.TryGetValue(itemId, out var l) ? l.Count : 0;
    }

    /// <summary>All notes of a video, as <paramref name="callerId"/> sees them (its votes, which are its own).</summary>
    public NotesDto List(string itemId, string callerId, bool callerIsOwner)
    {
        lock (_lock)
        {
            var notes = _items.TryGetValue(itemId, out var l) ? l : new List<Note>();
            return new NotesDto
            {
                IsOwner = callerIsOwner, FetchedAt = DateTimeOffset.UtcNow,
                Notes = notes.Select(n => new NoteDto
                {
                    Id = n.Id, AuthorId = n.AuthorId, AuthorName = n.AuthorName, AuthorHost = n.AuthorHost, Text = n.Text,
                    AtSeconds = n.AtSeconds, CreatedAt = n.CreatedAt, EditedAt = n.EditedAt,
                    Up = n.Votes.Values.Count(v => v > 0), Down = n.Votes.Values.Count(v => v < 0),
                    MyVote = n.Votes.TryGetValue(callerId, out var mine) ? mine : 0,
                    Mine = n.AuthorId == callerId,
                }).ToList(),
            };
        }
    }

    /// <summary>Cleans the text: trimmed, at most <see cref="NoteLimits.MaxLength"/> characters, no control characters but line breaks.</summary>
    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var chars = text.Replace("\r\n", "\n").Where(c => c == '\n' || !char.IsControl(c)).ToArray();
        var t = new string(chars).Trim();
        while (t.Contains("\n\n\n")) t = t.Replace("\n\n\n", "\n\n");
        return t.Length == 0 ? null : t.Length > NoteLimits.MaxLength ? null : t;
    }

    /// <summary>Adds a note; null with <paramref name="error"/> when refused.</summary>
    public NotesDto Add(string itemId, string authorId, string authorName, string authorHost, NoteWriteDto write, bool callerIsOwner, bool notifyOwner)
    {
        var text = Clean(write.Text);
        if (text is null) return Refused(itemId, authorId, callerIsOwner, write.Text?.Trim().Length > NoteLimits.MaxLength
            ? $"A note can be at most {NoteLimits.MaxLength} characters." : "The note is empty.");
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_lastAdd.TryGetValue((itemId, authorId), out var last) && now - last < TimeSpan.FromSeconds(NoteLimits.MinGapSeconds))
                return RefusedLocked(itemId, authorId, callerIsOwner, "Wait a moment before adding another note.");
            _lastAdd[(itemId, authorId)] = now;
            if (!_items.TryGetValue(itemId, out var list)) _items[itemId] = list = new List<Note>();
            list.Add(new Note
            {
                Id = Guid.NewGuid().ToString("N")[..12], AuthorId = authorId, AuthorName = authorName, AuthorHost = authorHost,
                Text = text, AtSeconds = write.AtSeconds is { } at && at >= 0 ? Math.Round(at, 1) : null, CreatedAt = now,
            });
            // too many: the oldest of the least useful go
            if (list.Count > NoteLimits.MaxPerVideo)
            {
                var drop = list.OrderBy(n => n.Votes.Values.Sum()).ThenBy(n => n.CreatedAt).Take(list.Count - NoteLimits.MaxPerVideo).ToHashSet();
                list.RemoveAll(drop.Contains);
            }
        }
        Changed();
        if (notifyOwner)
        {
            var name = _catalog.TryGetItem(itemId, out _, out var item) ? item.Name : "";
            _events.Publish("noteAdded", new NoteAddedDto { ItemId = itemId, ItemName = name, AuthorName = authorName, Text = text, AtSeconds = write.AtSeconds });
            _log.LogInformation("note from {Author} on {Item}", authorName, name);
        }
        return List(itemId, authorId, callerIsOwner);
    }

    /// <summary>The writer changes their note (text and moment).</summary>
    public NotesDto? Edit(string itemId, string noteId, string callerId, NoteWriteDto write, bool callerIsOwner)
    {
        var text = Clean(write.Text);
        if (text is null) return Refused(itemId, callerId, callerIsOwner, write.Text?.Trim().Length > NoteLimits.MaxLength
            ? $"A note can be at most {NoteLimits.MaxLength} characters." : "The note is empty.");
        lock (_lock)
        {
            var n = Find(itemId, noteId);
            if (n is null) return null;
            if (n.AuthorId != callerId) return RefusedLocked(itemId, callerId, callerIsOwner, "Only its writer can change a note.");
            n.Text = text; n.AtSeconds = write.AtSeconds is { } at && at >= 0 ? Math.Round(at, 1) : null; n.EditedAt = DateTimeOffset.UtcNow;
        }
        Changed();
        return List(itemId, callerId, callerIsOwner);
    }

    /// <summary>The writer, or the video's owner, removes a note.</summary>
    public NotesDto? Delete(string itemId, string noteId, string callerId, bool callerIsOwner)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(itemId, out var list)) return null;
            var n = list.FirstOrDefault(x => x.Id == noteId);
            if (n is null) return null;
            if (n.AuthorId != callerId && !callerIsOwner) return RefusedLocked(itemId, callerId, callerIsOwner, "Only its writer or the video's owner can remove a note.");
            list.Remove(n);
            if (list.Count == 0) _items.Remove(itemId);
        }
        Changed();
        return List(itemId, callerId, callerIsOwner);
    }

    /// <summary>A PC votes a note (+1/−1/0 = take it back); a writer can't vote their own.</summary>
    public NotesDto? Vote(string itemId, string noteId, string callerId, int vote, bool callerIsOwner)
    {
        lock (_lock)
        {
            var n = Find(itemId, noteId);
            if (n is null) return null;
            if (n.AuthorId == callerId) return RefusedLocked(itemId, callerId, callerIsOwner, "You can't vote on your own note.");
            if (vote == 0) n.Votes.Remove(callerId); else n.Votes[callerId] = Math.Sign(vote);
        }
        Save(soon: true); // the count of notes didn't change: no new catalog version
        return List(itemId, callerId, callerIsOwner);
    }

    private Note? Find(string itemId, string noteId) => _items.TryGetValue(itemId, out var l) ? l.FirstOrDefault(n => n.Id == noteId) : null;

    private NotesDto Refused(string itemId, string callerId, bool owner, string why)
    {
        var dto = List(itemId, callerId, owner); dto.Error = why; return dto;
    }

    /// <summary>Same, from inside the lock (List takes it again: Monitor is re-entrant).</summary>
    private NotesDto RefusedLocked(string itemId, string callerId, bool owner, string why) => Refused(itemId, callerId, owner, why);

    private void Changed()
    {
        Save(soon: true);
        _catalog.NotifyRatingsChanged(); // the number of notes is in the catalog (coalesced with votes)
    }

    private void Prune()
    {
        var removed = 0;
        lock (_lock)
            foreach (var id in _items.Keys.ToList())
                if (!_catalog.TryGetItem(id, out _, out _)) { _items.Remove(id); removed++; }
        lock (_lock)
        {
            var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var k in _lastAdd.Where(kv => kv.Value < old).Select(kv => kv.Key).ToList()) _lastAdd.Remove(k);
        }
        if (removed == 0) return;
        _log.LogInformation("notes: dropped those of {Count} video(s) no longer shared", removed);
        Save(soon: false);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var (id, list) in Json.Deserialize<Dictionary<string, List<Note>>>(File.ReadAllText(_path)) ?? new())
                if (list.Count > 0) _items[id] = list;
        }
        catch (Exception ex) { _log.LogWarning(ex, "notes: load failed"); }
    }

    private void Save(bool soon)
    {
        if (soon) { _dirty = true; _save.Change(2000, Timeout.Infinite); return; }
        try
        {
            string json;
            lock (_lock) { json = Json.Serialize(_items); _dirty = false; }
            AtomicFile.Write(_path, json);
        }
        catch (Exception ex) { _log.LogWarning(ex, "notes: save failed"); }
    }

    private void Save() => Save(soon: false);

    public void Dispose()
    {
        _save.Dispose();
        _prune.Dispose();
        if (_dirty) Save(soon: false);
    }
}
