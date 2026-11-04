using System.Collections.ObjectModel;
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
    /// <summary>"Continue watching": the last video played (null until something was played).</summary>
    [ObservableProperty] private ResumeVm? _resume;
    /// <summary>Multicasts running on the network right now.</summary>
    public ObservableCollection<LiveCastVm> Live { get; } = new();
    [ObservableProperty] private bool _anyLive;

    /// <summary>"Top on the network": the best-rated videos of everyone (mine included), optionally for one tag.</summary>
    public ObservableCollection<NetItemVm> Top { get; } = new();
    public ObservableCollection<TagChipVm> TopTags { get; } = new();
    [ObservableProperty] private bool _anyTop;
    [ObservableProperty] private string? _topTag;
    [ObservableProperty] private string _topEmptyText = "";

    /// <summary>A newer Ocean is downloaded and waits for "Install now" (hidden again with "Later").</summary>
    [ObservableProperty] private bool _updateBanner;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateHint = "";
    /// <summary>"Update now" (download, then install) or "Install now" (already downloaded).</summary>
    [ObservableProperty] private string _updateAction = "Update now";
    /// <summary>Downloading: the banner shows progress instead of its buttons.</summary>
    [ObservableProperty] private bool _updateBusy;

    public void ApplyTop(TopDto top, MainViewModel main)
    {
        // keep the row objects (and their thumbnails) for videos that stay in the list
        var byKey = Top.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var rows = top.Items.Select(d => byKey.TryGetValue(NetItemVm.KeyOf(d), out var vm) ? vm.Update(d) : new NetItemVm(d, main)).ToList();
        Top.Clear();
        foreach (var r in rows) { r.Rank = Top.Count + 1; Top.Add(r); }

        TopTags.Clear();
        foreach (var t in top.Tags)
            TopTags.Add(new TagChipVm(t.Tag, t.Count) { IsSelected = TopTag is not null && string.Equals(t.Tag, TopTag, StringComparison.CurrentCultureIgnoreCase) });
        if (TopTag is not null && !TopTags.Any(t => t.IsSelected)) TopTag = null;
        AnyTop = rows.Count > 0 || TopTags.Count > 0;
        TopEmptyText = rows.Count > 0 ? "" : TopTag is null ? "Nothing has been liked yet. Thumbs up a video you enjoyed and it shows up here."
                                                            : $"No liked videos tagged #{TopTag} yet.";
    }
}

/// <summary>Application shell: navigation, peers, live events from the service, toasts and access requests.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, BrowseViewModel> _browse = new();
    private int _peersRefreshPending;
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
    public SearchViewModel SearchVm { get; }
    /// <summary>What the service knows about newer versions (null until asked).</summary>
    [ObservableProperty] private UpdateStatusDto? _update;
    private string _updateDismissed = "";
    private string _updateAnnounced = "";
    private readonly System.ComponentModel.ICollectionView _peersView;
    /// <summary>The video search (top of the content area); typing opens the search page, clearing it goes back.</summary>
    [ObservableProperty] private string _videoQuery = "";
    private CancellationTokenSource? _queryCts;
    private (object Page, string Nav, PeerVm? Peer)? _beforeSearch;
    public PlayerViewModel Player { get; }
    public HomeViewModel Home { get; } = new();
    /// <summary>My multicast (null when none) and the one I'm watching (null when none).</summary>
    [ObservableProperty] private MyCastDto? _myCast;
    [ObservableProperty] private CastViewDto? _castView;

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
        SearchVm = new SearchViewModel(this);
        Player = new PlayerViewModel(this);
        _peersView = System.Windows.Data.CollectionViewSource.GetDefaultView(Peers);
        _peersView.Filter = o => o is PeerVm p && PeerMatches(p, SearchText);
        Page = Home;
        if (LastPlayedStore.Load() is { } last) ShowResumeCard(last);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync(NodeStatusDto status)
    {
        AppLog.Info("Main", "loading nodes, shares, downloads and settings");
        ApplyStatus(status);
        Connected = true;
        await ClaimUserNameAsync(status);
        await RefreshPeersAsync();
        await ResetCastAsync();
        _initialPeersLoaded = true;
        RefreshResumeCard();
        await SharesVm.LoadAsync();
        await DownloadsVm.LoadAsync();
        await SettingsVm.LoadAsync();
        await LoadPendingAsync();
        StartEventLoop();
        _ = LoadUpdateAsync();
        AppLog.Info("Main", $"ready: {Peers.Count} nodes known ({Peers.Count(p => p.Online)} online)");
        UpdateStatusText();
    }

    private void ApplyStatus(NodeStatusDto status)
    {
        Status = status;
        SharingPaused = status.SharingPaused;
        MyName = DisplayNames.Of(status.UserName, status.NodeName);
        MyHost = status.NodeName;
        // Sidebar subtitle: "Your name" from Settings (the PC's name is in the tooltip).
        IdentityText = MyName;
        RealmSet = status.RealmSet;
        Home.RealmSet = status.RealmSet;
        Home.MyName = MyName;
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
                AppLog.Info("Main", $"this PC now announces the Windows user name '{me}' instead of the PC name");
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
                catch (Exception ex)
                {
                    failures++;
                    AppLog.Warn("Events", $"live updates from the service interrupted (attempt {failures})", ex);
                    if (failures == 1) NoteServiceLoss();
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
                        await Application.Current.Dispatcher.InvokeAsync(() => ReconnectedAsync(r.Value.Api, r.Value.Status)).Task.Unwrap();
                }
            }
        });
    }

    // ------------------------------------------------------------------ losing the service for a moment

    private int _recovering;

    /// <summary>
    /// Errors from the app's own service. When the service merely can't be reached (it is restarting, being
    /// updated, or the PC just woke up), there is nothing for the user to do: show "Reconnecting…" in the status
    /// line instead of an error, and reload everything once it answers again.
    /// </summary>
    public void Error(string title, Exception ex)
    {
        if (ApiClient.IsServiceUnreachable(ex)) { AppLog.Warn("Error", $"{title}: the service is not reachable", ex); ConnectionLost(); return; }
        AppLog.Warn("Error", title, ex);
        Toast(title, ex.Message, "error");
    }

    // ---- old-style network add-ons that crash the service (see WinsockCatalog)

    public static IReadOnlyList<WinsockCatalog.Provider> NetworkAddOns => NetworkCompat.AddOns;
    private bool _addOnsExplained;
    private readonly Queue<DateTime> _recentLosses = new();

    public static string NetworkAddOnAdvice()
    {
        var addOns = NetworkAddOns.Count == 0 ? "" :
            "This PC has a network add-on of the kind that breaks programs:\n\n" +
            string.Join("\n", NetworkAddOns.Select(p => "  •  " + p.Name + (p.Path is null ? "" : "\n      " + p.Path))) + "\n\n" +
            "Some VPN, proxy, \"network accelerator\" and security programs install such add-ons. Ocean runs in its " +
            "compatibility mode because of it (Settings › Networking), which normally survives them.\n\n";
        return "Ocean's background service keeps stopping. " + addOns +
               "Please send the logs (Settings › Open logs: service-*.log and app-*.log) to whoever supports Ocean for you." +
               (NetworkAddOns.Count == 0 ? "" :
                "\n\nIf you'd rather remove the add-on: uninstall the program that installed it, or open Command Prompt as " +
                "administrator, run\n    netsh winsock reset\nand restart Windows (that program may need to be reinstalled afterwards).");
    }

    /// <summary>
    /// Counts the service going away (a request failing or the live updates dropping; the two together within 10 s
    /// are one loss). When it keeps dying (3 times within 3 minutes) say so, once, with what may cause it: otherwise
    /// the only visible symptom is "Reconnecting..." again and again.
    /// </summary>
    private void NoteServiceLoss()
    {
        bool tell;
        lock (_recentLosses)
        {
            var now = DateTime.UtcNow;
            if (_recentLosses.Count > 0 && now - _lastLoss < TimeSpan.FromSeconds(10)) return;
            _lastLoss = now;
            _recentLosses.Enqueue(now);
            while (_recentLosses.Count > 0 && now - _recentLosses.Peek() > TimeSpan.FromMinutes(3)) _recentLosses.Dequeue();
            tell = !_addOnsExplained && _recentLosses.Count >= 3;
            if (tell) _addOnsExplained = true;
        }
        if (!tell) return;
        AppLog.Warn("Main", $"service keeps stopping; telling the user (network add-ons found: {NetworkAddOns.Count})");
        Application.Current.Dispatcher.InvokeAsync(() => Dialogs.ShowError("Ocean's service keeps stopping", NetworkAddOnAdvice()));
    }
    private DateTime _lastLoss;

    public void ConnectionLost()
    {
        Connected = false;
        UpdateStatusText();
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        AppLog.Warn("Main", "lost the connection to the service; reconnecting");
        NoteServiceLoss();
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    await Task.Delay(Math.Min(10000, 1000 * attempt));
                    var r = await ServiceConnector.TryConnectAsync(CancellationToken.None);
                    if (r is null) continue;
                    await Application.Current.Dispatcher.InvokeAsync(() => ReconnectedAsync(r.Value.Api, r.Value.Status)).Task.Unwrap();
                    return;
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        });
    }

    /// <summary>The service answers again: refresh everything that may have been missed meanwhile.</summary>
    private async Task ReconnectedAsync(ApiClient api, NodeStatusDto status)
    {
        AppLog.Info("Main", $"reconnected to the service (Ocean {status.Version}, node {status.NodeName})");
        Api = api; Thumbs = new ThumbCache(Api);
        ApplyStatus(status); Connected = true; UpdateStatusText();
        await ClaimUserNameAsync(status); // may have been missed if the service was away when the app started
        await RefreshPeersAsync();
        await ResetCastAsync();
        RefreshResumeCard();
        await SharesVm.LoadAsync();
        await DownloadsVm.LoadAsync();
        await LoadPendingAsync();
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
                    if (change is not ("updated" or "versionchanged")) AppLog.Info("Peers", $"{change}: {name} ({(id.Length > 12 ? id[..12] : id)})");
                    // A node this app already shows as online isn't news: after the service restarts it reports every
                    // node again as "added", which would otherwise toast the whole network.
                    var shownOnline = Peers.FirstOrDefault(x => x.Id == id)?.Online == true;
                    if (change == "added" && _initialPeersLoaded && !shownOnline) Toast("Node online", $"{name} joined your network.", "info", () => SelectPeerById(id));
                    if (change == "online" && _initialPeersLoaded && !shownOnline) Toast("Back online", $"{name} is reachable again.", "success", () => SelectPeerById(id));
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
                        _browse.Remove(id); // a page for it would keep showing the old entry (offline) if it comes back
                        if (p is not null)
                        {
                            Peers.Remove(p);
                            if (SelectedPeer == p) ShowHome();
                        }
                        Home.PeerCount = Peers.Count(x => x.Online);
                        RefreshResumeCard();
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
                    ScheduleTopRefresh();
                    if (Page is SearchViewModel) SearchVm.ScheduleRefresh();
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
                    ScheduleTopRefresh();
                    if (Page is SearchViewModel) SearchVm.ScheduleRefresh();
                    break;
                case "update":
                    ApplyUpdate(Json.Deserialize<UpdateStatusDto>(data));
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
                case "viewers":
                {
                    using var doc = JsonDocument.Parse(data);
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (doc.RootElement.TryGetProperty("counts", out var c))
                        foreach (var prop in c.EnumerateObject()) counts[prop.Name] = prop.Value.GetInt32();
                    SharesVm.ApplyViewers(counts);
                    break;
                }
                case "myCast":
                {
                    var st = Json.Deserialize<MyCastDto>(data);
                    MyCast = st?.Active == true ? st : null;
                    SharesVm.ApplyMyCast(MyCast);
                    if (MyCast is not null) Player.HostUpdate(MyCast); else Player.EndCastIf(PlayerViewModel.CastRoles.Host);
                    break;
                }
                case "castView":
                {
                    var v = Json.Deserialize<CastViewDto>(data);
                    CastView = v?.Active == true ? v : null;
                    if (v is not null) Player.FollowUpdate(v);
                    break;
                }
                case "peerCast":
                {
                    using var doc = JsonDocument.Parse(data);
                    var id = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    var cast = doc.RootElement.TryGetProperty("cast", out var ce) && ce.ValueKind == JsonValueKind.Object ? Json.Deserialize<CastInfoDto>(ce.GetRawText()) : null;
                    var p = Peers.FirstOrDefault(x => x.Id == id);
                    if (p is not null) p.Cast = cast;
                    RefreshLive();
                    if (cast is not null && p is not null && _initialPeersLoaded)
                    {
                        AppLog.Info("Cast", $"{p.Label} started multicasting {cast.ItemName}");
                        Toast("Multicast started", $"{p.Label} is multicasting “{cast.ItemName}”. Click to join and watch together.", "info", () => _ = JoinCastAsync(p, cast));
                        TrayNotify?.Invoke("Multicast started", $"{p.Label}: {cast.ItemName}", () => _ = JoinCastAsync(p, cast));
                    }
                    break;
                }
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
        // Events can come several times a second on a big network: one refresh covers all of them, 300 ms after the
        // first (a timer restarted on every event would never fire while they keep coming).
        if (Interlocked.CompareExchange(ref _peersRefreshPending, 1, 0) != 0) return;
        _ = Task.Delay(300).ContinueWith(_ => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref _peersRefreshPending, 0);
            return RefreshPeersAsync();
        }), TaskScheduler.Default);
    }

    [ObservableProperty] private bool _isRefreshingNetwork;

    /// <summary>"Node info": who it is, its Ocean and Windows versions, how this PC reaches it, what it shares.</summary>
    [RelayCommand]
    private async Task ShowPeerInfo(PeerVm? peer)
    {
        if (peer is null) return;
        try
        {
            var info = await Api.GetAsync<PeerInfoDto>($"/api/peers/{peer.Id}/info");
            if (info is null) return;
            Dialogs.ShowPeerInfo(new PeerInfoViewModel(info));
        }
        catch (ApiException ex) when (ex.StatusCode == 404) { Toast("Not in the list anymore", $"{peer.Label} was removed from the list.", "info"); }
        catch (Exception ex) { Error("Could not show the node's details", ex); }
    }

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
        catch (Exception ex) { Error("Could not remove", ex); }
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
            for (var i = Peers.Count - 1; i >= 0; i--)
                if (!ids.Contains(Peers[i].Id)) { _browse.Remove(Peers[i].Id); Peers.RemoveAt(i); }
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
            RefreshResumeCard();
            RecountNew();
            UpdateStatusText();
            RefreshLive();
            ScheduleTopRefresh();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    // ------------------------------------------------------------------ updates

    /// <summary>At the app's start: what the service knows, and a look for a new version (silent without a connection).</summary>
    public async Task LoadUpdateAsync()
    {
        try { ApplyUpdate(await Api.GetAsync<UpdateStatusDto>("/api/update")); } catch { }
        try { await Api.PostAsync("/api/update/startup"); } catch (Exception ex) { AppLog.Warn("Update", "startup check", ex); } // the result comes as an event
    }

    /// <summary>The user clicked "Update now": once the download is complete, the installer starts by itself.</summary>
    private bool _installWhenReady;

    private void ApplyUpdate(UpdateStatusDto? u)
    {
        if (u is null) return;
        Update = u;
        var ready = u.Ready && u.Available && u.InstallerPath is not null;
        var available = u.Available && !ready;
        Home.UpdateBanner = (ready || available) && u.Latest != _updateDismissed;
        Home.UpdateBusy = u.Downloading;
        Home.UpdateAction = ready ? "Install now" : "Update now";
        Home.UpdateTitle = ready ? $"Ocean {u.Latest} is ready to install"
            : u.Downloading ? $"Downloading Ocean {u.Latest}…  {u.Progress:P0}"
            : available ? $"Ocean {u.Latest} is available" : "";
        Home.UpdateHint = ready ? $"You have {u.Current}. Installing takes about a minute; Ocean closes and opens again by itself."
            : u.Downloading ? (_installWhenReady ? "It installs as soon as the download is complete." : "You can keep using Ocean meanwhile.")
            : available ? $"You have {u.Current}. Update now downloads it{(u.InstallerSize > 0 ? $" ({Format.Bytes(u.InstallerSize)})" : "")} and installs it; Ocean closes and opens again by itself."
                          + (u.Error.Length > 0 ? "  " + u.Error : "")
            : "";
        SettingsVm.RefreshUpdateText();

        // Tell once per version: as soon as it is out, or in auto-update mode once it is downloaded.
        var tell = ready || (available && !u.Downloading && u.Mode != UpdateModes.AutoUpdate);
        if (tell && u.Latest != _updateAnnounced && u.Latest != _updateDismissed)
        {
            _updateAnnounced = u.Latest;
            var (title, text) = ready
                ? ("Update ready", $"Ocean {u.Latest} is downloaded. Install it from the home page or Settings.")
                : ("Update available", $"Ocean {u.Latest} is out (you have {u.Current}). Click Update now on the home page.");
            if (_initialPeersLoaded) Toast(title, text, "info", ShowHome);
            TrayNotify?.Invoke(title, text, ShowHome); // shown only while the window is closed to the tray
        }

        if (_installWhenReady && ready) { _installWhenReady = false; _ = InstallUpdateAsync(confirm: false); }
        else if (_installWhenReady && !u.Downloading && u.Error.StartsWith("Download failed", StringComparison.Ordinal))
        {
            _installWhenReady = false;
            Toast("Update not downloaded", u.Error + " Try again from the home page.", "warn");
        }
    }

    /// <summary>The banner's button: installs what is downloaded, or downloads it first and then installs.</summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (Update is { Ready: true }) { await InstallUpdateAsync(confirm: true); return; }
        try
        {
            _installWhenReady = true;
            AppLog.Info("Update", $"update now: downloading {Update?.Latest}");
            ApplyUpdate(await Api.PostAsync<UpdateStatusDto>("/api/update/download", null));
        }
        catch (Exception ex) { _installWhenReady = false; Error("Could not download the update", ex); }
    }

    /// <summary>Runs the downloaded installer (silent, Windows asks for permission); it closes Ocean, updates and reopens it.</summary>
    [RelayCommand]
    public Task InstallUpdate() => InstallUpdateAsync(confirm: true);

    private async Task InstallUpdateAsync(bool confirm)
    {
        if (Update is not { Ready: true, InstallerPath: { Length: > 0 } path, Latest: var version }) return;
        if (!File.Exists(path)) { Toast("Installer missing", "The downloaded file is gone; checking again.", "warn"); _ = CheckUpdatesAsync(); return; }
        var casting = MyCast is { Active: true };
        // "Update now" already said yes; still ask when it would stop a multicast others are watching
        if (confirm || casting)
        {
            var ok = await Dialogs.ConfirmAsync($"Install Ocean {version}?",
                "Ocean closes, the new version installs in the background (Windows asks for permission first) and Ocean opens again in about a minute. " +
                "Downloads continue afterwards." + (casting ? " Your multicast stops." : ""), "Install now");
            if (!ok) return;
        }
        try
        {
            AppLog.Info("Update", $"installing {version} from {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/SILENT /NORESTART /SUPPRESSMSGBOXES") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update", "installer did not start", ex);
            Toast("Update not installed", "Windows didn't let the installer run. You can run it yourself: " + path, "warn");
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        _updateDismissed = Update?.Latest ?? "";
        Home.UpdateBanner = false;
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = Update?.Url is { Length: > 0 } u ? u : "https://github.com/kittysoftpaw0510/Ocean/releases";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>"Check for updates" in Settings: asks the service to look now.</summary>
    public async Task CheckUpdatesAsync()
    {
        try { ApplyUpdate(await Api.PostAsync<UpdateStatusDto>("/api/update/check", null)); }
        catch (Exception ex) { Error("Could not check for updates", ex); }
    }

    // ------------------------------------------------------------------ votes, tags, top list

    /// <summary>
    /// Thumbs up/down on another node's video (vote: 1, -1, 0 = take it back; null = unchanged) and/or "watched it to
    /// the end". The service keeps my vote, sends it to the owner and brings back the new totals.
    /// </summary>
    private readonly Dictionary<string, DateTime> _voteNoticeShown = new(StringComparer.Ordinal);

    /// <summary>Why a vote didn't reach the owner yet (or at all); the same note for the same node at most every 10 minutes.</summary>
    private void VoteNotice(PeerVm peer, string? reason)
    {
        if (reason is null) return;
        var key = peer.Id + "|" + reason;
        if (_voteNoticeShown.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) return;
        _voteNoticeShown[key] = DateTime.UtcNow;
        var (title, text, kind) = reason switch
        {
            "offline" => ("Vote saved", $"{peer.Label} is offline right now. Your vote is delivered when it's back.", "info"),
            "unreachable" => ("Vote saved", $"{peer.Label} can't be reached right now. Your vote is delivered as soon as it can be.", "info"),
            "old" => ("Vote saved", $"{peer.Label} runs an older Ocean without votes. Your vote is kept and delivered once {peer.Label} updates.", "info"),
            "locked" => ("Vote saved", $"You don't have access to this video right now (the approval expired or was withdrawn). Your vote is sent once {peer.Label} lets you in again.", "warn"),
            "gone" => ("Vote not counted", $"{peer.Label} doesn't share this video anymore.", "warn"),
            _ => ("Vote not counted", $"{peer.Label} refused the vote.", "warn"),
        };
        Toast(title, text, kind);
    }

    public async Task<bool> RateAsync(PeerVm peer, ItemVm item, int? vote, bool finished)
    {
        try
        {
            var r = await Api.PostAsync<MyRatingDto>($"/api/peers/{peer.Id}/rate/{item.Id}", new RateDto { Vote = vote, Finished = finished });
            if (r is null) return false;
            item.ApplyRating(r);
            if (_browse.TryGetValue(peer.Id, out var b)) b.ApplyRating(item.Id, r); // the same video on the node's page
            SearchVm.ApplyRating(item.Id, r);
            if (Player.Item is { } playing && playing.Id == item.Id && !ReferenceEquals(playing, item)) playing.ApplyRating(r);
            if (vote is not null) VoteNotice(peer, r.Reason);
            ScheduleTopRefresh();
            return true;
        }
        catch (Exception ex)
        {
            if (vote is not null) Error("Could not vote", ex);
            else AppLog.Warn("Votes", $"finished report for {item.Name} failed", ex);
            return false;
        }
    }

    private int _topRefreshPending;

    /// <summary>Reloads "Top on the network" a moment later (catalog events come in bursts).</summary>
    public void ScheduleTopRefresh()
    {
        if (Interlocked.CompareExchange(ref _topRefreshPending, 1, 0) != 0) return;
        _ = Task.Delay(1500).ContinueWith(_ => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref _topRefreshPending, 0);
            return RefreshTopAsync();
        }), TaskScheduler.Default);
    }

    public async Task RefreshTopAsync()
    {
        try
        {
            var query = "/api/top?limit=12" + (Home.TopTag is { } tag ? "&tag=" + Uri.EscapeDataString(tag) : "");
            var top = await Api.GetAsync<TopDto>(query);
            if (top is not null) Home.ApplyTop(top, this);
        }
        catch (Exception ex) { AppLog.Warn("Top", "refresh failed", ex); }
    }

    [RelayCommand]
    private Task SelectTopTag(TagChipVm? chip)
    {
        Home.TopTag = chip is null || chip.IsSelected ? null : chip.Tag;
        foreach (var t in Home.TopTags) t.IsSelected = Home.TopTag is not null && string.Equals(t.Tag, Home.TopTag, StringComparison.CurrentCultureIgnoreCase);
        return RefreshTopAsync();
    }

    /// <summary>Play a row of the top list: my own video from disk, someone else's like from their page.</summary>
    [RelayCommand]
    private async Task PlayNet(NetItemVm? top)
    {
        if (top is null) return;
        if (top.Dto.Mine)
        {
            if (top.Dto.FullPath is not { Length: > 0 } path) return;
            var local = Json.Deserialize<LocalItemDto>(Json.Serialize(top.Dto.Item)) ?? new LocalItemDto();
            local.FullPath = path;
            Player.OpenLocal(new LocalItemVm(local, i => Thumbs.GetAsync(Api.LocalThumbPath(i.Id))), top.Dto.ShareName);
            return;
        }
        var peer = Peers.FirstOrDefault(p => p.Id == top.Dto.PeerId);
        if (peer is null) { Toast("Not available", $"{top.Dto.PeerName} isn't on the network right now.", "warn"); return; }
        await BrowseFor(peer).PlayCommand.ExecuteAsync(top.Item);
    }

    [RelayCommand] private Task VoteNetUp(NetItemVm? top) => VoteNetAsync(top, 1);
    [RelayCommand] private Task VoteNetDown(NetItemVm? top) => VoteNetAsync(top, -1);
    private Task VoteNetAsync(NetItemVm? top, int vote)
    {
        if (top is null || !top.CanVote) return Task.CompletedTask;
        var peer = Peers.FirstOrDefault(p => p.Id == top.Dto.PeerId);
        if (peer is null) { Toast("Not available", $"{top.Dto.PeerName} isn't on the network right now.", "warn"); return Task.CompletedTask; }
        return RateAsync(peer, top.Item, top.Item.MyVote == vote ? 0 : vote, finished: false);
    }

    /// <summary>Download a search result from its node (same queue and unlock flow as the node's page).</summary>
    [RelayCommand]
    private async Task DownloadNet(NetItemVm? row)
    {
        if (row is null || row.Dto.Mine) return;
        var peer = Peers.FirstOrDefault(p => p.Id == row.Dto.PeerId);
        if (peer is null) { Toast("Not available", $"{row.Dto.PeerName} isn't on the network right now.", "warn"); return; }
        await BrowseFor(peer).DownloadCommand.ExecuteAsync(row.Item);
    }

    /// <summary>Open the node's page from a row of the top list or the search.</summary>
    [RelayCommand]
    private void BrowseNet(NetItemVm? top)
    {
        if (top is null) return;
        if (top.Dto.Mine) { ShowShares(); return; }
        var peer = Peers.FirstOrDefault(p => p.Id == top.Dto.PeerId);
        if (peer is not null) SelectPeer(peer);
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

    // ------------------------------------------------------------------ multicast

    public void RefreshLive()
    {
        var live = Peers.Where(p => p.Online && p.Cast is not null).Select(p => new LiveCastVm(p, p.Cast!)).ToList();
        Home.Live.Clear();
        foreach (var l in live) Home.Live.Add(l);
        Home.AnyLive = live.Count > 0;
    }

    [RelayCommand] private Task JoinLive(LiveCastVm? live) => live is null ? Task.CompletedTask : JoinCastAsync(live.Peer, live.Cast);
    [RelayCommand] private Task JoinPeerCast(PeerVm? peer) => peer?.Cast is null ? Task.CompletedTask : JoinCastAsync(peer, peer.Cast);

    /// <summary>Join a node's multicast: same access rule as the video (password / approval), then follow the host.</summary>
    public async Task JoinCastAsync(PeerVm peer, CastInfoDto cast)
    {
        try
        {
            var cat = await Api.GetAsync<CatalogDto>($"/api/peers/{peer.Id}/catalog");
            var share = cat?.Shares.FirstOrDefault(s => s.Items.Any(i => i.Id == cast.ItemId));
            var dto = share?.Items.First(i => i.Id == cast.ItemId);
            if (share is null || dto is null) { Toast("Can't join", $"“{cast.ItemName}” isn't in {peer.Label}'s list anymore.", "warn"); return; }
            var item = new ItemVm(dto, peer.Id, share.Name, null);
            if (item.IsLocked && !await BrowseFor(peer).UnlockItemAsync(item)) return;
            var view = await Api.PostAsync<CastViewDto>($"/api/peers/{peer.Id}/cast/{cast.SessionId}/join", null);
            if (view is null || !view.Active) { Toast("Can't join", "That multicast has ended.", "warn"); return; }
            CastView = view;
            AppLog.Info("Cast", $"joined {peer.Label}'s multicast of {cast.ItemName}");
            var name = view.ItemId.EndsWith(LightVariant.Suffix, StringComparison.Ordinal) ? Path.ChangeExtension(item.Name, ".mp4") : item.Name;
            Player.Open(item, Api.StreamUrl(peer.Id, view.ItemId, name), peer, exactUrl: true);
            Player.BeginFollow(view);
        }
        catch (ApiException ex) { Toast("Can't join the multicast", ex.Message, "warn"); }
        catch (Exception ex) { Error("Can't join the multicast", ex); }
    }

    public async Task LeaveCastAsync(string why = "Leave")
    {
        AppLog.Info("Cast", $"leaving the multicast of {CastView?.ItemName ?? Player.Title}: {why}");
        try { await Api.PostAsync("/api/cast/leave"); } catch (Exception ex) { AppLog.Warn("Cast", "leave", ex); }
        CastView = null;
        Player.EndCast();
    }

    public async Task StopCastAsync()
    {
        try { await Api.PostAsync("/api/cast/stop"); } catch (Exception ex) { AppLog.Warn("Cast", "stop", ex); }
        MyCast = null;
        SharesVm.ApplyMyCast(null);
        Player.EndCast();
    }

    /// <summary>The player closed but the multicast goes on by itself (until the video ends, or Stop in My shares).</summary>
    public async Task DetachCastAsync(bool notify)
    {
        var name = MyCast?.ItemName ?? Player.Title;
        try { await Api.PostAsync("/api/cast/detach"); } catch (Exception ex) { AppLog.Warn("Cast", "detach", ex); }
        Player.EndCast();
        AppLog.Info("Cast", $"multicast of {name} goes on without the player");
        if (!notify) return;
        Toast("Multicast goes on", $"“{name}” keeps playing for everyone without your player. Take it back or stop it in My shares.", "info", ShowShares);
        TrayNotify?.Invoke("Multicast goes on", $"“{name}” keeps playing for everyone. Open Ocean to stop it.", ShowShares);
    }

    /// <summary>Open the player on the video I'm multicasting and drive it again from where it is now.</summary>
    public async Task ReopenCastAsync(LocalItemVm item, string shareName)
    {
        try
        {
            var st = await Api.GetAsync<MyCastDto>("/api/cast");
            if (st?.Active != true)
            {
                MyCast = null; SharesVm.ApplyMyCast(null);
                Player.OpenLocal(item, shareName); // it ended meanwhile: just watch it
                return;
            }
            MyCast = st; SharesVm.ApplyMyCast(st);
            Player.OpenLocal(item, shareName, startAt: st.Playing ? st.Position + 1 : st.Position, host: true);
            Player.BeginHost(st);
            AppLog.Info("Cast", $"player back on the multicast of {st.ItemName} at {st.Position:0} s");
        }
        catch (Exception ex) { Error("Can't open the player", ex); }
    }

    /// <summary>
    /// At start and after a reconnect: a multicast whose player is gone (the app restarted) runs on by itself until
    /// the video ends; a multicast I was watching is left (its player is gone too).
    /// </summary>
    private async Task ResetCastAsync()
    {
        try
        {
            var mine = await Api.GetAsync<MyCastDto>("/api/cast");
            if (mine?.Active == true && Player.CastRole != PlayerViewModel.CastRoles.Host)
            {
                if (!mine.Unattended) { await Api.PostAsync("/api/cast/detach"); mine.Unattended = true; }
                MyCast = mine; SharesVm.ApplyMyCast(mine);
            }
            var view = await Api.GetAsync<CastViewDto>("/api/cast/view");
            if (view?.Active == true && Player.CastRole != PlayerViewModel.CastRoles.Follower) await Api.PostAsync("/api/cast/leave");
            else if (view?.Active != true && Player.CastRole == PlayerViewModel.CastRoles.Follower)
            {
                // This PC's service came back (an update, a restart) while the player was following: it no longer
                // receives that multicast. Join it again when the host still sends it, otherwise say it is over.
                var peer = Peers.FirstOrDefault(p => p.Id == Player.FollowPeerId);
                Player.EndCast();
                if (peer is { Online: true, Cast: { } cast } && cast.SessionId == Player.FollowSessionId)
                {
                    AppLog.Info("Cast", $"joining {peer.Label}'s multicast of {cast.ItemName} again (the service was restarted)");
                    await JoinCastAsync(peer, cast);
                }
                else
                {
                    AppLog.Info("Cast", "the multicast being watched ended while Ocean was reconnecting");
                    Player.Notice = "The multicast ended while Ocean was reconnecting";
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Cast", "reset", ex); }
        RefreshLive();
    }

    private BrowseViewModel BrowseFor(PeerVm peer)
    {
        if (_browse.TryGetValue(peer.Id, out var vm) && ReferenceEquals(vm.Peer, peer)) return vm;
        vm = new BrowseViewModel(this, peer);
        _browse[peer.Id] = vm;
        return vm;
    }

    [RelayCommand]
    public void SelectPeer(PeerVm? peer)
    {
        if (peer is null) { ShowHome(); return; }
        if (Page is BrowseViewModel prev && prev.Peer != peer) prev.Deactivate();
        if (_browse.TryGetValue(peer.Id, out var vm) && !ReferenceEquals(vm.Peer, peer))
        {
            // the node left the list and came back as a new entry: the old page would keep showing it offline
            vm.Deactivate();
            _browse.Remove(peer.Id);
            vm = null;
        }
        if (vm is null)
        {
            vm = new BrowseViewModel(this, peer);
            _browse[peer.Id] = vm;
        }
        SelectedPeer = peer;
        Nav = "peer";
        Page = vm;
        _ = vm.LoadAsync(false);
    }

    partial void OnSelectedPeerChanged(PeerVm? value)
    {
        if (value is not null && (Page is not BrowseViewModel b || b.Peer != value)) SelectPeer(value);
    }

    partial void OnSearchTextChanged(string value) => _peersView.Refresh();

    /// <summary>The node list's filter box: name, user, PC name or address.</summary>
    private static bool PeerMatches(PeerVm p, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var f = text.Trim();
        return p.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) || p.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
            || p.User.Contains(f, StringComparison.CurrentCultureIgnoreCase) || p.Host.Contains(f, StringComparison.CurrentCultureIgnoreCase)
            || p.Address.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ video search

    partial void OnVideoQueryChanged(string value)
    {
        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(value))
        {
            _ = SearchVm.RunAsync("");
            if (Page is SearchViewModel) LeaveSearch();
            return;
        }
        if (Page is not SearchViewModel)
        {
            _beforeSearch = (Page ?? Home, Nav, SelectedPeer);
            if (Page is BrowseViewModel prev) prev.Deactivate();
            SelectedPeer = null; Nav = "search"; Page = SearchVm;
        }
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return Task.CompletedTask;
            return Application.Current.Dispatcher.InvokeAsync(() => SearchVm.RunAsync(value)).Task;
        }, TaskScheduler.Default);
    }

    /// <summary>Back to where the user was before typing (Esc or the clear button).</summary>
    private void LeaveSearch()
    {
        var back = _beforeSearch; _beforeSearch = null;
        if (back is null) { ShowHome(); return; }
        if (back.Value.Page is BrowseViewModel b && Peers.Contains(b.Peer)) SelectPeer(b.Peer);
        else if (back.Value.Page is SharesViewModel) ShowShares();
        else if (back.Value.Page is DownloadsViewModel) ShowDownloads();
        else if (back.Value.Page is SettingsViewModel) ShowSettings();
        else ShowHome();
    }

    [RelayCommand] private void ClearVideoQuery() => VideoQuery = "";

    [RelayCommand] public void ShowHome() { SelectedPeer = null; Nav = "home"; Page = Home; RefreshResumeCard(); }

    // ------------------------------------------------------------------ continue watching

    /// <summary>
    /// Called by the player while a video plays and when it stops. <paramref name="persist"/> writes it to disk
    /// (the player does that every few seconds and on stop, not on every tick).
    /// </summary>
    public void RememberPlayback(ItemVm item, PeerVm peer, double position, double duration, bool finished, bool persist)
    {
        var last = new LastPlayed
        {
            PeerId = peer.Id, PeerName = peer.Label, ItemId = item.Id, Name = item.Name, ShareName = item.ShareName,
            Folder = item.Dto.Folder ?? "", DurationSeconds = duration > 0 ? duration : item.DurationMs / 1000.0,
            // Stopped in the last few seconds: treat as watched, so the card offers "Play again" instead of the credits.
            Finished = finished || (duration > 30 && duration - position < 5),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        last.PositionSeconds = last.Finished ? 0 : Math.Max(0, position);
        ShowResumeCard(last);
        if (persist) LastPlayedStore.Save(last);
    }

    private void ShowResumeCard(LastPlayed last)
    {
        if (Home.Resume is { } card && card.Same(last)) card.Update(last);
        else Home.Resume = new ResumeVm(last);
        RefreshResumeCard();
    }

    /// <summary>Online state of the card's node, and its thumbnail once it can be fetched.</summary>
    private void RefreshResumeCard()
    {
        if (Home.Resume is not { } card) return;
        if (!_initialPeersLoaded) return; // the node list isn't loaded yet: don't call it offline
        var peer = Peers.FirstOrDefault(p => p.Id == card.Data.PeerId);
        card.Online = peer?.Online ?? false;
        if (card.Thumb is null && card.Online) _ = LoadResumeThumbAsync(card);
    }

    private async Task LoadResumeThumbAsync(ResumeVm card)
    {
        try
        {
            var img = await Thumbs.GetAsync(Api.PeerThumbPath(card.Data.PeerId, card.Data.ItemId));
            if (img is not null && Home.Resume == card) card.Thumb = img;
        }
        catch { }
    }

    [RelayCommand]
    private async Task ResumeLast()
    {
        if (Home.Resume is not { } card) return;
        var last = card.Data;
        var peer = Peers.FirstOrDefault(p => p.Id == last.PeerId);
        if (peer is null || !peer.Online)
        {
            Toast($"{last.PeerName} is offline right now", "You can continue as soon as it's back online.", "warn");
            return;
        }
        CatalogDto? cat;
        try { cat = await Api.GetAsync<CatalogDto>($"/api/peers/{peer.Id}/catalog"); }
        catch (Exception ex) { Toast("Couldn't connect to this PC", $"{peer.Label}: {ex.Message}", "error"); return; }

        var share = cat?.Shares.FirstOrDefault(s => s.Items.Any(i => i.Id == last.ItemId));
        var dto = share?.Items.First(i => i.Id == last.ItemId);
        if (share is null || dto is null)
        {
            Toast("This video isn't shared anymore", $"{last.Name} is no longer in {last.PeerName}'s shares.", "warn", () => SelectPeer(peer));
            return;
        }
        if (dto.Lock is not null && !dto.Unlocked)
        {
            // Unlocking (password / owner approval) lives on the node's page.
            SelectPeer(peer);
            Toast("Unlock to keep watching", $"{last.Name} is protected. Unlock it here, then press Play.", "info");
            return;
        }
        var item = new ItemVm(dto, peer.Id, share.Name, null);
        Player.Open(item, Api.StreamUrl(peer.Id, item.Id, item.Name), peer, card.CanResumeMidway ? last.PositionSeconds : 0);
    }

    [RelayCommand]
    private void ForgetLast()
    {
        Home.Resume = null;
        LastPlayedStore.Save(null);
    }
    [RelayCommand] public void ShowShares() { SelectedPeer = null; Nav = "shares"; Page = SharesVm; }
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
        catch (Exception ex) { Error("Could not change sharing", ex); }
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
        catch (Exception ex) { Error("Could not answer request", ex); }
        finally { req.Busy = false; }
    }

    [RelayCommand] private Task AllowOnce(PendingAccessVm req) => DecideAsync(req, "once");
    [RelayCommand] private Task AllowAlways(PendingAccessVm req) => DecideAsync(req, "always");
    [RelayCommand] private Task Deny(PendingAccessVm req) => DecideAsync(req, "deny");

    // ------------------------------------------------------------------ toasts

    public void Toast(string title, string message, string kind = "info", Action? onClick = null)
    {
        AppLog.Info("Toast", $"[{kind}] {title}: {message}");
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
