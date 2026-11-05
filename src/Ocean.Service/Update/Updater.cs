using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.State;

namespace Ocean.Service.Update;

/// <summary>
/// Looks for newer versions on the project's GitHub releases and, depending on the mode (Settings), tells the app or
/// also downloads the installer ahead; the app runs it when the user says so. A release is "vX.Y.Z" with the asset
/// "Ocean-Setup-X.Y.Z.exe" (and "Ocean-Setup-X.Y.Z.exe.sha256", which is checked when present).
/// <list type="bullet">
/// <item><b>auto-update</b>: checks by itself and downloads in the background; the user installs with one click.</item>
/// <item><b>auto-check</b> (default): checks by itself and tells the user; it downloads when they click Update.</item>
/// <item><b>manual</b>: only "Check for updates" looks.</item>
/// </list>
/// Every start looks at once (the service starting, and the app starting unless a check succeeded in the last 10 min),
/// and when there is no connection yet tries again after 10 s, 1 min and 10 min. After that checks follow the clock,
/// not a timer: the time of the last one is kept on disk, and every few minutes the service looks whether 4 h have
/// passed (30 min after a failed one), so sleep and restarts don't skip checks. A check or a background download that
/// can't reach the internet stays silent (the log says so); only "Check for updates" reports it, since the user asked.
/// <para>
/// Updating is what nobody comes to Ocean for, so it never gets in the way: a download in the background
/// (auto-update) waits until nothing is being watched, streamed to others, multicast or downloaded, pauses when that
/// starts, goes at most 2 MB/s, and picks up where it stopped: after a failure 1, 5, 15, then every 30 min later, and
/// soon after the network changes (it is usually back then). "Update now" downloads at full speed (the user asked)
/// and, when interrupted, goes on by itself after 10 s, 1 min and 5 min before it reports a failure. A connection
/// that sends nothing for 60 s counts as broken. Installing is always the user's click (the app).
/// </para> The newest release seen is kept too, so the app hears about it again right after a restart. When GitHub's
/// API refuses (60 requests an hour for one internet address, shared by every PC behind it), the release page is read
/// instead. No internet: the check fails, is logged, and is tried again later.
/// </summary>
public sealed class Updater : BackgroundService
{
    public const string Repo = "kittysoftpaw0510/Ocean";
    private static readonly TimeSpan Every = TimeSpan.FromHours(4);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);
    /// <summary>At start: the check itself (after a few seconds for the network to settle), then 3 more tries.</summary>
    private static readonly TimeSpan[] StartupWaits = TestWaits() ??
        new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10) };
    /// <summary>The app starting doesn't look again when a check succeeded this recently (the PC just booted).</summary>
    private static readonly TimeSpan RecentEnough = TimeSpan.FromMinutes(10);

    /// <summary>Developers only: OCEAN_TEST_UPDATE_WAITS = the startup waits in seconds ("1,2,3,4").</summary>
    private static TimeSpan[]? TestWaits()
    {
        var raw = Environment.GetEnvironmentVariable("OCEAN_TEST_UPDATE_WAITS");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var waits = raw.Split(',').Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? TimeSpan.FromSeconds(v) : (TimeSpan?)null).ToList();
        return waits.All(w => w is not null) && waits.Count > 0 ? waits.Select(w => w!.Value).ToArray() : null;
    }

    /// <summary>Developers only: OCEAN_TEST_UPDATE_FAIL = how many checks fail as if there were no connection.</summary>
    private int _simulatedOffline = OceanDefaults.EnvPort("OCEAN_TEST_UPDATE_FAIL", 0);
    private int _startupRunning;

    private readonly ConfigStore _config;
    private readonly EventHub _events;
    private readonly ILogger<Updater> _log;
    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect;
    private readonly string _dir;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private UpdateStatusDto _status;
    private Saved _saved = new();
    private Release? _found;
    private CancellationTokenSource _wake = new();
    private CancellationToken _stopping = CancellationToken.None;
    private readonly IServiceProvider _services;
    /// <summary>A background download (auto-update) is not tried before this (after a failure, or while Ocean is busy).</summary>
    private DateTimeOffset _nextBackgroundTry = DateTimeOffset.MinValue;
    private string? _waitingFor;
    /// <summary>A download in the background goes at most this fast (bytes a second): it must never crowd the network.</summary>
    private const double BackgroundRate = 2 * 1024 * 1024;
    /// <summary>A background download that failed is tried again after these waits (then every 30 min), from where it stopped.</summary>
    private static readonly TimeSpan[] BackgroundRetryWaits = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30) };
    private int _backgroundFailures;
    /// <summary>"Update now" (the user is waiting for it) tries again by itself after these waits before it reports a failure.</summary>
    private static readonly TimeSpan[] UserRetryWaits = { TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) };
    /// <summary>Nothing arrives for this long: the download is taken as broken (a connection can hang without an error).</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Developers only: OCEAN_TEST_UPDATE_DROP = "20x2": the connection drops after 20 MB, twice.</summary>
    private int _testDrops;
    private readonly long _testDropBytes;

    public Updater(ConfigStore config, EventHub events, ILogger<Updater> log, IServiceProvider services)
    {
        _config = config; _events = events; _log = log; _services = services;
        if (Environment.GetEnvironmentVariable("OCEAN_TEST_UPDATE_DROP") is { } drop && drop.Split('x') is [var mb, var times]
            && long.TryParse(mb, out var dropMb) && int.TryParse(times, out var dropTimes))
        { _testDropBytes = dropMb * 1048576; _testDrops = dropTimes; }
        // The network changed (often: it is back): what failed for lack of it is tried again soon, not in half an hour.
        Net.LocalNetworks.Changed += () =>
        {
            if (ModeOf(_config.Current) == UpdateModes.Manual) return;
            if (!_saved.LastAttemptOk) _ = StartupChecksAsync("the network changed", _stopping);
            if (_backgroundFailures > 0) { _nextBackgroundTry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10); WakeAt(TimeSpan.FromSeconds(10)); }
        };
        _dir = Path.Combine(OceanDefaults.DataDir, "updates");
        _statePath = Path.Combine(OceanDefaults.DataDir, "update-state.json");
        _http = Client(Handler(redirect: true));
        _noRedirect = Client(Handler(redirect: false));
        _status = new UpdateStatusDto { Current = Current, Mode = ModeOf(config.Current), AutoCheck = ModeOf(config.Current) != UpdateModes.Manual };
        config.Changed += c =>
        {
            var mode = ModeOf(c);
            string before;
            lock (_lock) { before = _status.Mode; _status.Mode = mode; _status.AutoCheck = mode != UpdateModes.Manual; }
            if (before == mode) return;
            _log.LogInformation("updates: {Mode}", mode);
            if (mode == UpdateModes.AutoUpdate && _found is { } rel && !Status.Ready) _ = DownloadInBackground(rel, quiet: true); // fetch what was only announced so far
            CheckSoon();
        };
    }

    /// <summary>
    /// In classic networking (a network add-on such as Astrill's ASProxy breaks asynchronous socket I/O) the
    /// connection to GitHub uses blocking sockets too: a normal handler there kills the service within a second of
    /// the first check, i.e. 3 s after every start (see <see cref="NetworkCompat"/>).
    /// </summary>
    private static HttpMessageHandler Handler(bool redirect) => NetworkCompat.Classic
        ? new SocketsHttpHandler { ConnectCallback = NetworkCompat.ConnectAsync, AllowAutoRedirect = redirect, ConnectTimeout = TimeSpan.FromSeconds(30) }
        : new HttpClientHandler { AllowAutoRedirect = redirect };

    private static HttpClient Client(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ocean", Current));
        return http;
    }

    /// <summary>The running version; OCEAN_UPDATE_FROM pretends an older one (to try the download on a dev PC).</summary>
    public static string Current => Environment.GetEnvironmentVariable("OCEAN_UPDATE_FROM") is { Length: > 0 } v ? v : OceanDefaults.AppVersion;

    /// <summary>The configured mode; older settings only had "check for updates" on/off.</summary>
    public static string ModeOf(ServiceConfig c) => UpdateModes.Normalize(c.UpdateMode) ?? (c.CheckUpdates ? UpdateModes.AutoCheck : UpdateModes.Manual);

    public UpdateStatusDto Status { get { lock (_lock) return Clone(_status); } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        try { Directory.CreateDirectory(_dir); } catch { }
        Restore();
        await StartupChecksAsync("the service started", ct);
        while (!ct.IsCancellationRequested)
        {
            if (ModeOf(_config.Current) != UpdateModes.Manual && Due()) await CheckCoreAsync(ct, userAsked: false);
            // auto-update: a newer version known but not downloaded yet (it waited for a quiet moment, or failed)
            if (ModeOf(_config.Current) == UpdateModes.AutoUpdate && _found is { } pending && IsNewer(pending.Version, Current)
                && !Status.Ready && !Status.Downloading && DateTimeOffset.UtcNow >= _nextBackgroundTry)
                _ = DownloadInBackground(pending, quiet: true);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
            try { await Task.Delay(Look, linked.Token); } catch (OperationCanceledException) { }
            if (_wake.IsCancellationRequested) { _wake.Dispose(); _wake = new CancellationTokenSource(); }
        }
    }

    /// <summary>4 h after the last check, 30 min after a failed one (wall clock, so sleep and restarts count).</summary>
    private bool Due()
    {
        var last = _saved.LastAttempt;
        if (last is null) return true;
        var since = DateTimeOffset.UtcNow - last.Value;
        if (since < TimeSpan.Zero) return true; // the clock went back
        return since >= (_saved.LastAttemptOk ? Every : AfterFailure);
    }

    /// <summary>
    /// At start: looks, and while there is no connection tries again after 10 s, 1 min and 10 min (silently). One run at
    /// a time; Manual never looks by itself.
    /// </summary>
    private async Task StartupChecksAsync(string why, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _startupRunning, 1) == 1) return;
        try
        {
            for (var i = 0; i < StartupWaits.Length; i++)
            {
                try { await Task.Delay(StartupWaits[i], ct); } catch (OperationCanceledException) { return; }
                if (ModeOf(_config.Current) == UpdateModes.Manual) return;
                if (await CheckCoreAsync(ct, userAsked: false)) return;
                if (i + 1 < StartupWaits.Length)
                    _log.LogInformation("update check ({Why}): no connection; trying again in {Wait}", why, Wait(StartupWaits[i + 1]));
            }
            _log.LogInformation("update check ({Why}): still no connection after {Tries} tries; next look in {Min} min", why, StartupWaits.Length, (int)AfterFailure.TotalMinutes);
        }
        finally { Interlocked.Exchange(ref _startupRunning, 0); }
    }

    private static string Wait(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    /// <summary>The app started: look as at the service's start, unless a check succeeded a moment ago (the PC just booted).</summary>
    public void AppStarted()
    {
        if (ModeOf(_config.Current) == UpdateModes.Manual) return;
        if (_saved is { LastAttemptOk: true, LastAttempt: { } at } && DateTimeOffset.UtcNow - at is var since && since >= TimeSpan.Zero && since < RecentEnough) return;
        _ = StartupChecksAsync("the app started", _stopping);
    }

    /// <summary>"Check for updates": looks now and reports a failure (the user asked); the result is in the status.</summary>
    public async Task<UpdateStatusDto> CheckAsync(CancellationToken ct)
    {
        await CheckCoreAsync(ct, userAsked: true);
        return Status;
    }

    /// <summary>
    /// Looks once. True when GitHub answered (whatever it said). A failure only shows in the status when the user asked;
    /// otherwise it is silent (logged).
    /// </summary>
    private async Task<bool> CheckCoreAsync(CancellationToken ct, bool userAsked)
    {
        if (!await _checkLock.WaitAsync(0, ct)) { await _checkLock.WaitAsync(ct); _checkLock.Release(); return _saved.LastAttemptOk; }
        try
        {
            Set(s => { s.Checking = true; if (userAsked) s.Error = ""; });
            Release? rel;
            string via;
            try
            {
                if (_simulatedOffline > 0) { _simulatedOffline--; throw new HttpRequestException("no connection (simulated)"); }
                (rel, via) = await FindLatestAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation("update check failed: {Msg}", ex.Message);
                Remember(ok: false, null);
                if (userAsked) Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = "Could not reach GitHub: " + ex.Message; });
                else Set(s => s.Checking = false); // silent: no connection is nothing to tell anyone
                return false;
            }
            Remember(ok: true, rel);
            if (rel is null)
            {
                _log.LogInformation("update check ({Via}): no release with an installer", via);
                Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = userAsked ? "No release with an installer found" : ""; });
                return true;
            }
            var newer = IsNewer(rel.Version, Current);
            _log.LogInformation("update check ({Via}): latest {Latest}, this PC {Current}{Note}", via, rel.Version, Current, newer ? " - newer version available" : "");
            Announce(rel, newer);
            if (!newer) { Cleanup(null); return true; }
            // Already downloaded and complete: ready, whatever the mode. Otherwise only auto-update fetches it now.
            if (InstallerOnDisk(rel) is { } path) Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
            else if (ModeOf(_config.Current) == UpdateModes.AutoUpdate) await DownloadAsync(rel, ct, quiet: !userAsked);
            return true;
        }
        finally { _checkLock.Release(); }
    }

    public void CheckSoon() => _wake.Cancel();

    /// <summary>"Update now": download the newest version (looking first when none is known yet); progress comes as events.</summary>
    public async Task<UpdateStatusDto> DownloadNowAsync(CancellationToken ct)
    {
        if (_found is null || !IsNewer(_found.Version, Current)) await CheckAsync(ct);
        if (_found is { } rel && IsNewer(rel.Version, Current) && !Status.Ready) _ = DownloadInBackground(rel, quiet: false);
        return Status;
    }

    private Task DownloadInBackground(Release rel, bool quiet) => Task.Run(() => DownloadAsync(rel, _stopping, quiet));

    private void Announce(Release rel, bool newer, DateTimeOffset? checkedAt = null)
    {
        if (newer) _found = rel;
        Set(s =>
        {
            s.Checking = false; s.CheckedAt = checkedAt ?? DateTimeOffset.UtcNow;
            s.Latest = rel.Version; s.Notes = rel.Notes; s.Url = rel.Url; s.PublishedAt = rel.PublishedAt; s.InstallerSize = rel.AssetSize;
            s.Available = newer;
            if (!newer) { s.Ready = false; s.InstallerPath = null; }
        });
    }

    // ---- what is remembered between restarts ----

    private sealed class Saved
    {
        public DateTimeOffset? LastAttempt { get; set; }
        public bool LastAttemptOk { get; set; }
        public Release? Latest { get; set; }
    }

    private void Restore()
    {
        try { if (File.Exists(_statePath)) _saved = Json.Deserialize<Saved>(File.ReadAllText(_statePath)) ?? new(); }
        catch (Exception ex) { _log.LogDebug("update state not readable: {Msg}", ex.Message); _saved = new(); }
        if (_saved.Latest is not { } rel) return;
        var newer = IsNewer(rel.Version, Current);
        Announce(rel, newer, _saved.LastAttempt); // the app hears about a known newer version right away, not hours later
        if (!newer) { Cleanup(null); return; }
        if (InstallerOnDisk(rel) is { } path) Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
        else if (ModeOf(_config.Current) == UpdateModes.AutoUpdate) _ = DownloadInBackground(rel, quiet: true);
    }

    private void Remember(bool ok, Release? latest)
    {
        _saved.LastAttempt = DateTimeOffset.UtcNow;
        _saved.LastAttemptOk = ok;
        if (ok) _saved.Latest = latest;
        try { AtomicFile.Write(_statePath, Json.Serialize(_saved)); }
        catch (Exception ex) { _log.LogDebug("update state not saved: {Msg}", ex.Message); }
    }

    // ---- downloading ----

    /// <summary>The installer already downloaded and complete (size; the checksum was checked when it was downloaded).</summary>
    private string? InstallerOnDisk(Release rel)
    {
        var path = Path.Combine(_dir, rel.AssetName);
        return File.Exists(path) && (rel.AssetSize <= 0 || new FileInfo(path).Length == rel.AssetSize) ? path : null;
    }

    /// <summary>What Ocean is busy with right now that a background download must not compete with (null: nothing).</summary>
    private string? Busy()
    {
        try
        {
            if (_services.GetService<Cast.CastHost>()?.Status().Active == true) return "a multicast you are hosting";
            if (_services.GetService<Cast.CastReceiver>()?.Status() is { Active: true }) return "a multicast being watched";
            if (_services.GetService<Peers.StreamCache>()?.InUse == true) return "a video being watched";
            if (_services.GetService<Cast.ViewerTracker>()?.Counts().Values.Sum() > 0) return "others watching your videos";
            if (_services.GetService<Downloads.DownloadManager>()?.List().Any(j => j.Status == DownloadStatus.Running) == true) return "downloads";
        }
        catch { }
        return null;
    }

    private sealed class PausedForActivity(string what) : Exception($"paused for {what}");

    /// <summary>
    /// <paramref name="quiet"/>: in the background (auto-update): only while Ocean is otherwise idle, paced, pausing when
    /// something starts; a failure is only logged and tried again 30 min later. Either way a broken download goes on
    /// from where it stopped (the ".part" file is kept), and the installer is checked before it counts as ready.
    /// </summary>
    private async Task DownloadAsync(Release rel, CancellationToken ct, bool quiet = false)
    {
        if (quiet && Busy() is { } busy)
        {
            if (_waitingFor != busy) _log.LogInformation("update download waits: {What}", busy);
            _waitingFor = busy;
            _nextBackgroundTry = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            return;
        }
        _waitingFor = null;
        if (!await _downloadLock.WaitAsync(0, ct)) return; // one at a time; the running one reports
        var path = Path.Combine(_dir, rel.AssetName);
        var part = path + ".part";
        try
        {
        for (var attempt = 0; ; attempt++)
        try
        {
            if (InstallerOnDisk(rel) is not null && await VerifyAsync(path, rel, ct))
            {
                Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
                Cleanup(path);
                return;
            }
            long have = 0;
            try { if (File.Exists(part)) have = new FileInfo(part).Length; } catch { }
            if (rel.AssetSize > 0 && have >= rel.AssetSize) { try { File.Delete(part); } catch { } have = 0; }
            Set(s => { s.Downloading = true; s.Progress = rel.AssetSize > 0 ? (double)have / rel.AssetSize : 0; s.Ready = false; s.InstallerPath = null; s.Error = ""; });
            _log.LogInformation("downloading Ocean {Version} ({Size} MB){From}{How}", rel.Version, rel.AssetSize / 1048576,
                have > 0 ? $", going on from {have / 1048576} MB" : "", quiet ? " in the background (at most 2 MB/s, pauses while Ocean is in use)" : "");
            long size;
            using (var req = new HttpRequestMessage(HttpMethod.Get, rel.AssetUrl))
            {
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var resumed = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed) have = 0; // the server sends the whole file: start over
                size = rel.AssetSize > 0 ? rel.AssetSize : (resp.Content.Headers.ContentLength ?? 0) + have;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                var buf = new byte[1 << 16]; long done = have; var lastShown = DateTime.UtcNow; var lastBusyCheck = DateTime.UtcNow;
                var paced = System.Diagnostics.Stopwatch.StartNew(); long pacedBytes = 0;
                int n;
                while ((n = await ReadOrStallAsync(src, buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (_testDrops > 0 && done - have >= _testDropBytes) { _testDrops--; throw new IOException("the connection dropped (simulated)"); }
                    if (quiet)
                    {
                        // never faster than BackgroundRate
                        pacedBytes += n;
                        var ahead = pacedBytes / BackgroundRate - paced.Elapsed.TotalSeconds;
                        if (ahead > 0.02) await Task.Delay(TimeSpan.FromSeconds(ahead), ct);
                        // Ocean got busy: stop here, the rest comes later from this point
                        if (DateTime.UtcNow - lastBusyCheck > TimeSpan.FromSeconds(2))
                        {
                            lastBusyCheck = DateTime.UtcNow;
                            if (Busy() is { } what) throw new PausedForActivity(what);
                        }
                    }
                    if (DateTime.UtcNow - lastShown > TimeSpan.FromSeconds(1))
                    {
                        lastShown = DateTime.UtcNow;
                        var p = size > 0 ? Math.Min(1, (double)done / size) : 0;
                        Set(s => s.Progress = p);
                    }
                }
            }
            if (size > 0 && new FileInfo(part).Length != size) throw new IOException("the download is incomplete");
            if (!await VerifyAsync(part, rel, ct))
            {
                try { File.Delete(part); } catch { } // damaged: the next try starts over
                throw new IOException("the installer's checksum does not match the release");
            }
            File.Move(part, path, overwrite: true);
            Set(s => { s.Downloading = false; s.Progress = 1; s.Ready = true; s.InstallerPath = path; s.InstallerSize = new FileInfo(path).Length; s.Error = ""; });
            Cleanup(path);
            _backgroundFailures = 0;
            _log.LogInformation("Ocean {Version} is ready to install: {Path}", rel.Version, path);
            break;
        }
        catch (PausedForActivity p)
        {
            _log.LogInformation("update download paused ({Why}); it goes on from here later", p.Message);
            _nextBackgroundTry = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            Set(s => s.Downloading = false);
            break;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !quiet && attempt < UserRetryWaits.Length)
        {
            // "Update now": the user is waiting, so keep at it for a while before calling it a failure
            var wait = UserRetryWaits[attempt];
            _log.LogInformation("download of Ocean {Version} interrupted ({Msg}): going on from where it stopped in {Wait:0} s", rel.Version, ex.Message, wait.TotalSeconds);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { Set(s => s.Downloading = false); break; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // what arrived is kept: the next try goes on from there
            var wait = BackgroundRetryWaits[Math.Min(_backgroundFailures, BackgroundRetryWaits.Length - 1)];
            _backgroundFailures++;
            _nextBackgroundTry = DateTimeOffset.UtcNow + wait;
            if (quiet) WakeAt(wait);
            _log.LogWarning("download of Ocean {Version} failed: {Msg}{Next}", rel.Version, ex.Message,
                quiet ? $"; trying again in {wait.TotalMinutes:0} min (or as soon as the network changes)" : "");
            Set(s => { s.Downloading = false; if (!quiet) s.Error = "Download failed: " + ex.Message; });
            break;
        }
        catch (OperationCanceledException) { Set(s => s.Downloading = false); break; } // stopping: the part stays for next time
        }
        finally { _downloadLock.Release(); }
    }

    /// <summary>One read; a connection that sends nothing for <see cref="StallTimeout"/> counts as broken.</summary>
    private static async Task<int> ReadOrStallAsync(Stream src, byte[] buf, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try { return await src.ReadAsync(buf, stall.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"nothing arrived for {StallTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Wakes the 5-minute loop after <paramref name="after"/> (a background retry that is due).</summary>
    private void WakeAt(TimeSpan after) => _ = Task.Delay(after, _stopping).ContinueWith(t => { if (!t.IsCanceled) CheckSoon(); }, TaskScheduler.Default);

    /// <summary>SHA-256 against the ".sha256" asset when the release has one; otherwise size alone.</summary>
    private async Task<bool> VerifyAsync(string path, Release rel, CancellationToken ct)
    {
        if (rel.ShaUrl is null) return true;
        string expected;
        try { expected = (await _http.GetStringAsync(rel.ShaUrl, ct)).Trim().Split(' ', '\t', '\n')[0].Trim(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug("checksum file not readable: {Msg}", ex.Message); return true; }
        if (expected.Length != 64) return true;
        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes older downloads (and any leftover .part).</summary>
    private void Cleanup(string? keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(f);
        }
        catch { }
    }

    private void Set(Action<UpdateStatusDto> change)
    {
        UpdateStatusDto copy;
        lock (_lock) { change(_status); copy = Clone(_status); }
        _events.Publish("update", copy);
    }

    private static UpdateStatusDto Clone(UpdateStatusDto s) => Json.Deserialize<UpdateStatusDto>(Json.Serialize(s))!;

    // ---- GitHub ----

    public sealed record Release(string Version, string Url, string Notes, DateTimeOffset? PublishedAt, string AssetName, string AssetUrl, long AssetSize, string? ShaUrl);

    /// <summary>The newest release: GitHub's API (release notes, exact assets), else its release page.</summary>
    private async Task<(Release? Release, string Via)> FindLatestAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("OCEAN_TEST_UPDATE_NO_API") != "1")
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub API answered {(int)resp.StatusCode}");
                return (ParseRelease(await resp.Content.ReadAsStringAsync(ct)), "API");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation("GitHub API not usable ({Msg}); reading the release page instead", ex.Message);
            }
        }
        return (await FromReleasePageAsync(ct), "release page");
    }

    /// <summary>
    /// Without the API: github.com/.../releases/latest redirects to .../releases/tag/vX.Y.Z, and the installer has a
    /// fixed address under .../releases/download/vX.Y.Z/. Its size comes from the download's headers.
    /// </summary>
    private async Task<Release?> FromReleasePageAsync(CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{Repo}/releases/latest");
        using var resp = await _noRedirect.SendAsync(head, ct);
        var location = resp.Headers.Location?.ToString() ?? "";
        if ((int)resp.StatusCode is < 300 or >= 400 || !location.Contains("/releases/tag/", StringComparison.Ordinal))
            throw new HttpRequestException($"the release page answered {(int)resp.StatusCode}");
        var tag = Uri.UnescapeDataString(location[(location.LastIndexOf('/') + 1)..]);
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) return null;
        var asset = $"Ocean-Setup-{version}.exe";
        var assetUrl = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{asset}";
        using var probe = new HttpRequestMessage(HttpMethod.Head, assetUrl);
        using var assetResp = await _http.SendAsync(probe, ct);
        if (!assetResp.IsSuccessStatusCode) return null; // a release without the installer
        var size = assetResp.Content.Headers.ContentLength ?? 0;
        var url = location.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? location : $"https://github.com{location}";
        return new Release(version, url, "", null, asset, assetUrl, size, assetUrl + ".sha256");
    }

    private static Release? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) return null;
        string? assetName = null, assetUrl = null, shaUrl = null; long size = 0;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith("Ocean-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { assetName = name; assetUrl = url; size = a.GetProperty("size").GetInt64(); }
                else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
        if (assetName is null || assetUrl is null || size <= 0) return null;
        DateTimeOffset? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var pa) ? pa : null;
        return new Release(version, r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "", published, assetName, assetUrl, size, shaUrl);
    }

    public static bool IsNewer(string candidate, string current)
        => Version.TryParse(candidate, out var c) && Version.TryParse(current, out var cur) && Pad(c) > Pad(cur);

    private static Version Pad(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
