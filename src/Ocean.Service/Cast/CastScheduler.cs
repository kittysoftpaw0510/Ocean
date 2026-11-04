using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.State;

namespace Ocean.Service.Cast;

/// <summary>
/// "Start the multicast in 10 minutes": one scheduled multicast at a time, kept in <c>cast-schedule.json</c> so it
/// survives a restart of the service or the PC. At its time it starts like any multicast (so every node gets the
/// usual "multicast started" notice through my catalog) and plays on by itself (<see cref="CastHost.Detach"/>)
/// until the host opens the player and takes over. Events: <c>castSchedule</c> (a <see cref="CastScheduleDto"/>).
/// </summary>
public sealed class CastScheduler : BackgroundService
{
    /// <summary>A start missed by less than this (the PC was asleep or off) still happens; later ones are dropped.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
    /// <summary>When the network has its share of multicasts already, it tries again this often, up to <see cref="Grace"/>.</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private readonly CastHost _cast;
    private readonly CatalogService _catalog;
    private readonly EventHub _events;
    private readonly ILogger<CastScheduler> _log;
    private readonly string _path = Path.Combine(OceanDefaults.DataDir, "cast-schedule.json");
    private readonly object _lock = new();
    private CastScheduleDto? _plan;
    /// <summary>While a start waits for the network (too many multicasts): when to give up.</summary>
    private DateTimeOffset? _retryUntil;
    private CancellationTokenSource _wake = new();

    public CastScheduler(CastHost cast, CatalogService catalog, EventHub events, ILogger<CastScheduler> log)
    {
        _cast = cast; _catalog = catalog; _events = events; _log = log;
        try { if (File.Exists(_path)) _plan = Json.Deserialize<CastScheduleDto>(File.ReadAllText(_path)); }
        catch (Exception ex) { _log.LogWarning("cast-schedule.json unreadable, ignored: {Msg}", ex.Message); }
        if (_plan is { Active: false }) _plan = null;
    }

    public CastScheduleDto Status()
    {
        lock (_lock) return _plan is null ? new CastScheduleDto() : Copy(_plan);
    }

    public CastScheduleDto Schedule(CastScheduleRequestDto req)
    {
        if (!_catalog.TryGetItem(req.ItemId, out var share, out var item))
            return new CastScheduleDto { Error = "This video is no longer in your shares." };
        var at = req.At.ToUniversalTime();
        if (at < DateTimeOffset.UtcNow.AddSeconds(-5)) return new CastScheduleDto { Error = "That time has already passed." };
        if (at > DateTimeOffset.UtcNow.AddDays(7)) return new CastScheduleDto { Error = "A multicast can be scheduled up to a week ahead." };
        var plan = new CastScheduleDto { Active = true, ItemId = req.ItemId, ItemName = item.Name, ShareName = share.Name, At = at, PreferLight = req.PreferLight };
        lock (_lock) { _plan = plan; _retryUntil = null; Save(); }
        _log.LogInformation("multicast scheduled: {Name} at {At:yyyy-MM-dd HH:mm} (in {Min:0} min)", plan.ItemName, at.ToLocalTime(), (at - DateTimeOffset.UtcNow).TotalMinutes);
        Wake();
        var st = Status();
        _events.Publish("castSchedule", st);
        return st;
    }

    public void Cancel()
    {
        CastScheduleDto? was;
        lock (_lock) { was = _plan; _plan = null; _retryUntil = null; Save(); }
        if (was is null) return;
        _log.LogInformation("scheduled multicast cancelled: {Name}", was.ItemName);
        Wake();
        _events.Publish("castSchedule", new CastScheduleDto());
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CastScheduleDto? plan;
            lock (_lock) plan = _plan is null ? null : Copy(_plan);
            var wait = Timeout.InfiniteTimeSpan;
            if (plan is not null)
            {
                var due = plan.At - DateTimeOffset.UtcNow;
                if (due <= TimeSpan.Zero) { TryStart(plan); continue; }
                // short naps: the clock can jump (sleep, time sync), a long single wait would miss the moment
                wait = due < TimeSpan.FromSeconds(30) ? due : TimeSpan.FromSeconds(30);
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
            try { await Task.Delay(wait, linked.Token); } catch (OperationCanceledException) { }
            if (_wake.IsCancellationRequested) { _wake.Dispose(); _wake = new CancellationTokenSource(); }
        }
    }

    private void TryStart(CastScheduleDto plan)
    {
        var now = DateTimeOffset.UtcNow;
        if (_retryUntil is null && now - plan.At > Grace) { Drop(plan, $"its time ({plan.At.ToLocalTime():HH:mm}) passed while Ocean wasn't running"); return; }
        var st = _cast.Start(plan.ItemId!, plan.PreferLight);
        if (st.Active && st.ItemId == plan.ItemId && st.Error is null)
        {
            _cast.Detach(); // nobody drives it yet: it plays from the start by itself until the host opens the player
            lock (_lock) { if (Same(_plan, plan)) { _plan = null; _retryUntil = null; Save(); } }
            _log.LogInformation("scheduled multicast started: {Name}", plan.ItemName);
            _events.Publish("castSchedule", new CastScheduleDto { ItemId = plan.ItemId, ItemName = plan.ItemName, ShareName = plan.ShareName, At = plan.At, Started = true });
            return;
        }
        // the network has its share of multicasts at the moment: try again a little later, up to the grace period
        var limit = _cast.Limit();
        if (!limit.Allowed)
        {
            lock (_lock) _retryUntil ??= now + Grace;
            if (now + RetryEvery <= _retryUntil)
            {
                _log.LogInformation("scheduled multicast waits: {Reason}", limit.Reason);
                lock (_lock) { if (Same(_plan, plan)) _plan!.At = now + RetryEvery; }
                return;
            }
        }
        Drop(plan, st.Error ?? "it could not start");
    }

    private void Drop(CastScheduleDto plan, string why)
    {
        lock (_lock) { if (Same(_plan, plan)) { _plan = null; _retryUntil = null; Save(); } }
        _log.LogWarning("scheduled multicast of {Name} dropped: {Why}", plan.ItemName, why);
        _events.Publish("castSchedule", new CastScheduleDto { ItemId = plan.ItemId, ItemName = plan.ItemName, ShareName = plan.ShareName, At = plan.At, Error = why });
    }

    private static bool Same(CastScheduleDto? a, CastScheduleDto b) => a is not null && a.ItemId == b.ItemId && a.Active;

    private static CastScheduleDto Copy(CastScheduleDto p) => new()
    {
        Active = p.Active, ItemId = p.ItemId, ItemName = p.ItemName, ShareName = p.ShareName, At = p.At, PreferLight = p.PreferLight,
    };

    private void Save()
    {
        try
        {
            if (_plan is null) { File.Delete(_path); return; }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(_plan));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("could not save the scheduled multicast: {Msg}", ex.Message); }
    }

    private void Wake() { try { _wake.Cancel(); } catch (ObjectDisposedException) { } }
}
