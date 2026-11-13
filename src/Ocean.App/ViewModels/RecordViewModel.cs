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
        Quality = _settings.Quality; Speaker = _settings.Speaker; Mic = _settings.Mic; Folder = _settings.FolderOrDefault;
        _loading = false;
        _tick.Tick += (_, _) => Refresh();
        RecordingEncoders.Warm(); // High's graphics card encoder is known before anyone records
    }

    // ------------------------------------------------------------------ settings (saved at once)

    [ObservableProperty] private string _quality = RecordingQuality.Balanced;
    [ObservableProperty] private bool _speaker = true;
    [ObservableProperty] private bool _mic = true;
    [ObservableProperty] private string _folder = "";
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
        _settings.Quality = Quality; _settings.Speaker = Speaker; _settings.Mic = Mic;
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
    public string StatusText => Saving ? "Saving…" : "REC";
    public string MicTip => MicMuted ? "The microphone is muted: click to record it again" : "Recording the microphone: click to mute it";
    public string SoundText => !HasSpeaker && !HasMic ? "no sound" : HasSpeaker && HasMic ? (MicMuted ? "speaker; mic muted" : "speaker + mic") : HasSpeaker ? "speaker" : MicMuted ? "mic muted" : "mic";
    partial void OnSavingChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnMicMutedChanged(bool value)
    {
        if (_rec?.Audio is { } a) a.MicMuted = value;
        OnPropertyChanged(nameof(MicTip)); OnPropertyChanged(nameof(SoundText));
    }
    partial void OnHasMicChanged(bool value) => OnPropertyChanged(nameof(SoundText));
    partial void OnHasSpeakerChanged(bool value) => OnPropertyChanged(nameof(SoundText));

    [RelayCommand]
    private async Task Start()
    {
        if (IsRecording) return;
        if (ScreenTools.Ffmpeg is null) { _main.Toast("Recording isn't available", "ffmpeg.exe is missing next to Ocean. Installing Ocean again puts it back.", "error"); return; }
        CheckDevices();
        var pick = await Ui.PickToRecordAsync(this);
        if (pick is null) return;
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
        MicMuted = false;
        HasMic = rec.Audio?.HasMic == true; HasSpeaker = rec.Audio?.HasSpeaker == true;
        IsRecording = true; Saving = false;
        Refresh();
        _tick.Start();
        Ui.ShowRecordBar(this);
        if (rec.Problems.Count > 0)
            _main.Toast("Recording without some sound", char.ToUpper(rec.Problems[0][0]) + string.Join("; ", rec.Problems)[1..] + ".", "warn");
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

    private void Refresh()
    {
        if (_rec is null) return;
        var t = _rec.Elapsed;
        ElapsedText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        SizeText = _rec.Bytes > 0 ? Format.Bytes(_rec.Bytes) : "";
    }

    private void OnFinished(ScreenRecorder rec, RecordingResult r)
    {
        if (!ReferenceEquals(_rec, rec)) return;
        _rec = null;
        _tick.Stop();
        IsRecording = false; Saving = false;
        Ui.HideRecordBar();
        if (r.Path is null)
        {
            _main.Toast("Nothing was recorded", r.Error is null ? "The recording stopped before it began." : char.ToUpper(r.Error[0]) + r.Error[1..] + ".", "error");
            return;
        }
        var path = r.Path;
        void show() { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } }
        var length = r.Duration.TotalHours >= 1 ? r.Duration.ToString(@"h\:mm\:ss") : r.Duration.ToString(@"m\:ss");
        var text = $"{Path.GetFileName(path)}  ·  {length}  ·  {Format.Bytes(r.Bytes)}. Click to show it in its folder.";
        if (r.Reason is { } why) _main.Toast("Recording stopped: " + why, text, "warn", show);
        else _main.Toast("Recording saved", text, "success", show);
        _main.Notify("Recording saved", Path.GetFileName(path), show);
    }

    /// <summary>The app is closing: a running recording is finished and saved first (a few seconds at most).</summary>
    public void Shutdown()
    {
        if (_rec is not { } rec) return;
        AppLog.Info("Record", "Ocean is closing: the recording is saved first");
        rec.Stop("Ocean was closed");
        rec.WaitFinished(TimeSpan.FromSeconds(25));
    }
}
