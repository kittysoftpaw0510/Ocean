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
    [ObservableProperty] private bool _allowPublicNetworks = true;
    [ObservableProperty] private string _networkMode = "auto";
    [ObservableProperty] private string _networkInfo = "";
    [ObservableProperty] private bool _firewallManaged = true;
    [ObservableProperty] private bool _allowBridging = true;
    [ObservableProperty] private int _streamCacheGB = 5;
    [ObservableProperty] private string _streamCacheUsedText = "";
    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string? _savedMessage;
    [ObservableProperty] private string _nodeIdShort = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _dataDir = "";
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private int _peerPort;
    [ObservableProperty] private string _versionText = "Ocean " + Core.OceanDefaults.AppVersion;
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
    partial void OnAllowPublicNetworksChanged(bool value) => MarkDirty();
    partial void OnAllowBridgingChanged(bool value) => MarkDirty();
    partial void OnNetworkModeChanged(string value) => MarkDirty();

    [RelayCommand]
    private async Task RestartService()
    {
        try
        {
            if (Dirty) await Save();
            await _main.Api.PostAsync("/api/service/restart");
            _main.Toast("Restarting the Ocean service", "It comes back within a few seconds.", "info");
        }
        catch (Exception ex) { _main.Error("Could not restart the service", ex); }
    }
    partial void OnStreamCacheGBChanged(int value) => MarkDirty();

    private static string CacheUsed(long bytes) => bytes <= 0 ? "Empty right now." : $"Using {Core.Format.Bytes(bytes)} right now.";

    [RelayCommand]
    private async Task ClearStreamCache()
    {
        try
        {
            await _main.Api.PostAsync("/api/cache/clear");
            StreamCacheUsedText = CacheUsed(0);
            _main.Toast("Video cache cleared", "Videos you watch next are fetched again.", "success");
        }
        catch (Exception ex) { _main.Error("Could not clear the cache", ex); }
    }
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
                AllowPublicNetworks = s.AllowPublicNetworks; FirewallManaged = s.FirewallManaged; AllowBridging = s.AllowBridging;
                NetworkMode = string.IsNullOrEmpty(s.NetworkMode) ? "auto" : s.NetworkMode.ToLowerInvariant();
                StreamCacheGB = s.StreamCacheGB; StreamCacheUsedText = CacheUsed(s.StreamCacheUsedBytes);
                _main.DownloadsVm.DownloadDir = s.DownloadDir;
            }
            var st = _main.Status;
            if (st is not null)
            {
                NodeIdShort = NodeIdentity.ShortId(st.NodeId);
                ModeText = st.RunningAsService ? "Windows service: starts with Windows and shares even when nobody is signed in" : "Background process started by this app (portable mode)";
                DataDir = st.DataDir; HostName = st.Host; PeerPort = st.PeerPort;
                NetworkInfo = st.NetworkInfo;
                // the service's version too when it differs (an update that didn't restart the service)
                var app = Core.OceanDefaults.AppVersion;
                VersionText = string.IsNullOrEmpty(st.Version) || st.Version == app ? $"Ocean {app}" : $"Ocean {app}  ·  service {st.Version}";
            }
            RunAtStartup = ReadRunAtStartup();
            Dirty = false;
        }
        catch (Exception ex) { _main.Error("Could not load settings", ex); }
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
                UploadLimitKBps = UploadLimitKBps, NewDays = NewDays, MaxConcurrentDownloads = MaxConcurrentDownloads, OfflineKeepHours = OfflineKeepHours, AllowPublicNetworks = AllowPublicNetworks, AllowBridging = AllowBridging, StreamCacheGB = StreamCacheGB, NetworkMode = NetworkMode,
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
        catch (Exception ex) { _main.Error("Startup setting failed", ex); }
    }
}
