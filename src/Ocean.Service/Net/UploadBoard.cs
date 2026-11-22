using Ocean.Core.Protocol;
using Ocean.Service.Discovery;
using Ocean.Service.State;

namespace Ocean.Service.Net;

/// <summary>
/// What this PC is sending to others right now as downloads (someone saving a copy of one of my files): which file,
/// to whom, how far, how fast. The owner can let one go at full speed: past the upload limit from Settings and not
/// slowed down for people watching. One entry per file and PC: a download that comes in several requests (retries,
/// ranges) is one row, and it stays a little after its last request (so a retry keeps its full speed).
/// Published as "uploads" events every second while anything is sent. When a PC starts downloading from me (after 2
/// minutes without any), an "uploadStarted" event tells the app once (with the files that began in its first 2
/// seconds), not once per file: a "Download all" of a folder is one notice.
/// </summary>
public sealed class UploadBoard : IDisposable
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuietBefore = TimeSpan.FromMinutes(2), Gather = TimeSpan.FromSeconds(2);

    // a PC's downloads: when it was last busy, and the notice being gathered (its first files)
    private readonly Dictionary<string, DateTimeOffset> _busyAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Due, List<string> Names, List<string> Shares)> _notices = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public required string Id, ItemId, Name, ShareName, ClientId;
        public long Size, Position, Sent, SentAtTick;
        public int Open;
        public bool FullSpeed;
        public double Speed;
        public DateTimeOffset StartedAt, LastActive;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly PeerRegistry _registry;
    private readonly EventHub _events;
    private readonly Timer _tick;
    private bool _announced;

    public UploadBoard(PeerRegistry registry, EventHub events)
    {
        _registry = registry; _events = events;
        _tick = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>A download of <paramref name="itemId"/> to <paramref name="clientId"/> starts at <paramref name="from"/>; it ends when the result is disposed.</summary>
    public Transfer Begin(string itemId, string name, string shareName, string clientId, long size, long from)
    {
        var id = Ocean.Core.Fnv.Hash(clientId + "|" + itemId).ToString("x16");
        Entry e;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_entries.TryGetValue(id, out e!))
            {
                _entries[id] = e = new Entry { Id = id, ItemId = itemId, Name = name, ShareName = shareName, ClientId = clientId, StartedAt = now };
                // a PC that starts downloading from me (it hadn't for a while): the owner is told, once
                if (_notices.TryGetValue(clientId, out var n)) { if (!n.Names.Contains(name)) n.Names.Add(name); if (!n.Shares.Contains(shareName)) n.Shares.Add(shareName); }
                else if (!_busyAt.TryGetValue(clientId, out var busy) || now - busy > QuietBefore)
                    _notices[clientId] = (now + Gather, new List<string> { name }, new List<string> { shareName });
            }
            _busyAt[clientId] = now;
            e.Size = size; e.Position = from; e.Open++; e.LastActive = DateTimeOffset.UtcNow;
        }
        Publish();
        return new Transfer(this, e);
    }

    /// <summary>One download being sent: counts its bytes, tells whether it goes at full speed.</summary>
    public sealed class Transfer : IDisposable
    {
        private readonly UploadBoard _board;
        private readonly Entry _e;
        private int _done;
        internal Transfer(UploadBoard board, Entry e) { _board = board; _e = e; }
        public bool FullSpeed => Volatile.Read(ref _e.FullSpeed);
        public void Sent(int bytes, long position)
        {
            lock (_board._lock) { _e.Sent += bytes; _e.Position = Math.Max(_e.Position, position); _e.LastActive = DateTimeOffset.UtcNow; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (_board._lock) { _e.Open--; _e.LastActive = DateTimeOffset.UtcNow; }
            _board.Publish();
        }
    }

    /// <summary>Full speed on or off for one download; false when it isn't here (any more).</summary>
    public bool SetFullSpeed(string id, bool on)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var e)) return false;
            e.FullSpeed = on;
        }
        Publish();
        return true;
    }

    public List<UploadDto> List()
    {
        lock (_lock) return _entries.Values.OrderBy(e => e.StartedAt).Select(ToDto).ToList();
    }

    private UploadDto ToDto(Entry e)
    {
        _registry.TryGet(e.ClientId, out var peer);
        return new UploadDto
        {
            Id = e.Id, ItemId = e.ItemId, Name = e.Name, ShareName = e.ShareName, PeerId = e.ClientId,
            PeerName = peer?.Name ?? "", PeerUser = peer?.User ?? "", Size = e.Size, Sent = Math.Min(e.Position, e.Size > 0 ? e.Size : long.MaxValue),
            SpeedBps = e.Open > 0 ? e.Speed : 0, FullSpeed = e.FullSpeed, Active = e.Open > 0, StartedAt = e.StartedAt,
        };
    }

    private void Tick()
    {
        bool any;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var e in _entries.Values)
            {
                // bytes of the last second, smoothed a little (a steady number to read)
                var rate = e.Sent - e.SentAtTick;
                e.SentAtTick = e.Sent;
                e.Speed = e.Speed <= 0 ? rate : e.Speed * 0.4 + rate * 0.6;
            }
            foreach (var gone in _entries.Values.Where(e => e.Open <= 0 && now - e.LastActive > Linger).Select(e => e.Id).ToList())
                _entries.Remove(gone);
            any = _entries.Count > 0;
            foreach (var e in _entries.Values.Where(e => e.Open > 0)) _busyAt[e.ClientId] = now;
            foreach (var quiet in _busyAt.Where(kv => now - kv.Value > QuietBefore * 2).Select(kv => kv.Key).ToList()) _busyAt.Remove(quiet);
            foreach (var (client, n) in _notices.Where(kv => kv.Value.Due <= now).ToList())
            {
                _notices.Remove(client);
                _registry.TryGet(client, out var peer);
                _events.Publish("uploadStarted", new UploadStartedDto
                {
                    PeerId = client, PeerName = peer?.Name ?? "", PeerUser = peer?.User ?? "",
                    Names = n.Names.Take(5).ToList(), Count = n.Names.Count, Shares = n.Shares,
                });
            }
        }
        // every second while anything is here, once more when the last one went
        if (any || _announced) Publish();
    }

    private void Publish()
    {
        var list = List();
        _announced = list.Count > 0;
        _events.Publish("uploads", list);
    }

    public void Dispose() => _tick.Dispose();
}
