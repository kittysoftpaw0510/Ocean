using System.Collections.Concurrent;

namespace Ocean.Core.Media;

/// <summary>Single STA thread that runs shell/COM work (thumbnails, property probing) sequentially.</summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaWorker(string name = "Ocean.Sta")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* isolated */ }
        }
    }

    public Task<T> RunAsync<T>(Func<T> fn, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { tcs.SetCanceled(ct); return tcs.Task; }
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(fn()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public int Pending => _queue.Count;

    public void Dispose() => _queue.CompleteAdding();
}
