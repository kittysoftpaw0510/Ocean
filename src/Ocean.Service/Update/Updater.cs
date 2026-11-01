using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.State;

namespace Ocean.Service.Update;

/// <summary>
/// Looks for newer versions on the project's GitHub releases (every few hours and on demand), downloads the
/// installer into the data folder and tells the app, which runs it when the user says so. A release is
/// "vX.Y.Z" with the asset "Ocean-Setup-X.Y.Z.exe" (and "Ocean-Setup-X.Y.Z.exe.sha256" when published, which is
/// checked). No internet: the check fails quietly and is tried again later.
/// </summary>
public sealed class Updater : BackgroundService
{
    public const string Repo = "kittysoftpaw0510/Ocean";
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstAfter = TimeSpan.FromSeconds(90);

    private readonly ConfigStore _config;
    private readonly EventHub _events;
    private readonly ILogger<Updater> _log;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private UpdateStatusDto _status;
    private CancellationTokenSource _wake = new();

    public Updater(ConfigStore config, EventHub events, ILogger<Updater> log)
    {
        _config = config; _events = events; _log = log;
        _dir = Path.Combine(OceanDefaults.DataDir, "updates");
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ocean", Current));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _status = new UpdateStatusDto { Current = Current, AutoCheck = config.Current.CheckUpdates };
        config.Changed += c => { lock (_lock) _status.AutoCheck = c.CheckUpdates; };
    }

    /// <summary>The running version; OCEAN_UPDATE_FROM pretends an older one (to try the download on a dev PC).</summary>
    public static string Current => Environment.GetEnvironmentVariable("OCEAN_UPDATE_FROM") is { Length: > 0 } v ? v : OceanDefaults.AppVersion;

    public UpdateStatusDto Status { get { lock (_lock) return Clone(_status); } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { Directory.CreateDirectory(_dir); } catch { }
        await Task.Delay(FirstAfter, ct).ContinueWith(_ => { }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            if (_config.Current.CheckUpdates) await CheckAsync(ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
            try { await Task.Delay(Every, linked.Token); } catch (OperationCanceledException) { }
            if (_wake.IsCancellationRequested) { _wake.Dispose(); _wake = new CancellationTokenSource(); }
        }
    }

    /// <summary>A check now (Settings "Check for updates"); the result is in the status it returns.</summary>
    public async Task<UpdateStatusDto> CheckAsync(CancellationToken ct)
    {
        if (!await _checkLock.WaitAsync(0, ct)) { await _checkLock.WaitAsync(ct); _checkLock.Release(); return Status; }
        try
        {
            Set(s => { s.Checking = true; s.Error = ""; });
            Release? rel;
            try
            {
                using var resp = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode}");
                rel = ParseRelease(await resp.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("update check failed: {Msg}", ex.Message);
                Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = "Could not reach GitHub: " + ex.Message; });
                return Status;
            }
            if (rel is null)
            {
                Set(s => { s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow; s.Error = "No release with an installer found"; });
                return Status;
            }
            var newer = IsNewer(rel.Version, Current);
            Set(s =>
            {
                s.Checking = false; s.CheckedAt = DateTimeOffset.UtcNow;
                s.Latest = rel.Version; s.Notes = rel.Notes; s.Url = rel.Url; s.PublishedAt = rel.PublishedAt;
                s.Available = newer;
                if (!newer) { s.Ready = false; s.InstallerPath = null; s.InstallerSize = 0; }
            });
            if (!newer) { Cleanup(null); return Status; }
            await DownloadAsync(rel, ct);
            return Status;
        }
        finally { _checkLock.Release(); }
    }

    public void CheckSoon() => _wake.Cancel();

    private async Task DownloadAsync(Release rel, CancellationToken ct)
    {
        var path = Path.Combine(_dir, rel.AssetName);
        if (File.Exists(path) && new FileInfo(path).Length == rel.AssetSize && await VerifyAsync(path, rel, ct))
        {
            Set(s => { s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; });
            Cleanup(path);
            return;
        }
        var part = path + ".part";
        try
        {
            Set(s => { s.Downloading = true; s.Progress = 0; s.Ready = false; s.InstallerPath = null; });
            _log.LogInformation("downloading Ocean {Version} ({Size} MB)", rel.Version, rel.AssetSize / 1048576);
            using (var resp = await _http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
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
                        var p = rel.AssetSize > 0 ? Math.Min(1, (double)done / rel.AssetSize) : 0;
                        Set(s => s.Progress = p);
                    }
                }
            }
            if (new FileInfo(part).Length != rel.AssetSize) throw new IOException("the download is incomplete");
            if (!await VerifyAsync(part, rel, ct)) throw new IOException("the installer's checksum does not match the release");
            File.Move(part, path, overwrite: true);
            Set(s => { s.Downloading = false; s.Progress = 1; s.Ready = true; s.InstallerPath = path; s.InstallerSize = rel.AssetSize; s.Error = ""; });
            Cleanup(path);
            _log.LogInformation("Ocean {Version} is ready to install: {Path}", rel.Version, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("download of Ocean {Version} failed: {Msg}", rel.Version, ex.Message);
            try { File.Delete(part); } catch { }
            Set(s => { s.Downloading = false; s.Error = "Download failed: " + ex.Message; });
        }
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

    private sealed record Release(string Version, string Url, string Notes, DateTimeOffset? PublishedAt, string AssetName, string AssetUrl, long AssetSize, string? ShaUrl);

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
