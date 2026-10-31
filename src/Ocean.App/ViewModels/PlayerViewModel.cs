using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>State of the in-app player overlay. Playback itself is driven by the PlayerView (MediaElement).</summary>
public partial class PlayerViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private ItemVm? _item;
    [ObservableProperty] private PeerVm? _peer;
    /// <summary>Downloading applies to another node's video, not to one of my own.</summary>
    public bool CanDownload => Peer is not null;
    partial void OnPeerChanged(PeerVm? value) => OnPropertyChanged(nameof(CanDownload));
    [ObservableProperty] private string? _url;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _volume = 0.8;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _generation;
    /// <summary>The next (re)open keeps quiet about resuming (switching between the original and the lighter copy).</summary>
    public bool QuietStart { get; set; }

    public PlayerViewModel(MainViewModel main) { _main = main; }

    // ---- lighter copy ----------------------------------------------------------------------
    // After half a minute of watching the original, the owner is asked for a lighter copy; the banner follows it
    // ("Preparing… 35%"), and once it is ready the viewer can switch (or is switched when playback keeps stalling).

    /// <summary>none | queued | preparing | ready | off</summary>
    [ObservableProperty] private string _lightState = "none";
    [ObservableProperty] private int _lightPercent;
    [ObservableProperty] private int _lightQueue;
    [ObservableProperty] private bool _usingLight;
    [ObservableProperty] private string _lightBanner = "";
    [ObservableProperty] private bool _lightBannerVisible;
    /// <summary>A short message the view shows on screen.</summary>
    [ObservableProperty] private string? _notice;
    private string? _originalUrl, _lightUrl;
    private int _watchedTicks, _stalls;
    private bool _lightRequested, _lightDismissed;
    private CancellationTokenSource? _lightCts;
    /// <summary>Remembered for the session: the user chose the original over the lighter copy.</summary>
    private static bool _preferOriginal;
    private const int TicksBeforeRequest = 120; // ticks come every 250 ms while playing: 30 s of watching

    public bool CanSwitchToLight => LightState == LightVariant.Ready && !UsingLight && Peer is not null && _lightUrl is not null;
    public bool CanToggleVariant => LightState == LightVariant.Ready && Peer is not null && _lightUrl is not null;
    public string VariantButtonText => UsingLight ? "Watch original" : "Watch lighter version";
    partial void OnLightStateChanged(string value) => LightChanged();
    partial void OnUsingLightChanged(bool value) => LightChanged();
    partial void OnLightPercentChanged(int value) => UpdateBanner();
    partial void OnLightQueueChanged(int value) => UpdateBanner();

    private void LightChanged()
    {
        OnPropertyChanged(nameof(CanSwitchToLight)); OnPropertyChanged(nameof(CanToggleVariant)); OnPropertyChanged(nameof(VariantButtonText));
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        var text = _lightDismissed ? "" : LightState switch
        {
            "queued" => $"Waiting to prepare a lighter version ({Place(LightQueue)} in line)… You're watching the original meanwhile.",
            LightVariant.Preparing => $"Preparing a lighter version… {LightPercent}%. You're watching the original meanwhile.",
            LightVariant.Ready when !UsingLight => "A lighter version is ready.",
            _ => "",
        };
        LightBanner = text;
        LightBannerVisible = text.Length > 0;
    }

    private static string Place(int n) => n switch { <= 1 => "next", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    private void BeginLight(ItemVm item, PeerVm peer, string originalUrl)
    {
        StopLight();
        _originalUrl = originalUrl;
        _lightUrl = _main.Api.StreamUrl(peer.Id, item.Id + LightVariant.Suffix, Path.ChangeExtension(item.Name, ".mp4"));
        _watchedTicks = 0; _stalls = 0; _lightRequested = false; _lightDismissed = false;
        var ready = item.Dto.Light == LightVariant.Ready && item.Dto.LightSize > 0;
        UsingLight = ready && !_preferOriginal;
        LightPercent = 0; LightQueue = 0;
        LightState = ready ? LightVariant.Ready : item.Dto.Light == LightVariant.Preparing ? LightVariant.Preparing : "none";
        if (LightState == LightVariant.Preparing) StartLightPolling(); // someone else asked already: follow it
    }

    private void EndLight()
    {
        StopLight();
        _originalUrl = _lightUrl = null;
        UsingLight = false; LightState = "none"; LightPercent = 0; LightQueue = 0;
    }

    private void StopLight() { _lightCts?.Cancel(); _lightCts = null; }

    /// <summary>Every 250 ms while playing: after 30 s on the original, ask the owner for a lighter copy.</summary>
    private void TickLight()
    {
        if (UsingLight || _lightRequested || Peer is null || Item is null) return;
        if (LightState is LightVariant.Ready or "off") return;
        if (++_watchedTicks < TicksBeforeRequest) return;
        _lightRequested = true;
        StartLightPolling(request: true);
    }

    private void StartLightPolling(bool request = false)
    {
        StopLight();
        var peer = Peer; var item = Item;
        if (peer is null || item is null) return;
        var cts = _lightCts = new CancellationTokenSource();
        var path = $"/api/peers/{peer.Id}/light/{Uri.EscapeDataString(item.Id)}";
        _ = Task.Run(async () =>
        {
            var ask = request;
            while (!cts.IsCancellationRequested)
            {
                LightStatusDto? st = null;
                try { st = ask ? await _main.Api.PostAsync<LightStatusDto>(path, null, cts.Token) : await _main.Api.GetAsync<LightStatusDto>(path, cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Services.AppLog.Warn("Player", "lighter copy: status", ex); }
                ask = false;
                if (cts.IsCancellationRequested) return;
                var state = st?.State ?? "none";
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    LightPercent = st?.Percent ?? 0; LightQueue = st?.Queue ?? 0;
                    LightState = state;
                    if (state == LightVariant.Ready) Services.AppLog.Info("Player", $"lighter copy ready: {item.Name} from {peer.Label}");
                });
                if (state is not ("queued" or LightVariant.Preparing)) return;
                try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    [RelayCommand]
    private void SwitchToLight()
    {
        if (!CanSwitchToLight) return;
        _preferOriginal = false;
        Switch(_lightUrl!, light: true);
        Notice = "Switched to the lighter version";
    }

    [RelayCommand]
    private void ToggleVariant()
    {
        if (!CanToggleVariant) return;
        if (!UsingLight) { SwitchToLight(); return; }
        _preferOriginal = true; _lightDismissed = true;
        Switch(_originalUrl!, light: false);
        Notice = "Original quality";
    }

    [RelayCommand]
    private void DismissLightBanner() { _lightDismissed = true; UpdateBanner(); }

    private void Switch(string url, bool light)
    {
        StartAt = PositionSeconds;
        QuietStart = true;
        UsingLight = light;
        Url = url;
        Generation++; // the view reopens the video and resumes at StartAt
    }

    /// <summary>The view saw playback stall mid-video; after a couple of stalls the lighter copy takes over.</summary>
    public void NoteStall(double positionSeconds)
    {
        if (positionSeconds < 10 || UsingLight || _lightDismissed) return;
        if (++_stalls >= 2 && CanSwitchToLight) SwitchToLight();
    }

    // ---- A-B repeat --------------------------------------------------------------------------
    // Click once: A = here. Again: B = here, the segment A-B loops. Again: off.

    public const double MinLoop = 0.5;

    [ObservableProperty] private double? _loopA;
    [ObservableProperty] private double? _loopB;

    public bool LoopActive => LoopA is not null && LoopB is not null;
    public bool LoopArmed => LoopA is not null && LoopB is null;
    public string LoopText => LoopA is not { } a ? "A-B" : LoopB is not { } b ? $"A {T(a)}  ·  set B" : $"{T(a)} to {T(b)}";

    partial void OnLoopAChanged(double? value) => NotifyLoop();
    partial void OnLoopBChanged(double? value) => NotifyLoop();
    private void NotifyLoop()
    {
        OnPropertyChanged(nameof(LoopActive)); OnPropertyChanged(nameof(LoopArmed)); OnPropertyChanged(nameof(LoopText));
    }

    private static string T(double s) => Core.Format.Duration(TimeSpan.FromSeconds(s));

    /// <summary>Returns a short message describing the new state (shown over the video).</summary>
    public string CycleLoop(double position)
    {
        if (LoopA is null) return SetLoopA(position);
        if (LoopB is null) return SetLoopB(position);
        ClearLoop();
        return "Repeat off";
    }

    public string SetLoopA(double position)
    {
        LoopA = Math.Max(0, position);
        if (LoopB is { } b && b - LoopA < MinLoop) LoopB = null;
        return LoopB is null ? $"A  {T(LoopA.Value)}" : $"Repeat {T(LoopA.Value)} to {T(LoopB!.Value)}";
    }

    public string SetLoopB(double position)
    {
        var a = LoopA ?? 0;
        var b = position;
        if (b < a) (a, b) = (b, a);                       // pressed B before A: swap
        // A loop shorter than half a second is never what the user wants (e.g. pressed twice while paused):
        // keep A and ask them to move on first.
        if (b - a < MinLoop) return LoopA is null ? "Set A first" : $"A  {T(LoopA.Value)}  ·  play on, then set B";
        LoopA = a; LoopB = b;
        return $"Repeat {T(a)} to {T(b)}";
    }

    /// <summary>The user jumped somewhere: leaving the A-B range ends the repeat (instead of snapping back).</summary>
    public bool EndLoopIfOutside(double position)
    {
        if (LoopA is not { } a || LoopB is not { } b) return false;
        if (position >= a - 0.25 && position < b) return false;
        ClearLoop();
        return true;
    }

    public void ClearLoop() { LoopA = null; LoopB = null; }

    // ---- playback speed --------------------------------------------------------------------
    public static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };

    /// <summary>Kept across videos for the rest of the session.</summary>
    [ObservableProperty] private double _speed = 1.0;

    public string SpeedText => FormatSpeed(Speed);
    partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(SpeedText));

    public static string FormatSpeed(double s) => s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    [RelayCommand]
    private void SetSpeed(string value)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            Speed = Math.Clamp(s, Speeds[0], Speeds[^1]);
    }

    public void StepSpeed(int direction)
    {
        var i = Array.FindIndex(Speeds, s => Math.Abs(s - Speed) < 0.001);
        if (i < 0) i = Array.IndexOf(Speeds, 1.0);
        Speed = Speeds[Math.Clamp(i + direction, 0, Speeds.Length - 1)];
    }

    public string TimeText => $"{Core.Format.Duration(TimeSpan.FromSeconds(PositionSeconds))} / {Core.Format.Duration(TimeSpan.FromSeconds(DurationSeconds))}";
    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(TimeText));
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(TimeText));

    /// <summary>Where the next video starts (seconds), applied once it has opened; 0 = from the beginning.</summary>
    public double StartAt { get; set; }

    // The video being watched, kept for the resume point even while Item is being cleared on close.
    private ItemVm? _playing;
    private PeerVm? _playingPeer;
    private DateTime _lastSaved;

    /// <summary>
    /// Records the resume point for the home page. <paramref name="force"/> writes it to disk now (stop, close,
    /// end); otherwise at most every few seconds.
    /// </summary>
    public void Remember(double position, bool finished = false, bool force = false)
    {
        TickLight();
        if (_playing is null || _playingPeer is null) return;
        var persist = force || DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(5);
        if (persist) _lastSaved = DateTime.UtcNow;
        _main.RememberPlayback(_playing, _playingPeer, position, DurationSeconds, finished, persist);
    }

    public void Open(ItemVm item, string url, PeerVm peer, double startAt = 0)
    {
        Item = item; Peer = peer; Error = null;
        BeginLight(item, peer, url);
        Url = UsingLight ? _lightUrl : url;
        _playing = item; _playingPeer = peer; StartAt = Math.Max(0, startAt);
        Services.AppLog.Info("Player", $"play {item.Name} from {peer.Label}" + (StartAt > 0 ? $" at {StartAt:0} s" : ""));
        Title = item.Name;
        Subtitle = $"{peer.Label}  ·  {item.ShareName}{(item.Folder.Length > 0 ? "  ›  " + item.Folder : "")}{(item.ResolutionText.Length > 0 ? "  ·  " + item.ResolutionText : "")}";
        PositionSeconds = 0;
        DurationSeconds = item.DurationMs / 1000.0;
        ClearLoop(); // A-B points belong to one video
        IsOpen = true;
        Generation++;
    }

    /// <summary>Plays one of my own shared videos straight from disk.</summary>
    public void OpenLocal(LocalItemVm item, string shareName)
    {
        EndLight();
        Item = null; Peer = null; Url = item.Dto.FullPath; Error = null;
        _playing = null; _playingPeer = null; StartAt = 0; // no resume point: it is not another node's video
        Services.AppLog.Info("Player", $"play my own {item.Name}");
        Title = item.Name;
        Subtitle = $"My shares  ·  {shareName}{(item.Folder.Length > 0 ? "  ›  " + item.Folder : "")}";
        PositionSeconds = 0;
        DurationSeconds = (item.Dto.DurationMs ?? 0) / 1000.0;
        ClearLoop();
        IsOpen = true;
        Generation++;
    }

    [RelayCommand]
    private void Close()
    {
        EndLight();
        IsOpen = false; IsFullscreen = false; IsPlaying = false;
        Url = null; Item = null;
    }

    [RelayCommand] private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;
    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void OpenExternal()
    {
        if (Url is null) return;
        OpenExternally(Url, _main);
        Close();
    }

    [RelayCommand]
    private async Task Download()
    {
        if (Item is null || Peer is null) return;
        try
        {
            await _main.Api.PostAsync("/api/downloads", new Core.Protocol.DownloadRequestDto { PeerId = Peer.Id, ItemIds = new List<string> { Item.Id } });
            _main.Toast("Download started", Item.Name, "success", () => _main.ShowDownloadsCommand.Execute(null));
        }
        catch (Exception ex) { _main.Error("Download failed to start", ex); }
    }

    /// <summary>
    /// Opens a video in a player outside Ocean. A file on this PC goes to its default app. A stream (an http address)
    /// can't: Windows picks the program for an address by its scheme, i.e. the browser, and Store apps such as
    /// Windows 11's Media Player only accept files. So a stream goes to the first of: the default player for the
    /// video's type when that is a desktop program, VLC / MPC-HC / PotPlayer / mpv, the classic Windows Media Player
    /// (part of Windows), and only then the browser.
    /// </summary>
    public static void OpenExternally(string url, MainViewModel main)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); // my own file: its default app
                return;
            }
            var player = DefaultDesktopPlayer(ExtensionOf(url)) ?? KnownPlayer() ?? ClassicMediaPlayer();
            if (player is not null)
            {
                Services.AppLog.Info("Player", $"external player: {player}");
                Process.Start(new ProcessStartInfo(player, $"\"{url}\"") { UseShellExecute = false });
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            main.Toast("Opened in your browser", "No video player on this PC can play a network stream. VLC (free) plays almost anything.", "info");
        }
        catch (Exception ex) { main.Toast("Could not open player", ex.Message, "error"); }
    }

    private static string ExtensionOf(string url)
    {
        try { var ext = Path.GetExtension(Uri.UnescapeDataString(new Uri(url).AbsolutePath)); return ext.Length > 1 ? ext : ".mp4"; }
        catch { return ".mp4"; }
    }

    /// <summary>The default program for the video's type, when it is a desktop program (not a Store app, not a browser).</summary>
    private static string? DefaultDesktopPlayer(string extension)
    {
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            if (AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, extension, null, sb, ref len) != 0) return null; // e.g. a Store app
            var exe = sb.ToString();
            if (!File.Exists(exe)) return null;
            var name = Path.GetFileName(exe).ToLowerInvariant();
            string[] notPlayers = { "rundll32.exe", "explorer.exe", "openwith.exe", "applicationframehost.exe", "msedge.exe", "chrome.exe", "firefox.exe", "opera.exe", "brave.exe", "iexplore.exe" };
            return notPlayers.Contains(name) ? null : exe;
        }
        catch { return null; }
    }

    /// <summary>A well-known player that plays network streams, installed in its usual place.</summary>
    private static string? KnownPlayer()
    {
        string[] candidates =
        {
            @"VideoLAN\VLC\vlc.exe", @"MPC-HC\mpc-hc64.exe", @"MPC-HC\mpc-hc.exe", @"K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
            @"MPC-BE x64\mpc-be64.exe", @"DAUM\PotPlayer\PotPlayerMini64.exe", @"Kakao\PotPlayer\PotPlayerMini64.exe", @"PotPlayer\PotPlayerMini64.exe",
        };
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var c in candidates)
            {
                var p = Path.Combine(root, c);
                if (File.Exists(p)) return p;
            }
        }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            if (k?.GetValue("") is string exe && File.Exists(exe)) return exe;
        }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "mpv.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>The classic Windows Media Player, part of Windows 10 and 11; it plays network streams.</summary>
    private static string? ClassicMediaPlayer()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Windows Media Player", "wmplayer.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, System.Text.StringBuilder outStr, ref uint outLen);
}
