using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Ocean";
    private readonly MainViewModel _main;

    [ObservableProperty] private string _nodeName = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _secret = "";
    [ObservableProperty] private bool _showSecret;
    [ObservableProperty] private string _downloadDir = "";
    [ObservableProperty] private int _uploadLimitKBps;
    [ObservableProperty] private int _newDays = 3;
    [ObservableProperty] private int _maxConcurrentDownloads = 2;
    [ObservableProperty] private int _offlineKeepHours = 24;
    [ObservableProperty] private bool _notifyNewVideos = true;
    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string? _savedMessage;
    [ObservableProperty] private string _nodeIdShort = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _dataDir = "";
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private int _peerPort;
    private bool _loading;

    public SettingsViewModel(MainViewModel main) { _main = main; }

    public string UploadLimitText => UploadLimitKBps <= 0 ? "Unlimited" : UploadLimitKBps >= 1024 ? $"{UploadLimitKBps / 1024.0:0.#} MB/s" : $"{UploadLimitKBps} KB/s";
    partial void OnUploadLimitKBpsChanged(int value) { OnPropertyChanged(nameof(UploadLimitText)); MarkDirty(); }
    partial void OnNodeNameChanged(string value) => MarkDirty();
    partial void OnUserNameChanged(string value) => MarkDirty();
    partial void OnSecretChanged(string value) => MarkDirty();
    partial void OnDownloadDirChanged(string value) => MarkDirty();
    partial void OnNewDaysChanged(int value) => MarkDirty();
    partial void OnMaxConcurrentDownloadsChanged(int value) => MarkDirty();
    partial void OnOfflineKeepHoursChanged(int value) => MarkDirty();
    partial void OnNotifyNewVideosChanged(bool value) => MarkDirty();
    partial void OnRunAtStartupChanged(bool value) { if (!_loading) ApplyRunAtStartup(value); }
    private void MarkDirty() { if (!_loading) { Dirty = true; SavedMessage = null; } }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var s = await _main.Api.GetAsync<SettingsDto>("/api/settings");
            if (s is not null)
            {
                NodeName = s.NodeName; UserName = s.UserName; Secret = s.Secret ?? ""; DownloadDir = s.DownloadDir;
                UploadLimitKBps = s.UploadLimitKBps; NewDays = s.NewDays; MaxConcurrentDownloads = s.MaxConcurrentDownloads; NotifyNewVideos = s.NotifyNewVideos; OfflineKeepHours = s.OfflineKeepHours;
                _main.DownloadsVm.DownloadDir = s.DownloadDir;
            }
            var st = _main.Status;
            if (st is not null)
            {
                NodeIdShort = NodeIdentity.ShortId(st.NodeId);
                ModeText = st.RunningAsService ? "Windows service: starts with Windows and shares even when nobody is signed in" : "Background process started by this app (portable mode)";
                DataDir = st.DataDir; HostName = st.Host; PeerPort = st.PeerPort;
            }
            RunAtStartup = ReadRunAtStartup();
            Dirty = false;
        }
        catch (Exception ex) { _main.Toast("Could not load settings", ex.Message, "error"); }
        finally { _loading = false; }
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            await _main.Api.PutAsync("/api/settings", new SettingsDto
            {
                NodeName = NodeName.Trim(), UserName = UserName.Trim(), Secret = Secret, DownloadDir = DownloadDir.Trim(),
                UploadLimitKBps = UploadLimitKBps, NewDays = NewDays, MaxConcurrentDownloads = MaxConcurrentDownloads, OfflineKeepHours = OfflineKeepHours,
                NotifyNewVideos = NotifyNewVideos, SharingPaused = _main.SharingPaused,
            });
            Dirty = false;
            _main.Toast("Settings saved", "Other PCs see the changes within a few seconds.", "success");
            _main.DownloadsVm.DownloadDir = DownloadDir.Trim();
            await _main.ReloadStatusAsync();
        }
        catch (Exception ex) { _main.Dialogs.ShowError("Could not save settings", ex.Message); }
    }

    [RelayCommand]
    private Task Discard() => LoadAsync();

    [RelayCommand]
    private void BrowseDownloadDir()
    {
        var dir = _main.Dialogs.PickFolder("Where should downloads go?");
        if (dir is not null) DownloadDir = dir;
    }

    [RelayCommand] private void OpenDataDir() { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = true }); } catch { } }
    [RelayCommand] private void OpenLogs() { try { var p = Path.Combine(DataDir, "logs"); Directory.CreateDirectory(p); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true }); } catch { } }
    [RelayCommand] private void ClearSecret() => Secret = "";
    [RelayCommand] private void GenerateSecret() => Secret = Passwords.RandomToken(9);
    [RelayCommand] private void CopyNodeId() { try { System.Windows.Clipboard.SetText(_main.Status?.NodeId ?? ""); _main.Toast("Copied", "Node id copied to clipboard.", "success"); } catch { } }

    private static bool ReadRunAtStartup()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        catch { return false; }
    }

    private void ApplyRunAtStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return;
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue(RunName, false);
        }
        catch (Exception ex) { _main.Toast("Startup setting failed", ex.Message, "error"); }
    }
}
