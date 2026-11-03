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
/// Checks are scheduled by the clock, not a timer: the time of the last one is kept on disk, and every few minutes the
/// service looks whether 4 h have passed (30 min after a failed one), so sleep and restarts neither skip nor repeat
/// checks. The newest release seen is kept too, so the app hears about it again right after a restart. When GitHub's
/// API refuses (60 requests an hour for one internet address, shared by every PC behind it), the release page is read
/// instead. No internet: the check fails, is logged, and is tried again later.
/// </summary>
public sealed class Updater : BackgroundService
{
    public const string Repo = "kittysoftpaw0510/Ocean";
    private static readonly TimeSpan Every = TimeSpan.FromHours(4);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(OceanDefaults.EnvPort("OCEAN_TEST_UPDATE_FIRST_AFTER", 60));

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

    public Updater(ConfigStore config, EventHub events, ILogger<Updater> log)
    {
        _config = config; _events = events; _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "updates");
        _statePath = Path.Combine(OceanDefaults.DataDir, "update-state.json");
        _http = Client(new HttpClientHandler());
        _noRedirect = Client(new HttpClientHandler { AllowAutoRedirect = false });
        _status = new UpdateStatusDto { Current = Current, Mode = ModeOf(config.Current), AutoCheck = ModeOf(config.Current) != UpdateModes.Manual };
        config.Changed += c =>
        {
            var mode = ModeOf(c);
            string before;
            lock (_lock) { before = _status.Mode; _status.Mode = mode; _status.AutoCheck = mode != UpdateModes.Manual; }
            if (before == mode) return;
            _log.LogInformation("updates: {Mode}", mode);
            if (mode == UpdateModes.AutoUpdate && _found is { } rel && !Status.Ready) _ = DownloadInBackground(rel); // fetch what was only announced so far
            CheckSoon();
        };
    }

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
        await Task.Delay(FirstLook, ct).ContinueWith(_ => { }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            if (ModeOf(_config.Current) != UpdateModes.Manual && Due()) await CheckAsync(ct);
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

    /// <summary>Looks now ("Check for updates", or when due); the result is in the status it returns.</summary>
    public async Task<UpdateStatusDto> CheckAsync(CancellationToken ct)
    {
        if (!await _checkLock.WaitAsync(0, ct)) { await _checkLock.WaitAsync(ct); _checkLock.Release(); return Status; }
        try
        {
            Set(s => { s.Checking = true; s.Error = ""; });
            Release? rel;
            string via;
            try { (rel, via) = await FindLatestAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation("update check failed: {Msg}; trying again in {Min} min", ex.Message, (int)AfterFailure.TotalMinutes);
                Remember(ok: false, null);
                Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = "Could not reach GitHub: " + ex.Message; });
                return Status;
            }
            Remember(ok: true, rel);
            if (rel is null)
            {
                _log.LogInformation("update check ({Via}): no release with an installer", via);
                Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = "No release with an installer found"; });
                return Status;
            }
            var newer = IsNewer(rel.Version, Current);
            _log.LogInformation("update check ({Via}): latest {Latest}, this PC {Current}{Note}", via, rel.Version, Current, newer ? " - newer version available" : "");
            Announce(rel, newer);
            if (!newer) { Cleanup(null); return Status; }
            // Already downloaded and complete: ready, whatever the mode. Otherwise only auto-update fetches it now.
            if (InstallerOnDisk(rel) is { } path) Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
            else if (ModeOf(_config.Current) == UpdateModes.AutoUpdate) await DownloadAsync(rel, ct);
            return Status;
        }
        finally { _checkLock.Release(); }
    }

    public void CheckSoon() => _wake.Cancel();

    /// <summary>"Update now": download the newest version (looking first when none is known yet); progress comes as events.</summary>
    public async Task<UpdateStatusDto> DownloadNowAsync(CancellationToken ct)
    {
        if (_found is null || !IsNewer(_found.Version, Current)) await CheckAsync(ct);
        if (_found is { } rel && IsNewer(rel.Version, Current) && !Status.Ready) _ = DownloadInBackground(rel);
        return Status;
    }

    private Task DownloadInBackground(Release rel) => Task.Run(() => DownloadAsync(rel, _stopping));

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
        else if (ModeOf(_config.Current) == UpdateModes.AutoUpdate) _ = DownloadInBackground(rel);
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

    private async Task DownloadAsync(Release rel, CancellationToken ct)
    {
        if (!await _downloadLock.WaitAsync(0, ct)) return; // one at a time; the running one reports
        var path = Path.Combine(_dir, rel.AssetName);
        var part = path + ".part";
        try
        {
            if (InstallerOnDisk(rel) is not null && await VerifyAsync(path, rel, ct))
            {
                Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
                Cleanup(path);
                return;
            }
            Set(s => { s.Downloading = true; s.Progress = 0; s.Ready = false; s.InstallerPath = null; s.Error = ""; });
            _log.LogInformation("downloading Ocean {Version} ({Size} MB)", rel.Version, rel.AssetSize / 1048576);
            long size;
            using (var resp = await _http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                size = rel.AssetSize > 0 ? rel.AssetSize : resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                var buf = new byte[1 << 16]; long done = 0; var lastShown = DateTime.UtcNow;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (DateTime.UtcNow - lastShown > TimeSpan.FromSeconds(1))
                    {
                        lastShown = DateTime.UtcNow;
                        var p = size > 0 ? Math.Min(1, (double)done / size) : 0;
                        Set(s => s.Progress = p);
                    }
                }
            }
            if (size > 0 && new FileInfo(part).Length != size) throw new IOException("the download is incomplete");
            if (!await VerifyAsync(part, rel, ct)) throw new IOException("the installer's checksum does not match the release");
            File.Move(part, path, overwrite: true);
            Set(s => { s.Downloading = false; s.Progress = 1; s.Ready = true; s.InstallerPath = path; s.InstallerSize = new FileInfo(path).Length; s.Error = ""; });
            Cleanup(path);
            _log.LogInformation("Ocean {Version} is ready to install: {Path}", rel.Version, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("download of Ocean {Version} failed: {Msg}", rel.Version, ex.Message);
            try { File.Delete(part); } catch { }
            Set(s => { s.Downloading = false; s.Error = "Download failed: " + ex.Message; });
        }
        catch (OperationCanceledException) { try { File.Delete(part); } catch { } Set(s => s.Downloading = false); }
        finally { _downloadLock.Release(); }
    }

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
