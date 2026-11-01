using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Cast;

/// <summary>Who is watching which of my videos right now: one entry per open stream, counted by PC.</summary>
public sealed class ViewerTracker
{
    private readonly EventHub _events;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, int>> _open = new(StringComparer.Ordinal); // item → client → open streams
    private readonly Timer _publish;
    private bool _pending;

    public ViewerTracker(CatalogService catalog, EventHub events)
    {
        _events = events;
        _publish = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
        catalog.ViewerLookup = Count;
    }

    /// <summary>A stream of <paramref name="itemId"/> to <paramref name="clientId"/> is open until the result is disposed.</summary>
    public IDisposable Track(string itemId, string clientId)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(itemId, out var clients)) _open[itemId] = clients = new Dictionary<string, int>(StringComparer.Ordinal);
            clients[clientId] = clients.GetValueOrDefault(clientId) + 1;
        }
        Schedule();
        return new Release(() =>
        {
            lock (_lock)
            {
                if (!_open.TryGetValue(itemId, out var clients)) return;
                if (clients.TryGetValue(clientId, out var n) && n > 1) clients[clientId] = n - 1;
                else clients.Remove(clientId);
                if (clients.Count == 0) _open.Remove(itemId);
            }
            Schedule();
        });
    }

    /// <summary>PCs watching this video now.</summary>
    public int Count(string itemId)
    {
        lock (_lock) return _open.TryGetValue(itemId, out var clients) ? clients.Count : 0;
    }

    public Dictionary<string, int> Counts()
    {
        lock (_lock) return _open.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
    }

    private void Schedule()
    {
        lock (_lock)
        {
            if (_pending) return;
            _pending = true;
        }
        _publish.Change(500, Timeout.Infinite); // a player opening a stream connects a few times in a row: one event
    }

    private void Publish()
    {
        lock (_lock) _pending = false;
        _events.Publish("viewers", new { counts = Counts() });
    }

    private sealed class Release : IDisposable
    {
        private Action? _action;
        public Release(Action action) { _action = action; }
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
