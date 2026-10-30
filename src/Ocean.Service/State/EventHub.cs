using System.Collections.Concurrent;
using System.Threading.Channels;
using Ocean.Core;

namespace Ocean.Service.State;

/// <summary>Fan-out of service events to UI subscribers (Server-Sent Events).</summary>
public sealed class EventHub
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();

    public int SubscriberCount => _subs.Count;

    public void Publish(string type, object? data = null)
    {
        var payload = data is null ? "{}" : Json.Serialize(data);
        var frame = $"event: {type}\ndata: {payload}\n\n";
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(frame);
    }

    /// <summary>Yields SSE frames; emits a comment heartbeat when idle so dead connections are detected.</summary>
    public async IAsyncEnumerable<string> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        _subs[id] = ch;
        try
        {
            Task<bool>? read = null;
            while (!ct.IsCancellationRequested)
            {
                read ??= ch.Reader.WaitToReadAsync(ct).AsTask();
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var done = await Task.WhenAny(read, Task.Delay(Heartbeat, delayCts.Token));
                delayCts.Cancel();
                if (done != read) { yield return ": ping\n\n"; continue; }
                var ok = await read;
                read = null;
                if (!ok) yield break;
                while (ch.Reader.TryRead(out var frame)) yield return frame;
            }
        }
        finally
        {
            _subs.TryRemove(id, out _);
        }
    }
}
