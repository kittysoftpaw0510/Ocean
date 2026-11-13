using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core;

namespace Ocean.App.ViewModels;

/// <summary>One of the qualities, for the choice buttons.</summary>
public sealed class QualityOption
{
    public QualityOption(RecordingQuality.Spec spec) { Spec = spec; }
    public RecordingQuality.Spec Spec { get; }
    public string Key => Spec.Key;
    public string Name => Spec.Name;
    public string Hint => Spec.Hint;
}

/// <summary>
/// Screen recording in the app: the settings (remembered per Windows user: quality, speaker, microphone, folder),
/// starting (pick what to record), the running recording (time, size, mute the microphone, stop) and the saved file.
/// </summary>
public sealed partial class RecordViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly RecordingSettings _settings = RecordingSettings.Load();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ScreenRecorder? _rec;
    private bool _loading;

    public IScreenUi Ui { get; set; } = null!;
    public IReadOnlyList<QualityOption> Qualities { get; } = RecordingQuality.All.Select(k => new QualityOption(RecordingQuality.Of(k))).ToList();

    public RecordViewModel(MainViewModel main)
    {
        _main = main;
        _loading = true;
        Quality = _settings.Quality; Speaker = _settings.Speaker; Mic = _settings.Mic; Folder = _settings.FolderOrDefault; Countdown = _settings.Countdown;
        _loading = false;
        _tick.Tick += (_, _) => Refresh();
        RecordingEncoders.Warm(); // High's graphics card encoder is known before anyone records
    }

    // ------------------------------------------------------------------ settings (saved at once)

    [ObservableProperty] private string _quality = RecordingQuality.Balanced;
    [ObservableProperty] private bool _speaker = true;
    [ObservableProperty] private bool _mic = true;
    [ObservableProperty] private string _folder = "";
    /// <summary>3-2-1 over the area before recording starts.</summary>
    [ObservableProperty] private bool _countdown = true;
    partial void OnCountdownChanged(bool value) => Save();
    public string QualityHint => RecordingQuality.Of(Quality).Hint;
    public bool IsSmall { get => Quality == RecordingQuality.Small; set { if (value) Quality = RecordingQuality.Small; } }
    public bool IsBalanced { get => Quality == RecordingQuality.Balanced; set { if (value) Quality = RecordingQuality.Balanced; } }
    public bool IsHigh { get => Quality == RecordingQuality.High; set { if (value) Quality = RecordingQuality.High; } }
    public bool IsDefaultFolder => string.Equals(Folder.TrimEnd('\\'), RecordingSettings.DefaultFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    /// <summary>Is there a microphone at all (asked again whenever the picker opens)?</summary>
    [ObservableProperty] private bool _micAvailable = true;
    public string MicText => !MicAvailable ? "Microphone (none found)" : "Microphone";

    partial void OnQualityChanged(string value)
    {
        OnPropertyChanged(nameof(QualityHint)); OnPropertyChanged(nameof(IsSmall)); OnPropertyChanged(nameof(IsBalanced)); OnPropertyChanged(nameof(IsHigh));
        Save();
    }
    partial void OnSpeakerChanged(bool value) => Save();
    partial void OnMicChanged(bool value) => Save();
    partial void OnFolderChanged(string value) { OnPropertyChanged(nameof(IsDefaultFolder)); Save(); }
    partial void OnMicAvailableChanged(bool value) => OnPropertyChanged(nameof(MicText));

    private void Save()
    {
        if (_loading) return;
        _settings.Quality = Quality; _settings.Speaker = Speaker; _settings.Mic = Mic; _settings.Countdown = Countdown;
        _settings.Folder = IsDefaultFolder ? null : Folder;
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dir = _main.Dialogs.PickFolder("Where should screen recordings be saved?");
        if (dir is null) return;
        try { Directory.CreateDirectory(dir); Folder = dir; }
        catch (Exception ex) { _main.Toast("Can't use that folder", ex.Message, "warn"); }
    }

    [RelayCommand] private void DefaultFolder() => Folder = RecordingSettings.DefaultFolder;

    [RelayCommand]
    private void OpenFolder()
    {
        try { Directory.CreateDirectory(Folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _main.Toast("Can't open the folder", ex.Message, "warn"); }
    }

    /// <summary>Before the picker shows: is there a microphone now?</summary>
    public void CheckDevices() => MicAvailable = RecordingAudio.MicrophoneExists();

    // ------------------------------------------------------------------ the recording

    [ObservableProperty] private bool _isRecording;
    /// <summary>Stopped; the file is being finished (a moment).</summary>
    [ObservableProperty] private bool _saving;
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _micMuted;
    [ObservableProperty] private bool _hasMic;
    [ObservableProperty] private bool _hasSpeaker;
    /// <summary>Paused: nothing is captured; the time stands still.</summary>
    [ObservableProperty] private bool _paused;
    /// <summary>What is being recorded (for the boundary drawn around it).</summary>
    public ScreenPick? Target => _rec?.Pick;
    public string StatusText => Saving ? "Saving…" : Paused ? "PAUSED" : "REC";
    public string PauseTip => Paused ? "Go on recording (the recording continues in the same file)" : "Pause: nothing is recorded until you go on";
    partial void OnPausedChanged(bool value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(PauseTip)); }
    public string MicTip => MicMuted ? "The microphone is muted: click to record it again" : "Recording the microphone: click to mute it";
    public string SoundText => !HasSpeaker && !HasMic ? "no sound" : HasSpeaker && HasMic ? (MicMuted ? "speaker; mic muted" : "speaker + mic") : HasSpeaker ? "speaker" : MicMuted ? "mic muted" : "mic";
    partial void OnSavingChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnMicMutedChanged(bool value)
    {
        if (_rec is { } r) r.MicMuted = value;
        OnPropertyChanged(nameof(MicTip)); OnPropertyChanged(nameof(SoundText));
    }
    partial void OnHasMicChanged(bool value) => OnPropertyChanged(nameof(SoundText));
    partial void OnHasSpeakerChanged(bool value) => OnPropertyChanged(nameof(SoundText));

    [RelayCommand]
    private async Task Start()
    {
        if (IsRecording || _starting) return;
        if (ScreenTools.Ffmpeg is null) { _main.Toast("Recording isn't available", "ffmpeg.exe is missing next to Ocean. Installing Ocean again puts it back.", "error"); return; }
        CheckDevices();
        var pick = await Ui.PickToRecordAsync(this);
        if (pick is null) return;
        await BeginAsync(pick);
    }

    /// <summary>"Record the same again": what was recorded last, without the picker.</summary>
    [RelayCommand]
    private async Task RecordAgain()
    {
        if (IsRecording || _starting) return;
        if (_settings.Last?.Resolve() is not { } pick)
        {
            _main.Toast("Pick again", _settings.Last is null ? "Nothing was recorded yet." : $"“{_settings.Last.Label}” isn't there any more.", "info");
            await Start();
            return;
        }
        CheckDevices();
        await BeginAsync(pick);
    }

    /// <summary>What "Record the same again" records (for menus); null when there is nothing to repeat.</summary>
    public string? RecordAgainText => _settings.Last is { } l ? "Record the same again: " + l.Label : null;
    public bool CanRecordAgain => _settings.Last is not null;

    private bool _starting;

    private async Task BeginAsync(ScreenPick pick)
    {
        _starting = true;
        try
        {
            // little room on the disk: say so first, with how long it lasts
            if (DiskWarning(out var warning) && !Ui.Confirm("Little free space", warning, "Record anyway", danger: false, hideFromCapture: false)) return;
            if (Countdown && !await Ui.CountdownAsync(pick)) return;
            Begin(pick);
        }
        finally { _starting = false; }
    }

    /// <summary>Less than 2 GB free where recordings go: how long that is at the chosen quality.</summary>
    private bool DiskWarning(out string text)
    {
        text = "";
        try
        {
            Directory.CreateDirectory(Folder);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder))!);
            if (drive.AvailableFreeSpace >= 2L * 1024 * 1024 * 1024) return false;
            // what a minute of ordinary screen work takes at each quality (measured: a still screen takes far less)
            var perMinute = Quality switch { RecordingQuality.Small => 1.5, RecordingQuality.High => 8.0, _ => 4.0 } * 1024 * 1024;
            var minutes = Math.Max(0, (drive.AvailableFreeSpace - ScreenRecorder.MinFreeBytes) / perMinute);
            text = $"Only {Format.Bytes(drive.AvailableFreeSpace)} is free on {drive.Name.TrimEnd('\\')} where recordings are saved. " +
                   $"At {RecordingQuality.Of(Quality).Name} that is roughly {(minutes < 1 ? "less than a minute" : $"{minutes:0} minutes")} of recording; it stops by itself before the disk is full.";
            return true;
        }
        catch { return false; }
    }

    private void Begin(ScreenPick pick)
    {
        ScreenRecorder rec;
        try
        {
            _settings.Mic = Mic && MicAvailable;
            rec = new ScreenRecorder(pick, _settings);
            _settings.Mic = Mic;
            rec.Finished += r => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnFinished(rec, r));
            rec.Start();
        }
        catch (Exception ex) { _main.Toast("The recording didn't start", ex.Message, "error"); AppLog.Warn("Record", "start", ex); return; }
        _rec = rec;
        _settings.Last = LastTarget.From(pick);
        _settings.Save();
        OnPropertyChanged(nameof(RecordAgainText)); OnPropertyChanged(nameof(CanRecordAgain));
        _remindedHours = 0;
        MicMuted = false;
        HasMic = rec.HasMic; HasSpeaker = rec.HasSpeaker;
        IsRecording = true; Saving = false; Paused = false;
        OnPropertyChanged(nameof(Target));
        Refresh();
        _tick.Start();
        Ui.ShowRecordBar(this);
        if (rec.Problems.Count > 0)
            _main.Toast("Recording without some sound", char.ToUpper(rec.Problems[0][0]) + string.Join("; ", rec.Problems)[1..] + ".", "warn");
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_rec is not { } rec || Saving) return;
        if (!Paused)
        {
            rec.Pause();
            Paused = true;
            _tick.Stop();
            Refresh();
            return;
        }
        try
        {
            rec.Resume();
            Paused = false;
            _tick.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Record", "resume", ex);
            _main.Toast("Can't go on recording", ex.Message + " The recording so far is saved when you stop.", "warn");
        }
    }

    [RelayCommand]
    public void Stop()
    {
        if (_rec is null || Saving) return;
        Saving = true;
        _tick.Stop();
        Refresh();
        _rec.Stop();
    }

    [RelayCommand] private void ToggleMic() => MicMuted = !MicMuted;

    /// <summary>Throw this recording away (after asking; the question is never in the recording).</summary>
    [RelayCommand]
    private void Discard()
    {
        if (_rec is not { } rec || Saving) return;
        if (!Ui.Confirm("Discard this recording?", $"The {ElapsedText} recorded so far are deleted, nothing is saved.", "Discard", danger: true, hideFromCapture: true)) return;
        if (!ReferenceEquals(_rec, rec)) return;
        Saving = true;
        _tick.Stop();
        rec.Discard();
    }

    private void Refresh()
    {
        if (_rec is null) return;
        var t = _rec.Elapsed;
        ElapsedText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        SizeText = _rec.Bytes > 0 ? Format.Bytes(_rec.Bytes) : "";
        // a gentle reminder at every full hour (a recording that was simply forgotten fills the disk)
        if ((int)t.TotalHours > _remindedHours && !Paused)
        {
            _remindedHours = (int)t.TotalHours;
            var text = $"{ElapsedText} so far ({SizeText}). Stop it on the recording bar or from the tray icon.";
            _main.Toast("Still recording", text, "info");
            _main.Notify("Still recording: " + ElapsedText, SizeText, null);
        }
    }
    private int _remindedHours;

    private void OnFinished(ScreenRecorder rec, RecordingResult r)
    {
        if (!ReferenceEquals(_rec, rec)) return;
        _rec = null;
        _tick.Stop();
        IsRecording = false; Saving = false; Paused = false;
        Ui.HideRecordBar();
        if (r.Discarded) { _main.Toast("Recording discarded", "Nothing was saved.", "info"); return; }
        if (r.Path is null)
        {
            _main.Toast("Nothing was recorded", r.Error is null ? "The recording stopped before it began." : char.ToUpper(r.Error[0]) + r.Error[1..] + ".", "error");
            return;
        }
        // what to do next: open, share, trim, show in the folder, copy the path
        Ui.ShowSaved(r, "Recording saved");
    }

    /// <summary>Trim a recording (from the saved card); the trimmed copy gets its own card.</summary>
    public void Trim(string path, double duration) => Ui.OpenTrim(path, duration, (result, length) =>
    {
        if (result.Path is null) return;
        Ui.ShowSaved(new RecordingResult { Path = result.Path, Bytes = new FileInfo(result.Path).Length, Duration = TimeSpan.FromSeconds(length) }, "Trimmed copy saved");
    });

    /// <summary>The app is closing: a running recording is finished and saved first (a few seconds at most).</summary>
    public void Shutdown()
    {
        if (_rec is not { } rec) return;
        AppLog.Info("Record", "Ocean is closing: the recording is saved first");
        rec.Stop("Ocean was closed");
        rec.WaitFinished(TimeSpan.FromSeconds(25));
    }
}
