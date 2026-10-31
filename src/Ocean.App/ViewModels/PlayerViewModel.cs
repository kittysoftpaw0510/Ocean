using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ocean.App.ViewModels;

/// <summary>State of the in-app player overlay. Playback itself is driven by the PlayerView (MediaElement).</summary>
public partial class PlayerViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private ItemVm? _item;
    [ObservableProperty] private PeerVm? _peer;
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

    public PlayerViewModel(MainViewModel main) { _main = main; }

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
        if (_playing is null || _playingPeer is null) return;
        var persist = force || DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(5);
        if (persist) _lastSaved = DateTime.UtcNow;
        _main.RememberPlayback(_playing, _playingPeer, position, DurationSeconds, finished, persist);
    }

    public void Open(ItemVm item, string url, PeerVm peer, double startAt = 0)
    {
        Item = item; Peer = peer; Url = url; Error = null;
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

    [RelayCommand]
    private void Close()
    {
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

    /// <summary>Opens the stream in VLC when installed (best format coverage), otherwise with the system default handler.</summary>
    public static void OpenExternally(string url, MainViewModel main)
    {
        try
        {
            var vlc = FindVlc();
            if (vlc is not null) Process.Start(new ProcessStartInfo(vlc, $"\"{url}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { main.Toast("Could not open player", ex.Message, "error"); }
    }

    private static string? FindVlc()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe");
            if (File.Exists(p)) return p;
        }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            if (k?.GetValue("") is string exe && File.Exists(exe)) return exe;
        }
        catch { }
        return null;
    }
}
