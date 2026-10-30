using System.Diagnostics;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>Browsing one peer's catalog: share chips, filtering, sorting, view modes, unlocking, playing, downloading.</summary>
public partial class BrowseViewModel : ObservableObject
{
    private static ViewMode _lastViewMode = ViewMode.Grid;
    private static SortMode _lastSort = SortMode.Newest;

    private readonly MainViewModel _main;
    private readonly Dictionary<string, ItemVm> _byId = new();
    private List<ItemVm> _all = new();
    private CancellationTokenSource? _pollCts;

    public PeerVm Peer { get; }

    [ObservableProperty] private CatalogDto? _catalog;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private ShareChipVm? _selectedShare;
    [ObservableProperty] private ViewMode _viewMode = _lastViewMode;
    [ObservableProperty] private SortMode _sort = _lastSort;
    [ObservableProperty] private bool _newOnly;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private List<ItemVm> _items = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _headerText = "";
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string? _pendingText;

    public ObservableCollection<ShareChipVm> Shares { get; } = new();
    public ShareChipVm AllChip { get; } = new(null) { Name = "All", IsSelected = true };

    public BrowseViewModel(MainViewModel main, PeerVm peer)
    {
        _main = main; Peer = peer;
        Shares.Add(AllChip);
        SelectedShare = AllChip;
        Peer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PeerVm.Online) or nameof(PeerVm.OfflineSince) or "")
            {
                OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(OfflineBanner));
                if (Peer.Online) _ = LoadAsync(true); // back: fresh list
            }
        };
    }

    // ------------------------------------------------------------------ offline node (last known shares)

    public bool IsOffline => !Peer.Online;
    public string OfflineBanner => $"{Peer.Label} is offline{(Peer.OfflineSince is { } t ? $" (last seen {Format.Ago(t)})" : "")}. " +
                                   "This is its last known list: playing works again when it's back, and downloads you start now wait and continue automatically.";

    /// <summary>True (and explains why) when the node can't be reached right now.</summary>
    private bool BlockedOffline()
    {
        if (!IsOffline) return false;
        _main.Toast($"{Peer.Label} is offline", "Try again when it's back online. Queued downloads continue automatically.", "warn");
        return true;
    }

    public string SortLabel => Sort switch
    {
        SortMode.Newest => "Newest first",
        SortMode.Name => "Name",
        SortMode.Size => "Largest first",
        SortMode.Duration => "Longest first",
        SortMode.Folder => "Folder",
        _ => "",
    };

    partial void OnSortChanged(SortMode value) { _lastSort = value; OnPropertyChanged(nameof(SortLabel)); Rebuild(); }
    partial void OnViewModeChanged(ViewMode value) { _lastViewMode = value; }
    partial void OnNewOnlyChanged(bool value) => Rebuild();
    partial void OnFilterChanged(string value) => Rebuild();
    partial void OnSelectedShareChanged(ShareChipVm? oldValue, ShareChipVm? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (!ReferenceEquals(oldValue, newValue) && CurrentFolder.Length > 0) CurrentFolder = ""; // rebuilds
        else Rebuild();
    }

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync(bool refresh)
    {
        if (IsLoading) return;
        IsLoading = true; Error = null;
        try
        {
            var cat = await _main.Api.GetAsync<CatalogDto>($"/api/peers/{Peer.Id}/catalog?refresh={(refresh ? "true" : "false")}");
            if (cat is not null) ApplyCatalog(cat);
        }
        catch (ApiException ex) { Error = ex.Message; }
        catch (Exception ex) { Error = "Could not load this node: " + ex.Message; }
        finally { IsLoading = false; }
    }

    public void ApplyCatalog(CatalogDto cat)
    {
        Catalog = cat;
        var keepSelected = SelectedShare?.Id;
        var chips = new List<ShareChipVm>();
        var all = new List<ItemVm>();
        var seen = new HashSet<string>();
        foreach (var s in cat.Shares)
        {
            var chip = Shares.FirstOrDefault(c => c.Id == s.Id) ?? new ShareChipVm(s.Id);
            chip.Name = s.Name; chip.Count = s.ItemCount; chip.TotalBytes = s.TotalBytes; chip.Mode = s.Mode;
            chip.IsLocked = s.Lock is not null && !s.Unlocked;
            chip.NewCount = s.Items.Count(i => i.IsNew);
            chips.Add(chip);
            foreach (var i in s.Items)
            {
                seen.Add(i.Id);
                if (_byId.TryGetValue(i.Id, out var vm)) vm.Update(i);
                else { vm = new ItemVm(i, Peer.Id, s.Name, LoadThumbAsync); _byId[i.Id] = vm; }
                all.Add(vm);
            }
        }
        foreach (var gone in _byId.Keys.Where(k => !seen.Contains(k)).ToList()) _byId.Remove(gone);
        _all = all;

        // sync chip collection (All + shares) without flicker
        for (var i = Shares.Count - 1; i >= 1; i--) if (!chips.Contains(Shares[i])) Shares.RemoveAt(i);
        foreach (var c in chips) if (!Shares.Contains(c)) Shares.Add(c);
        for (var i = 0; i < chips.Count; i++) { var idx = Shares.IndexOf(chips[i]); if (idx != i + 1) Shares.Move(idx, i + 1); }

        AllChip.Count = all.Count;
        AllChip.NewCount = all.Count(i => i.IsNew);
        AllChip.TotalBytes = cat.Shares.Sum(s => s.TotalBytes);
        var sel = Shares.FirstOrDefault(c => c.Id == keepSelected) ?? AllChip;
        if (!ReferenceEquals(sel, SelectedShare)) SelectedShare = sel; else Rebuild();
    }

    private Task<BitmapImage?> LoadThumbAsync(ItemVm item) => _main.Thumbs.GetAsync(_main.Api.PeerThumbPath(Peer.Id, item.Id));

    // ------------------------------------------------------------------ folders

    /// <summary>Folder being shown inside the selected share ("" = its root).</summary>
    [ObservableProperty] private string _currentFolder = "";
    /// <summary>Folders + videos as displayed (folder mode) or just videos (flat mode).</summary>
    [ObservableProperty] private List<object> _entries = new();
    /// <summary>True while browsing a share's folders; false for "All", search and "New only" (flat lists).</summary>
    [ObservableProperty] private bool _folderMode;
    [ObservableProperty] private List<Crumb> _crumbs = new();
    [ObservableProperty] private string _downloadAllLabel = "Download all";

    partial void OnCurrentFolderChanged(string value) => Rebuild();

    private List<ItemVm> SortItems(IEnumerable<ItemVm> q) => (Sort switch
    {
        SortMode.Name => q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Size => q.OrderByDescending(i => i.Size),
        SortMode.Duration => q.OrderByDescending(i => i.DurationMs).ThenBy(i => i.Name),
        SortMode.Folder => q.OrderBy(i => i.ShareName).ThenBy(i => i.Folder, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        _ => q.OrderByDescending(i => i.AddedAt).ThenBy(i => i.Name),
    }).ToList();

    private List<FolderVm> SortFolders(IEnumerable<FolderVm> q) => (Sort switch
    {
        SortMode.Newest => q.OrderByDescending(f => f.Newest).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Size => q.OrderByDescending(f => f.TotalBytes),
        _ => q.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
    }).ToList();

    private void Rebuild()
    {
        var shareId = SelectedShare?.Id;
        var shareDto = shareId is null ? null : Catalog?.Shares.FirstOrDefault(s => s.Id == shareId);
        FolderMode = shareDto is not null && string.IsNullOrWhiteSpace(Filter) && !NewOnly;

        List<ItemVm> list;
        if (FolderMode)
        {
            var inShare = _all.Where(i => i.Dto.ShareId == shareId).ToList();
            var folders = shareDto!.Folders;
            if (CurrentFolder.Length > 0 && !folders.Contains(CurrentFolder, StringComparer.OrdinalIgnoreCase))
            {
                CurrentFolder = ""; // folder vanished (owner reorganized): back to the root; this re-enters Rebuild
                return;
            }
            var subFolders = folders
                .Where(f => string.Equals(Core.Naming.FolderPath.Parent(f), CurrentFolder, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FolderVm(f, inShare.Where(i => Core.Naming.FolderPath.IsUnder(i.Dto.Folder, f)).ToList()));
            list = SortItems(inShare.Where(i => string.Equals(i.Dto.Folder, CurrentFolder, StringComparison.OrdinalIgnoreCase)));
            var sortedFolders = SortFolders(subFolders);
            Entries = sortedFolders.Cast<object>().Concat(list).ToList();

            var crumbs = new List<Crumb> { new(shareDto.Name, "", CurrentFolder.Length == 0) };
            crumbs.AddRange(Core.Naming.FolderPath.SelfAndAncestors(CurrentFolder)
                .Select(p => new Crumb(Core.Naming.FolderPath.Name(p), p, string.Equals(p, CurrentFolder, StringComparison.OrdinalIgnoreCase))));
            Crumbs = crumbs;
            Summary = $"{(sortedFolders.Count > 0 ? $"{sortedFolders.Count} folder{(sortedFolders.Count == 1 ? "" : "s")}  ·  " : "")}{list.Count} video{(list.Count == 1 ? "" : "s")}";
        }
        else
        {
            IEnumerable<ItemVm> q = _all;
            if (shareId is not null) q = q.Where(i => i.Dto.ShareId == shareId);
            if (NewOnly) q = q.Where(i => i.IsNew);
            if (!string.IsNullOrWhiteSpace(Filter))
            {
                var f = Filter.Trim();
                q = q.Where(i => i.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                              || i.Folder.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                              || i.ShareName.Contains(f, StringComparison.CurrentCultureIgnoreCase));
            }
            list = SortItems(q);
            Entries = list.Cast<object>().ToList();
            Crumbs = new();
            Summary = list.Count == 0 ? "" : $"{list.Count} video{(list.Count == 1 ? "" : "s")}  ·  {Format.Bytes(list.Sum(i => i.Size))}";
        }

        Items = list;
        NewCount = _all.Count(i => i.IsNew);
        OnSelectionChanged();
        DownloadAllLabel = FolderMode && CurrentFolder.Length > 0 ? "Download folder" : "Download all";
        HeaderText = Summary.Length > 0 ? $"{Peer.Subtitle}  ·  {Summary}" : Peer.Subtitle;
        IsEmpty = Entries.Count == 0 && !IsLoading;
        EmptyText = !IsEmpty || SelectedShare is { IsLocked: true } ? "" :
            _all.Count == 0 && Catalog is { Shares.Count: 0 } ? "This node is not sharing anything yet." :
            NewOnly ? "No new videos since your last visit." :
            Filter.Length > 0 ? "No videos match your search." :
            FolderMode && CurrentFolder.Length > 0 ? "This folder is empty." :
            _all.Count == 0 ? "Nothing to show here." : "No videos in this share.";
    }

    public void OnSelectionChanged()
        => SelectedCount = Entries.Count(e => e is ItemVm { IsSelected: true } || e is FolderVm { IsSelected: true });

    [RelayCommand] private void OpenFolder(FolderVm? folder) { if (folder is not null) CurrentFolder = folder.Path; }

    [RelayCommand] private void GoToFolder(string? path) => CurrentFolder = path ?? "";

    [RelayCommand]
    private void Up() { if (CurrentFolder.Length > 0) CurrentFolder = Core.Naming.FolderPath.Parent(CurrentFolder); }

    [RelayCommand]
    private async Task DownloadFolder(FolderVm? folder)
    {
        if (folder is null || SelectedShare?.Id is not { } sid) return;
        if (!await _main.Dialogs.ConfirmAsync("Download folder?", $"Download “{folder.Name}” ({folder.Summary}) from {Peer.Label}? Its folder structure is kept.", "Download")) return;
        await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ShareId = sid, Folder = folder.Path }, folder.Name);
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand] private Task Refresh() => LoadAsync(true);

    [RelayCommand] private void SelectShare(ShareChipVm chip) => SelectedShare = chip;

    [RelayCommand] private void SetView(string mode) => ViewMode = Enum.Parse<ViewMode>(mode, true);

    [RelayCommand] private void SetSort(string mode) => Sort = Enum.Parse<SortMode>(mode, true);

    [RelayCommand] private void ToggleNewOnly() => NewOnly = !NewOnly;

    [RelayCommand]
    private async Task MarkSeen()
    {
        try
        {
            await _main.Api.PostAsync($"/api/peers/{Peer.Id}/visited");
            foreach (var i in _all) { i.Dto.IsNew = false; i.Update(i.Dto); }
            foreach (var c in Shares) c.NewCount = 0;
            Peer.NewCount = 0;
            Rebuild();
            _main.RecountNew();
        }
        catch (Exception ex) { _main.Toast("Could not mark as seen", ex.Message, "error"); }
    }

    [RelayCommand]
    private async Task Play(ItemVm? item)
    {
        if (BlockedOffline()) return;
        if (item is null) return;
        if (item.IsLocked) { if (!await UnlockItemAsync(item)) return; }
        _main.Player.Open(item, _main.Api.StreamUrl(Peer.Id, item.Id, item.Name), Peer);
    }

    [RelayCommand]
    private async Task OpenExternal(ItemVm? item)
    {
        if (BlockedOffline()) return;
        if (item is null) return;
        if (item.IsLocked) { if (!await UnlockItemAsync(item)) return; }
        var url = _main.Api.StreamUrl(Peer.Id, item.Id, item.Name);
        PlayerViewModel.OpenExternally(url, _main);
    }

    [RelayCommand]
    private void CopyLink(ItemVm? item)
    {
        if (item is null) return;
        try { System.Windows.Clipboard.SetText(_main.Api.StreamUrl(Peer.Id, item.Id, item.Name)); _main.Toast("Link copied", "Paste it into VLC or any player on this PC.", "success"); }
        catch { }
    }

    [RelayCommand]
    private async Task Download(ItemVm? item)
    {
        if (item is null) return;
        if (item.IsLocked) { if (!await UnlockItemAsync(item)) return; }
        await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ItemIds = new List<string> { item.Id } }, item.Name);
    }

    [RelayCommand]
    private async Task DownloadSelected()
    {
        // Selected videos plus everything inside selected folders (the folder structure is kept on disk).
        var ids = Items.Where(i => i.IsSelected && !i.IsLocked).Select(i => i.Id).ToHashSet();
        foreach (var f in Entries.OfType<FolderVm>().Where(f => f.IsSelected))
            foreach (var i in _all.Where(i => i.Dto.ShareId == SelectedShare?.Id && !i.IsLocked && Core.Naming.FolderPath.IsUnder(i.Dto.Folder, f.Path)))
                ids.Add(i.Id);
        if (ids.Count == 0) return;
        await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ItemIds = ids.ToList() }, $"{ids.Count} videos");
    }

    [RelayCommand]
    private async Task DownloadShare(ShareChipVm? chip)
    {
        chip ??= SelectedShare;
        if (chip is null) return;
        if (chip.Id is null)
        {
            var ids = _all.Where(i => !i.IsLocked).Select(i => i.Id).ToList();
            if (ids.Count == 0) return;
            if (!await _main.Dialogs.ConfirmAsync("Download everything?", $"Download all {ids.Count} videos ({Format.Bytes(_all.Where(i => !i.IsLocked).Sum(i => i.Size))}) from {Peer.Label}?", "Download")) return;
            await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ItemIds = ids }, $"{ids.Count} videos");
            return;
        }
        if (chip.IsLocked) { if (!await UnlockShareAsync(chip)) return; }
        if (FolderMode && ReferenceEquals(chip, SelectedShare) && CurrentFolder.Length > 0)
        {
            // Inside a folder, "Download folder" takes just this folder (with its sub-folders).
            var inside = _all.Where(i => i.Dto.ShareId == chip.Id && !i.IsLocked && Core.Naming.FolderPath.IsUnder(i.Dto.Folder, CurrentFolder)).ToList();
            if (inside.Count == 0) { _main.Toast("Nothing to download", "This folder has no videos yet.", "info"); return; }
            var name = Core.Naming.FolderPath.Name(CurrentFolder);
            if (!await _main.Dialogs.ConfirmAsync("Download folder?", $"Download “{name}” ({inside.Count} videos, {Format.Bytes(inside.Sum(i => i.Size))}) from {Peer.Label}? Its folder structure is kept.", "Download")) return;
            await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ShareId = chip.Id, Folder = CurrentFolder }, name);
            return;
        }
        if (!await _main.Dialogs.ConfirmAsync("Download share?", $"Download “{chip.Name}” ({chip.Count} videos, {chip.SizeText}) from {Peer.Label}?", "Download")) return;
        await EnqueueAsync(new DownloadRequestDto { PeerId = Peer.Id, ShareId = chip.Id }, chip.Name);
    }

    private async Task EnqueueAsync(DownloadRequestDto req, string label)
    {
        try
        {
            var jobs = await _main.Api.PostAsync<List<DownloadJobDto>>("/api/downloads", req);
            var n = jobs?.Count ?? 0;
            if (n > 0 && IsOffline)
                _main.Toast("Download queued", $"{label}: starts automatically when {Peer.Label} is back online.", "info", () => _main.ShowDownloadsCommand.Execute(null));
            else
                _main.Toast(n == 0 ? "Nothing new to download" : "Download started",
                    n == 0 ? $"{label}: already downloaded or in progress." : $"{label} → Downloads", n == 0 ? "info" : "success",
                    () => _main.ShowDownloadsCommand.Execute(null));
        }
        catch (Exception ex) { _main.Toast("Download failed to start", ex.Message, "error"); }
    }

    [RelayCommand]
    private async Task Unlock(ShareChipVm? chip)
    {
        if (chip is null || !chip.IsLocked) return;
        await UnlockShareAsync(chip);
    }

    public async Task<bool> UnlockShareAsync(ShareChipVm chip)
        => await UnlockAsync(chip.LockId, chip.Mode, chip.Name, chip);

    private async Task<bool> UnlockItemAsync(ItemVm item)
        => item.Dto.Lock is null || await UnlockAsync(item.Dto.Lock.Id, item.Dto.Lock.Mode, item.Name, null);

    private async Task<bool> UnlockAsync(string lockId, AccessMode mode, string label, ShareChipVm? chip)
    {
        if (BlockedOffline()) return false;
        string? password = null;
        if (mode == AccessMode.Password)
        {
            password = await _main.Dialogs.AskPasswordAsync($"Unlock “{label}”", $"{Peer.Label} protected this with a password.");
            if (password is null) return false;
        }
        try
        {
            var r = await _main.Api.PostAsync<AccessResponseDto>($"/api/peers/{Peer.Id}/unlock", new UnlockRequestDto { LockId = lockId, Password = password });
            if (r is null) return false;
            switch (r.Status)
            {
                case AccessStatus.Granted:
                    await LoadAsync(true);
                    _main.Toast("Unlocked", $"“{label}” is now available.", "success");
                    return true;
                case AccessStatus.WrongPassword:
                    _main.Toast("Wrong password", $"“{label}” stays locked.", "warn");
                    return false;
                case AccessStatus.Pending when r.RequestId is not null:
                    return await WaitForApprovalAsync(r.RequestId, label, chip);
                case AccessStatus.Denied:
                    _main.Toast("Access declined", r.Message ?? $"{Peer.Label} declined.", "warn");
                    return false;
                default:
                    _main.Toast("Could not unlock", r.Message ?? r.Status, "error");
                    return false;
            }
        }
        catch (Exception ex) { _main.Toast("Could not unlock", ex.Message, "error"); return false; }
    }

    private async Task<bool> WaitForApprovalAsync(string requestId, string label, ShareChipVm? chip)
    {
        _pollCts?.Cancel();
        var cts = _pollCts = new CancellationTokenSource();
        if (chip is not null) chip.IsPending = true;
        PendingText = $"Waiting for {Peer.Label} to approve access to “{label}”…";
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000, cts.Token);
                var r = await _main.Api.GetAsync<AccessResponseDto>($"/api/peers/{Peer.Id}/unlock/{requestId}", cts.Token);
                if (r is null) continue;
                if (r.Status == AccessStatus.Granted)
                {
                    await LoadAsync(true);
                    _main.Toast("Access granted", $"{Peer.Label} approved “{label}”.", "success");
                    return true;
                }
                if (r.Status is AccessStatus.Denied or AccessStatus.NotFound or AccessStatus.Error)
                {
                    _main.Toast("Access declined", r.Message ?? $"{Peer.Label} declined.", "warn");
                    return false;
                }
            }
            _main.Toast("No answer", $"{Peer.Label} did not respond. Try again later.", "warn");
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { _main.Toast("Could not unlock", ex.Message, "error"); return false; }
        finally
        {
            if (chip is not null) chip.IsPending = false;
            if (ReferenceEquals(_pollCts, cts)) { PendingText = null; _pollCts = null; }
        }
    }

    [RelayCommand] private void CancelPending() { _pollCts?.Cancel(); PendingText = null; }

    public void Deactivate() => _pollCts?.Cancel();
}
