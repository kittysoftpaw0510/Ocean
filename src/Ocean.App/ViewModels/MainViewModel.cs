using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    [ObservableProperty] private int _peerCount;
    [ObservableProperty] private bool _realmSet;
    [ObservableProperty] private string _myName = "";
    [ObservableProperty] private int _shareCount;
    [ObservableProperty] private int _totalNew;
}

/// <summary>Application shell: navigation, peers, live events from the service, toasts and access requests.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, BrowseViewModel> _browse = new();
    private CancellationTokenSource? _peersDebounce;
    private bool _initialPeersLoaded;
    private CancellationTokenSource? _eventsCts;

    public ApiClient Api { get; private set; }
    public ThumbCache Thumbs { get; private set; }
    public IDialogs Dialogs { get; set; } = null!;

    public ObservableCollection<PeerVm> Peers { get; } = new();
    public ObservableCollection<ToastVm> Toasts { get; } = new();
    public ObservableCollection<PendingAccessVm> Pending { get; } = new();

    public SharesViewModel SharesVm { get; }
    public DownloadsViewModel DownloadsVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public PlayerViewModel Player { get; }
    public HomeViewModel Home { get; } = new();

    [ObservableProperty] private NodeStatusDto? _status;
    [ObservableProperty] private string _nav = "home";
    [ObservableProperty] private object? _page;
    [ObservableProperty] private PeerVm? _selectedPeer;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _sharingPaused;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private int _activeDownloads;
    [ObservableProperty] private int _totalNew;
    [ObservableProperty] private string _myName = "";
    [ObservableProperty] private string _myHost = "";
    [ObservableProperty] private string _identityText = "";
    [ObservableProperty] private bool _realmSet;

    public event Action<PendingAccessVm>? AccessRequested;
    public event Action<string, string, Action?>? TrayNotify;

    public MainViewModel(ApiClient api)
    {
        Api = api;
        Thumbs = new ThumbCache(api);
        SharesVm = new SharesViewModel(this);
        DownloadsVm = new DownloadsViewModel(this);
        SettingsVm = new SettingsViewModel(this);
        Player = new PlayerViewModel(this);
        Page = Home;
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync(NodeStatusDto status)
    {
        ApplyStatus(status);
        Connected = true;
        await ClaimUserNameAsync(status);
        await RefreshPeersAsync();
        _initialPeersLoaded = true;
        await SharesVm.LoadAsync();
        await DownloadsVm.LoadAsync();
        await SettingsVm.LoadAsync();
        await LoadPendingAsync();
        StartEventLoop();
        UpdateStatusText();
    }

    private void ApplyStatus(NodeStatusDto status)
    {
        Status = status;
        SharingPaused = status.SharingPaused;
        MyName = status.UserName;
        MyHost = status.NodeName;
        // Sidebar subtitle: "Riddle · DESKTOP-1" or just the PC name when both are the same.
        IdentityText = string.Equals(status.UserName, status.NodeName, StringComparison.OrdinalIgnoreCase)
            ? status.NodeName : $"{status.UserName}  ·  {status.NodeName}";
        RealmSet = status.RealmSet;
        Home.RealmSet = status.RealmSet;
        Home.MyName = status.UserName;
    }

    /// <summary>
    /// The Windows service runs as LocalSystem and cannot know who uses this PC, so it announces the PC name.
    /// The first time a person opens the app we put their Windows user name there instead (once, so a later
    /// rename in Settings sticks).
    /// </summary>
    private async Task ClaimUserNameAsync(NodeStatusDto status)
    {
        const string key = @"Software\Ocean";
        if (!status.RunningAsService) return; // a private background process already uses the user's name
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
            // Remember per node, so a reinstall (new node identity) or an older flag does not block it.
            if (k is null || Equals(k.GetValue("NameClaimed") as string, status.NodeId)) return;
            var me = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(me) &&
                string.Equals(status.UserName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(me, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                var s = await Api.GetAsync<SettingsDto>("/api/settings");
                if (s is null) return;
                s.UserName = me;
                await Api.PutAsync("/api/settings", s);
                await ReloadStatusAsync();
            }
            k.SetValue("NameClaimed", status.NodeId);
        }
        catch { /* cosmetic only */ }
    }

    public async Task ReloadStatusAsync()
    {
        try
        {
            var s = await Api.GetAsync<NodeStatusDto>("/api/status");
            if (s is not null) ApplyStatus(s);
            UpdateStatusText();
        }
        catch { }
    }

    private void StartEventLoop()
    {
        _eventsCts?.Cancel();
        var cts = _eventsCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var (type, data) in Api.EventsAsync(cts.Token))
                    {
                        failures = 0;
                        var t = type; var d = data;
                        await Application.Current.Dispatcher.InvokeAsync(() => HandleEvent(t, d));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    failures++;
                    await Application.Current.Dispatcher.InvokeAsync(() => { Connected = false; UpdateStatusText(); });
                    await Task.Delay(Math.Min(10000, 1000 * failures), cts.Token).ContinueWith(_ => { });
                    if (cts.IsCancellationRequested) break;
                    // The service may have restarted (or been started as a Windows service meanwhile): reconnect.
                    var r = await ServiceConnector.TryConnectAsync(cts.Token);
                    if (r is null && failures >= 3)
                    {
                        try { var c = await ServiceConnector.ConnectAsync(null, cts.Token); r = (c.Api, c.Status); } catch { }
                    }
                    if (r is not null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            Api = r.Value.Api; Thumbs = new ThumbCache(Api);
                            ApplyStatus(r.Value.Status); Connected = true; UpdateStatusText();
                            await RefreshPeersAsync(); await SharesVm.LoadAsync(); await DownloadsVm.LoadAsync();
                        });
                    }
                }
            }
        });
    }

    public void Shutdown() => _eventsCts?.Cancel();

    // ------------------------------------------------------------------ events

    private void HandleEvent(string type, string data)
    {
        try
        {
            switch (type)
            {
                case "peer":
                {
                    using var doc = JsonDocument.Parse(data);
                    var change = doc.RootElement.TryGetProperty("change", out var c) ? c.GetString() : "";
                    var name = doc.RootElement.TryGetProperty("user", out var u) && u.GetString() is { Length: > 0 } us ? us : doc.RootElement.GetProperty("name").GetString() ?? "";
                    var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                    if (change == "added" && _initialPeersLoaded) Toast("Node online", $"{name} joined your network.", "info", () => SelectPeerById(id));
                    if (change == "online" && _initialPeersLoaded) Toast("Back online", $"{name} is reachable again.", "success", () => SelectPeerById(id));
                    if (change == "offline")
                    {
                        // The node stays in the list (dimmed) with its last known shares; it is forgotten later.
                        var p = Peers.FirstOrDefault(x => x.Id == id);
                        if (p is not null) { p.Online = false; p.OfflineSince = DateTimeOffset.UtcNow; }
                        Toast("Node offline", $"{name} went offline. Its last known shares stay visible for now.", "warn", () => SelectPeerById(id));
                    }
                    if (change == "removed")
                    {
                        var p = Peers.FirstOrDefault(x => x.Id == id);
                        if (p is not null)
                        {
                            Peers.Remove(p);
                            if (SelectedPeer == p) ShowHome();
                        }
                        Home.PeerCount = Peers.Count(x => x.Online);
                        UpdateStatusText();
                    }
                    else SchedulePeersRefresh();
                    break;
                }
                case "peerCatalog":
                {
                    using var doc = JsonDocument.Parse(data);
                    var id = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    var p = Peers.FirstOrDefault(x => x.Id == id);
                    if (p is not null)
                    {
                        p.NewCount = doc.RootElement.GetProperty("newCount").GetInt32();
                        p.ItemCount = doc.RootElement.GetProperty("itemCount").GetInt32();
                        p.CatalogLoaded = true;
                        RecountNew();
                    }
                    if (_browse.TryGetValue(id, out var b)) _ = b.LoadAsync(false);
                    break;
                }
                case "newVideos":
                {
                    using var doc = JsonDocument.Parse(data);
                    var id = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    var peerName = doc.RootElement.GetProperty("peerName").GetString() ?? "";
                    var count = doc.RootElement.GetProperty("count").GetInt32();
                    var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(n => n.GetString() ?? "").ToList();
                    var p = Peers.FirstOrDefault(x => x.Id == id);
                    var who = p?.Label ?? peerName;
                    var text = count == 1 ? names.FirstOrDefault() ?? "" : $"{count} new videos" + (names.Count > 0 ? ": " + string.Join(", ", names) + (count > names.Count ? "…" : "") : "");
                    Toast($"New from {who}", text, "info", () => SelectPeerById(id));
                    TrayNotify?.Invoke($"New videos from {who}", text, () => SelectPeerById(id));
                    break;
                }
                case "access":
                {
                    var dto = Json.Deserialize<PendingAccessDto>(data);
                    if (dto is null) break;
                    if (Pending.Any(p => p.RequestId == dto.RequestId)) break;
                    var vm = new PendingAccessVm(dto);
                    Pending.Add(vm);
                    AccessRequested?.Invoke(vm);
                    TrayNotify?.Invoke("Access request", vm.Title, () => AccessRequested?.Invoke(vm));
                    break;
                }
                case "accessDecided":
                {
                    using var doc = JsonDocument.Parse(data);
                    var id = doc.RootElement.GetProperty("requestId").GetString();
                    var vm = Pending.FirstOrDefault(p => p.RequestId == id);
                    if (vm is not null) Pending.Remove(vm);
                    break;
                }
                case "download":
                {
                    var dto = Json.Deserialize<DownloadJobDto>(data);
                    if (dto is null) break;
                    var before = DownloadsVm.Jobs.FirstOrDefault(j => j.Id == dto.Id)?.Status;
                    DownloadsVm.Upsert(dto);
                    if (dto.Status == DownloadStatus.Done && before != DownloadStatus.Done)
                        Toast("Download complete", dto.Name, "success", () => ShowDownloads());
                    else if (dto.Status == DownloadStatus.Failed && before != DownloadStatus.Failed)
                        Toast("Download failed", $"{dto.Name}: {dto.Error}", "error", () => ShowDownloads());
                    break;
                }
                case "downloadRemoved":
                {
                    using var doc = JsonDocument.Parse(data);
                    DownloadsVm.RemoveJob(doc.RootElement.GetProperty("id").GetString() ?? "");
                    break;
                }
                case "catalog":
                    SharesVm.ScheduleReload();
                    break;
                case "grants":
                    _ = SharesVm.RefreshGrantsAsync();
                    break;
                case "shareScan":
                {
                    using var doc = JsonDocument.Parse(data);
                    SharesVm.SetScanning(doc.RootElement.GetProperty("shareId").GetString() ?? "", doc.RootElement.GetProperty("scanning").GetBoolean());
                    break;
                }
                case "settings":
                    _ = ReloadStatusAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"event {type}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ peers

    private void SchedulePeersRefresh()
    {
        _peersDebounce?.Cancel();
        var cts = _peersDebounce = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Application.Current.Dispatcher.InvokeAsync(() => RefreshPeersAsync());
        }, TaskScheduler.Default);
    }

    [ObservableProperty] private bool _isRefreshingNetwork;

    /// <summary>Removes an offline node (and its remembered shares) from the list. It reappears if it comes back online.</summary>
    [RelayCommand]
    public async Task RemovePeer(PeerVm? peer)
    {
        if (peer is null) return;
        if (peer.Online) { Toast("Still online", $"{peer.Label} can be removed once it goes offline.", "info"); return; }
        try
        {
            await Api.DeleteAsync($"/api/peers/{peer.Id}");
            Peers.Remove(peer);
            _browse.Remove(peer.Id);
            if (SelectedPeer == peer || (Page is BrowseViewModel b && b.Peer == peer)) ShowHome();
            Toast("Removed", $"{peer.Label} was removed from the list. It shows up again if it comes back online.", "success");
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { Peers.Remove(peer); }
        catch (Exception ex) { Toast("Could not remove", ex.Message, "error"); }
    }

    /// <summary>The Network ↻ button: announce ourselves now, re-check every online node, then reload the list.</summary>
    [RelayCommand]
    private async Task RefreshNetwork()
    {
        if (IsRefreshingNetwork) return;
        IsRefreshingNetwork = true;
        try
        {
            try { await Api.PostAsync("/api/peers/refresh"); } catch { /* older service: just reload the list */ }
            await Task.Delay(900); // give answers and catalog checks a moment
            await RefreshPeersAsync();
        }
        finally { IsRefreshingNetwork = false; }
    }

    [RelayCommand]
    public async Task RefreshPeersAsync()
    {
        try
        {
            var list = await Api.GetAsync<List<PeerDto>>("/api/peers") ?? new();
            var ids = list.Select(p => p.Id).ToHashSet();
            for (var i = Peers.Count - 1; i >= 0; i--) if (!ids.Contains(Peers[i].Id)) Peers.RemoveAt(i);
            foreach (var dto in list)
            {
                var vm = Peers.FirstOrDefault(p => p.Id == dto.Id);
                if (vm is null) Peers.Add(new PeerVm(dto)); else vm.Update(dto);
            }
            // keep sorted by label
            // online nodes first, then the ones that went offline recently
            var sorted = Peers.OrderByDescending(p => p.Online).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            for (var i = 0; i < sorted.Count; i++) { var idx = Peers.IndexOf(sorted[i]); if (idx != i) Peers.Move(idx, i); }
            Home.PeerCount = Peers.Count(p => p.Online);
            RecountNew();
            UpdateStatusText();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void RecountNew()
    {
        TotalNew = Peers.Sum(p => p.NewCount);
        Home.TotalNew = TotalNew;
    }

    private void UpdateStatusText()
    {
        if (!Connected) { StatusText = "Reconnecting to the Ocean service…"; return; }
        var n = Peers.Count(p => p.Online);
        StatusText = (n == 0 ? "No other nodes online" : n == 1 ? "1 node online" : $"{n} nodes online") + (SharingPaused ? "  ·  sharing paused" : "");
    }

    private void SelectPeerById(string id)
    {
        var p = Peers.FirstOrDefault(x => x.Id == id);
        if (p is not null) SelectPeer(p);
    }

    // ------------------------------------------------------------------ navigation

    [RelayCommand]
    public void SelectPeer(PeerVm? peer)
    {
        if (peer is null) { ShowHome(); return; }
        if (Page is BrowseViewModel prev && prev.Peer != peer) prev.Deactivate();
        if (!_browse.TryGetValue(peer.Id, out var vm))
        {
            vm = new BrowseViewModel(this, peer);
            _browse[peer.Id] = vm;
        }
        SelectedPeer = peer;
        Nav = "peer";
        vm.Filter = SearchText;
        Page = vm;
        _ = vm.LoadAsync(false);
    }

    partial void OnSelectedPeerChanged(PeerVm? value)
    {
        if (value is not null && (Page is not BrowseViewModel b || b.Peer != value)) SelectPeer(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (Page is BrowseViewModel b) b.Filter = value;
        else if (Page is SharesViewModel s) s.ItemFilter = value;
    }

    [RelayCommand] public void ShowHome() { SelectedPeer = null; Nav = "home"; Page = Home; }
    [RelayCommand] public void ShowShares() { SelectedPeer = null; Nav = "shares"; SharesVm.ItemFilter = SearchText; Page = SharesVm; }
    [RelayCommand] public void ShowDownloads() { SelectedPeer = null; Nav = "downloads"; Page = DownloadsVm; }
    [RelayCommand] public void ShowSettings() { SelectedPeer = null; Nav = "settings"; _ = SettingsVm.LoadAsync(); Page = SettingsVm; }
    [RelayCommand] private void ClearSearch() => SearchText = "";

    [RelayCommand]
    public async Task TogglePause()
    {
        try
        {
            await Api.PostAsync($"/api/sharing/pause?paused={(!SharingPaused).ToString().ToLowerInvariant()}");
            await ReloadStatusAsync();
            Toast(SharingPaused ? "Sharing paused" : "Sharing resumed", SharingPaused ? "Your shares are hidden until you resume." : "Your shares are visible again.", "info");
        }
        catch (Exception ex) { Toast("Could not change sharing", ex.Message, "error"); }
    }

    // ------------------------------------------------------------------ access requests

    private async Task LoadPendingAsync()
    {
        try
        {
            var list = await Api.GetAsync<List<PendingAccessDto>>("/api/access/pending") ?? new();
            foreach (var dto in list)
            {
                if (Pending.Any(p => p.RequestId == dto.RequestId)) continue;
                var vm = new PendingAccessVm(dto);
                Pending.Add(vm);
                AccessRequested?.Invoke(vm);
            }
        }
        catch { }
    }

    public async Task DecideAsync(PendingAccessVm req, string decision)
    {
        if (req.Busy) return;
        req.Busy = true;
        try
        {
            await Api.PostAsync($"/api/access/{req.RequestId}/decide", new DecisionDto { Decision = decision });
            Pending.Remove(req);
            Toast(decision == "deny" ? "Declined" : "Access granted", decision == "deny" ? $"{req.Who} was declined." : $"{req.Who} can now open {req.What}{(decision == "once" ? " for 8 hours" : "")}.", decision == "deny" ? "warn" : "success");
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { Pending.Remove(req); }
        catch (Exception ex) { Toast("Could not answer request", ex.Message, "error"); }
        finally { req.Busy = false; }
    }

    [RelayCommand] private Task AllowOnce(PendingAccessVm req) => DecideAsync(req, "once");
    [RelayCommand] private Task AllowAlways(PendingAccessVm req) => DecideAsync(req, "always");
    [RelayCommand] private Task Deny(PendingAccessVm req) => DecideAsync(req, "deny");

    // ------------------------------------------------------------------ toasts

    public void Toast(string title, string message, string kind = "info", Action? onClick = null)
    {
        void Add()
        {
            var t = new ToastVm { Title = title, Message = message, Kind = kind, OnClick = onClick };
            Toasts.Add(t);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            _ = Task.Delay(kind == "error" ? 9000 : 5500).ContinueWith(_ => Application.Current.Dispatcher.InvokeAsync(() => Toasts.Remove(t)));
        }
        if (Application.Current.Dispatcher.CheckAccess()) Add(); else Application.Current.Dispatcher.InvokeAsync(Add);
    }

    [RelayCommand] private void DismissToast(ToastVm? t) { if (t is not null) Toasts.Remove(t); }

    [RelayCommand] private void ToastClick(ToastVm? t) { if (t is null) return; Toasts.Remove(t); t.OnClick?.Invoke(); }
}
