using System.Diagnostics;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.Core;
using Ocean.Core.Media;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

public partial class MyShareVm : ObservableObject
{
    [ObservableProperty] private ShareDefinitionDto _dto;
    public MyShareVm(ShareDefinitionDto dto) { _dto = dto; }
    public string Id => Dto.Id;
    public string Name => Dto.Name;
    public AccessMode Mode => Dto.Mode;
    public string ModeText => Dto.Mode switch { AccessMode.Password => "Password", AccessMode.Ask => "Ask me", _ => "Public" };
    public string ModeGlyph => Dto.Mode switch { AccessMode.Password => "", AccessMode.Ask => "", _ => "" };
    public int ItemCount => Dto.ItemCount;
    public string SizeText => Format.Bytes(Dto.TotalBytes);
    public string RootsText => string.Join("\n", Dto.Roots);
    public string RootsShort => Dto.Roots.Count switch
    {
        0 => "No videos yet: use Add videos, or drop files here",
        1 => Dto.Roots[0],
        _ => $"{Dto.Roots.Count} locations",
    };
    public bool HasRoots => Dto.Roots.Count > 0;
    public int OverrideCount => Dto.Overrides.Count;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private bool _isSelected;
    partial void OnDtoChanged(ShareDefinitionDto value) { Scanning = value.Scanning; OnPropertyChanged(string.Empty); }
}

public partial class LocalItemVm : ObservableObject
{
    private readonly Func<LocalItemVm, Task<BitmapImage?>> _thumbLoader;
    private BitmapImage? _thumb;
    private bool _loading;
    public LocalItemDto Dto { get; private set; }
    public LocalItemVm(LocalItemDto dto, Func<LocalItemVm, Task<BitmapImage?>> loader) { Dto = dto; _thumbLoader = loader; }
    public string Id => Dto.Id;
    public string Name => Dto.Name;
    public string Folder => FolderPath.Display(Dto.Folder);
    [ObservableProperty] private bool _isSelected;
    public string SizeText => Format.Bytes(Dto.Size);
    public string DurationText => Format.Duration(Dto.DurationMs);
    public string AddedText => Format.Ago(Dto.AddedAt);
    public string FullPath => Dto.FullPath;
    public ItemAccessMode OverrideMode => Dto.OverrideMode;
    public string PolicyText => Dto.OverrideMode switch { ItemAccessMode.Public => "Public", ItemAccessMode.Password => "Password", ItemAccessMode.Ask => "Ask me", _ => "Same as share" };
    public string PolicyGlyph => Dto.OverrideMode switch { ItemAccessMode.Password => "", ItemAccessMode.Ask => "", ItemAccessMode.Public => "", _ => "" };
    public bool HasOverride => Dto.OverrideMode != ItemAccessMode.Inherit;
    public BitmapImage? Thumb
    {
        get
        {
            if (_thumb is null && !_loading) { _loading = true; _ = Load(); }
            return _thumb;
        }
    }
    private async Task Load()
    {
        try
        {
            var img = await _thumbLoader(this);
            if (img is not null) { _thumb = img; OnPropertyChanged(nameof(Thumb)); }
            else { await Task.Delay(20000); _loading = false; }
        }
        catch { _loading = false; }
    }
    public void Update(LocalItemDto dto) { Dto = dto; OnPropertyChanged(string.Empty); }
}

/// <summary>A folder in "My shares" (owner side): open, rename, delete, drop videos onto it.</summary>
public partial class LocalFolderVm : ObservableObject
{
    public string Path { get; }
    public string Name => FolderPath.Name(Path);
    public int Count { get; }
    public long TotalBytes { get; }
    public LocalItemVm? Cover { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDropTarget;

    public LocalFolderVm(string path, IReadOnlyCollection<LocalItemVm> contents)
    {
        Path = path;
        Count = contents.Count;
        TotalBytes = contents.Sum(i => i.Dto.Size);
        Cover = contents.OrderByDescending(i => i.Dto.AddedAt).FirstOrDefault();
        if (Cover is not null) Cover.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(LocalItemVm.Thumb) or "") OnPropertyChanged(nameof(Thumb)); };
    }

    public BitmapImage? Thumb => Cover?.Thumb;
    public string Summary => Count == 0 ? "Empty folder" : $"{Count} video{(Count == 1 ? "" : "s")}  ·  {Format.Bytes(TotalBytes)}";
}

public partial class GrantVm : ObservableObject
{
    public GrantDto Dto { get; }
    public GrantVm(GrantDto d) { Dto = d; }
    public string Id => Dto.Id;
    public string Who => Dto.ClientName;
    public string What => Dto.LockLabel;
    public string ExpiresText => Dto.ExpiresAt is { } e ? (e < DateTimeOffset.UtcNow ? "expired" : "until " + e.LocalDateTime.ToString("g")) : "always";
}

public partial class ShareEditorViewModel : ObservableObject
{
    public string? Id { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private AccessMode _mode = AccessMode.Public;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _hasExistingPassword;
    [ObservableProperty] private string? _error;
    public ObservableCollection<string> Roots { get; } = new();
    public bool IsNew => Id is null;
    public string Title => IsNew ? "New share" : "Edit share";
    public bool IsPasswordMode => Mode == AccessMode.Password;
    partial void OnModeChanged(AccessMode value) => OnPropertyChanged(nameof(IsPasswordMode));

    public ShareUpsertDto? Build()
    {
        Error = null;
        // An existing (possibly empty) folder may stay empty; a brand-new share needs content or a name.
        if (Roots.Count == 0 && IsNew && string.IsNullOrWhiteSpace(Name)) { Error = "Add a folder or video files, or at least give it a name."; return null; }
        var name = string.IsNullOrWhiteSpace(Name) ? SuggestName() : Name.Trim();
        if (Mode == AccessMode.Password && Password.Length == 0 && !HasExistingPassword) { Error = "Enter a password for this share."; return null; }
        return new ShareUpsertDto
        {
            Id = Id, Name = name, Mode = Mode, Roots = Roots.ToList(),
            Password = Password.Length > 0 ? Password : null,
        };
    }

    public string SuggestName()
    {
        var first = Roots.FirstOrDefault();
        if (first is null) return "My videos";
        if (File.Exists(first)) return Path.GetFileNameWithoutExtension(first);
        var trimmed = first.TrimEnd('\\', '/');
        var n = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(n) ? trimmed.Replace(":", "").Replace("\\", "") + " drive" : n;
    }
}

/// <summary>"My shares": what this PC offers to the LAN, per-item policies, and who has been granted access.</summary>
public partial class SharesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, LocalItemVm> _itemVms = new();
    private CancellationTokenSource? _reloadDebounce;

    public ObservableCollection<MyShareVm> Shares { get; } = new();
    public ObservableCollection<GrantVm> Grants { get; } = new();
    [ObservableProperty] private MyShareVm? _selected;
    [ObservableProperty] private List<LocalItemVm> _items = new();
    [ObservableProperty] private bool _itemsLoading;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _itemFilter = "";
    private List<LocalItemVm> _allItems = new();

    public SharesViewModel(MainViewModel main) { _main = main; }

    partial void OnSelectedChanged(MyShareVm? oldValue, MyShareVm? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (!ReferenceEquals(oldValue, newValue))
        {
            // New share: start at its root; the list is rebuilt once its items are loaded.
            _switchingShare = true;
            try { CurrentFolder = ""; } finally { _switchingShare = false; }
        }
        _ = LoadItemsAsync();
    }

    partial void OnItemFilterChanged(string value) => ApplyItemFilter();

    public async Task LoadAsync()
    {
        try
        {
            var list = await _main.Api.GetAsync<List<ShareDefinitionDto>>("/api/shares") ?? new();
            var keep = Selected?.Id;
            var ids = list.Select(s => s.Id).ToHashSet();
            for (var i = Shares.Count - 1; i >= 0; i--) if (!ids.Contains(Shares[i].Id)) Shares.RemoveAt(i);
            foreach (var dto in list)
            {
                var vm = Shares.FirstOrDefault(s => s.Id == dto.Id);
                if (vm is null) Shares.Add(new MyShareVm(dto)); else vm.Dto = dto;
            }
            IsEmpty = Shares.Count == 0;
            if (Selected is null || !ids.Contains(Selected.Id)) Selected = Shares.FirstOrDefault(s => s.Id == keep) ?? Shares.FirstOrDefault();
            else _ = LoadItemsAsync();
            await RefreshGrantsAsync();
        }
        catch (Exception ex) { _main.Error("Could not load shares", ex); }
    }

    public void ScheduleReload()
    {
        _reloadDebounce?.Cancel();
        var cts = _reloadDebounce = new CancellationTokenSource();
        _ = Task.Delay(600, cts.Token).ContinueWith(t => { if (!t.IsCanceled) System.Windows.Application.Current.Dispatcher.InvokeAsync(() => LoadAsync()); }, TaskScheduler.Default);
    }

    public void SetScanning(string shareId, bool scanning)
    {
        var s = Shares.FirstOrDefault(x => x.Id == shareId);
        if (s is not null) s.Scanning = scanning;
    }

    private async Task LoadItemsAsync()
    {
        var share = Selected;
        if (share is null) { Items = new(); _allItems = new(); return; }
        ItemsLoading = true;
        try
        {
            var list = await _main.Api.GetAsync<List<LocalItemDto>>($"/api/shares/{share.Id}/items") ?? new();
            if (!ReferenceEquals(Selected, share)) return;
            var vms = new List<LocalItemVm>(list.Count);
            foreach (var dto in list)
            {
                if (_itemVms.TryGetValue(dto.Id, out var vm)) vm.Update(dto);
                else { vm = new LocalItemVm(dto, LoadThumbAsync); _itemVms[dto.Id] = vm; }
                vms.Add(vm);
            }
            _allItems = vms;
            ApplyItemFilter();
        }
        catch (Exception ex) { _main.Error("Could not load items", ex); }
        finally { ItemsLoading = false; }
    }

    // ------------------------------------------------------------------ top level: folders = shares

    /// <summary>Creates an empty, named folder at the root of My shares (shared to everyone; change in Edit).</summary>
    [RelayCommand]
    private async Task NewTopFolder()
    {
        var name = await _main.Dialogs.PromptAsync("New folder", "Create a folder in My shares. Add videos to it afterwards; nothing is shared until you do.", "", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var saved = await _main.Api.PostAsync<ShareDefinitionDto>("/api/shares", new ShareUpsertDto { Name = name.Trim(), Mode = AccessMode.Public });
            await LoadAsync();
            if (saved is not null) Selected = Shares.FirstOrDefault(s => s.Id == saved.Id);
            _main.Toast("Folder created", $"Add videos to “{name.Trim()}”, or drop files onto it.", "success");
        }
        catch (Exception ex) { _main.Dialogs.ShowError("Could not create folder", ex.Message); }
    }

    [RelayCommand]
    private async Task RenameShare(MyShareVm? share)
    {
        share ??= Selected;
        if (share is null) return;
        var name = await _main.Dialogs.PromptAsync("Rename", "New name (others see it right away)", share.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == share.Name) return;
        try
        {
            await _main.Api.PostAsync<ShareDefinitionDto>("/api/shares", new ShareUpsertDto
            {
                Id = share.Id, Name = name.Trim(), Mode = share.Mode, Roots = share.Dto.Roots.ToList(), Password = null, // null keeps the password
            });
            await LoadAsync();
        }
        catch (Exception ex) { _main.Error("Could not rename", ex); }
    }

    [RelayCommand]
    private async Task AddVideos(MyShareVm? share)
    {
        share ??= Selected;
        var files = _main.Dialogs.PickFiles("Add videos");
        if (share is null || files is null || files.Length == 0) return;
        await AddPathsAsync(share, files);
    }

    [RelayCommand]
    private async Task AddFolderTo(MyShareVm? share)
    {
        share ??= Selected;
        var folder = _main.Dialogs.PickFolder("Add a folder (its sub-folders come along)");
        if (share is null || folder is null) return;
        await AddPathsAsync(share, new[] { folder });
    }

    /// <summary>Adds files/folders to a share; when it is the open share, they land in the folder being viewed.</summary>
    public async Task AddPathsAsync(MyShareVm share, IEnumerable<string> paths)
    {
        var list = paths.Where(p => Directory.Exists(p) || (File.Exists(p) && VideoTypes.IsVideo(p))).ToList();
        if (list.Count == 0) { _main.Toast("Nothing to add", "Pick video files or folders.", "warn"); return; }
        var into = ReferenceEquals(share, Selected) ? CurrentFolder : "";
        try
        {
            await _main.Api.PostAsync($"/api/shares/{share.Id}/add", new AddToShareDto { Paths = list, Folder = into });
            _main.Toast("Added", $"{(list.Count == 1 ? Path.GetFileName(list[0].TrimEnd('\\')) : $"{list.Count} items")} → “{(into.Length == 0 ? share.Name : FolderPath.Display(into))}”", "success");
            await Task.Delay(800); // let the scan pick them up
            await LoadAsync();
        }
        catch (Exception ex) { _main.Error("Could not add", ex); }
    }

    // ------------------------------------------------------------------ folders (organize)

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private List<object> _entries = new();
    [ObservableProperty] private List<Crumb> _crumbs = new();
    [ObservableProperty] private bool _folderMode = true;
    [ObservableProperty] private int _selectedItemCount;

    private bool _switchingShare;
    partial void OnCurrentFolderChanged(string value) { if (!_switchingShare) ApplyItemFilter(); }

    private List<string> Folders => Selected?.Dto.Folders ?? new();

    /// <summary>Folder view (videos + sub-folders of the current folder), or a flat list while searching.</summary>
    private void ApplyItemFilter()
    {
        var f = ItemFilter.Trim();
        FolderMode = f.Length == 0;
        if (!FolderMode)
        {
            Items = _allItems.Where(i => i.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || i.Folder.Contains(f, StringComparison.CurrentCultureIgnoreCase)).ToList();
            Entries = Items.Cast<object>().ToList();
            Crumbs = new();
            OnSelectionChanged();
            return;
        }
        if (CurrentFolder.Length > 0 && !Folders.Contains(CurrentFolder, StringComparer.OrdinalIgnoreCase))
        {
            CurrentFolder = ""; // removed or renamed elsewhere; re-enters here
            return;
        }
        var subFolders = Folders
            .Where(p => string.Equals(FolderPath.Parent(p), CurrentFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new LocalFolderVm(p, _allItems.Where(i => FolderPath.IsUnder(i.Dto.Folder, p)).ToList()))
            .ToList();
        Items = _allItems.Where(i => string.Equals(i.Dto.Folder, CurrentFolder, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Entries = subFolders.Cast<object>().Concat(Items).ToList();
        var crumbs = new List<Crumb> { new(Selected?.Name ?? "Share", "", CurrentFolder.Length == 0) };
        crumbs.AddRange(FolderPath.SelfAndAncestors(CurrentFolder).Select(p => new Crumb(FolderPath.Name(p), p, string.Equals(p, CurrentFolder, StringComparison.OrdinalIgnoreCase))));
        Crumbs = crumbs;
        OnSelectionChanged();
    }

    public void OnSelectionChanged() => SelectedItemCount = Entries.OfType<LocalItemVm>().Count(i => i.IsSelected);

    private List<LocalItemVm> SelectedItems() => Entries.OfType<LocalItemVm>().Where(i => i.IsSelected).ToList();

    [RelayCommand] private void EnterFolder(LocalFolderVm? folder) { if (folder is not null) CurrentFolder = folder.Path; }
    [RelayCommand] private void GoToFolder(string? path) => CurrentFolder = path ?? "";
    [RelayCommand] private void Up() { if (CurrentFolder.Length > 0) CurrentFolder = FolderPath.Parent(CurrentFolder); }

    [RelayCommand]
    private async Task NewFolder()
    {
        if (Selected is null) return;
        var name = await _main.Dialogs.PromptAsync("New folder", CurrentFolder.Length == 0 ? $"Create a folder in “{Selected.Name}”" : $"Create a folder in “{FolderPath.Name(CurrentFolder)}”", "", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = FolderPath.Combine(CurrentFolder, name.Replace('/', '-').Replace('\\', '-'));
        if (await OrganizeAsync($"/api/shares/{Selected.Id}/folders", new FolderOpDto { Path = path }))
            _main.Toast("Folder created", $"Drag videos onto “{FolderPath.Name(path)}”, or use Move to….", "success");
    }

    [RelayCommand]
    private async Task RenameFolder(LocalFolderVm? folder)
    {
        if (folder is null || Selected is null) return;
        var name = await _main.Dialogs.PromptAsync("Rename folder", "New name", folder.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name) return;
        var to = FolderPath.Combine(FolderPath.Parent(folder.Path), name.Replace('/', '-').Replace('\\', '-'));
        await OrganizeAsync($"/api/shares/{Selected.Id}/folders/rename", new FolderOpDto { Path = folder.Path, To = to });
    }

    [RelayCommand]
    private async Task DeleteFolder(LocalFolderVm? folder)
    {
        if (folder is null || Selected is null) return;
        var where = CurrentFolder.Length == 0 ? $"“{Selected.Name}”" : $"“{FolderPath.Name(CurrentFolder)}”";
        if (folder.Count > 0 && !await _main.Dialogs.ConfirmAsync("Remove folder?",
                $"“{folder.Name}” is removed from Ocean and its {folder.Count} video(s) move up to {where}. Nothing is deleted from your disk.", "Remove folder", danger: true))
            return;
        await OrganizeAsync($"/api/shares/{Selected.Id}/folders/delete", new FolderOpDto { Path = folder.Path });
    }

    /// <summary>"Move to…" for the selected videos (or the right-clicked one).</summary>
    [RelayCommand]
    private async Task MoveTo(LocalItemVm? clicked)
    {
        if (Selected is null) return;
        var items = SelectedItems();
        if (clicked is not null && !items.Contains(clicked)) items = new() { clicked };
        if (items.Count == 0) return;
        var target = await _main.Dialogs.PickShareFolderAsync(
            items.Count == 1 ? $"Move “{items[0].Name}”" : $"Move {items.Count} videos", Selected.Name, Folders, CurrentFolder);
        if (target is null) return;
        await MoveItemsAsync(items.Select(i => i.Id).ToList(), target);
    }

    /// <summary>Used by drag and drop as well.</summary>
    public async Task MoveItemsAsync(List<string> ids, string folder)
    {
        if (Selected is null || ids.Count == 0) return;
        if (await OrganizeAsync($"/api/shares/{Selected.Id}/move", new MoveItemsDto { ItemIds = ids, Folder = folder }))
            _main.Toast(ids.Count == 1 ? "Moved" : $"Moved {ids.Count} videos", folder.Length == 0 ? $"to “{Selected.Name}”" : $"to “{FolderPath.Display(folder)}”", "success");
    }

    private async Task<bool> OrganizeAsync(string url, object body)
    {
        try
        {
            await _main.Api.PostAsync(url, body);
            await LoadAsync(); // refreshes folders and items
            return true;
        }
        catch (Exception ex) { _main.Error("Could not organize", ex); return false; }
    }

    private Task<BitmapImage?> LoadThumbAsync(LocalItemVm item) => _main.Thumbs.GetAsync(_main.Api.LocalThumbPath(item.Id));

    public async Task RefreshGrantsAsync()
    {
        try
        {
            var list = await _main.Api.GetAsync<List<GrantDto>>("/api/access/grants") ?? new();
            Grants.Clear();
            foreach (var g in list) Grants.Add(new GrantVm(g));
        }
        catch { }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = _main.Dialogs.PickFolder("Choose a folder to share");
        if (folder is null) return;
        await NewShareFromPathsAsync(new[] { folder });
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = _main.Dialogs.PickFiles("Choose videos to share");
        if (files is null || files.Length == 0) return;
        await NewShareFromPathsAsync(files);
    }

    public async Task NewShareFromPathsAsync(IEnumerable<string> paths)
    {
        var editor = new ShareEditorViewModel();
        foreach (var p in paths.Where(p => Directory.Exists(p) || (File.Exists(p) && VideoTypes.IsVideo(p)))) editor.Roots.Add(p);
        if (editor.Roots.Count == 0) { _main.Toast("Nothing to share", "Drop folders or video files.", "warn"); return; }
        editor.Name = editor.SuggestName();
        var dto = await _main.Dialogs.EditShareAsync(editor);
        if (dto is null) return;
        await SaveAsync(dto);
    }

    [RelayCommand]
    private async Task Edit(MyShareVm? share)
    {
        share ??= Selected;
        if (share is null) return;
        var editor = new ShareEditorViewModel { Id = share.Id, Name = share.Name, Mode = share.Mode, HasExistingPassword = share.Dto.HasPassword };
        foreach (var r in share.Dto.Roots) editor.Roots.Add(r);
        var dto = await _main.Dialogs.EditShareAsync(editor);
        if (dto is null) return;
        await SaveAsync(dto);
    }

    private async Task SaveAsync(ShareUpsertDto dto)
    {
        try
        {
            var saved = await _main.Api.PostAsync<ShareDefinitionDto>("/api/shares", dto);
            await LoadAsync();
            if (saved is not null) Selected = Shares.FirstOrDefault(s => s.Id == saved.Id);
            _main.Toast(dto.Id is null ? "Share created" : "Share updated", $"“{dto.Name}” is {(dto.Mode == AccessMode.Public ? "visible to everyone on your network" : dto.Mode == AccessMode.Password ? "password protected" : "available on request")}.", "success");
        }
        catch (Exception ex) { _main.Dialogs.ShowError("Could not save share", ex.Message); }
    }

    [RelayCommand]
    private async Task Remove(MyShareVm? share)
    {
        share ??= Selected;
        if (share is null) return;
        if (!await _main.Dialogs.ConfirmAsync($"Remove “{share.Name}”?", "It disappears from My shares and from the network. Your files on disk are not deleted.", "Remove", danger: true)) return;
        try { await _main.Api.DeleteAsync($"/api/shares/{share.Id}"); await LoadAsync(); }
        catch (Exception ex) { _main.Error("Could not remove share", ex); }
    }

    [RelayCommand]
    private async Task Rescan(MyShareVm? share)
    {
        share ??= Selected;
        if (share is null) return;
        try { share.Scanning = true; await _main.Api.PostAsync($"/api/shares/{share.Id}/rescan"); }
        catch (Exception ex) { _main.Error("Could not rescan", ex); }
    }

    [RelayCommand]
    private void OpenFolder(MyShareVm? share)
    {
        share ??= Selected;
        var root = share?.Dto.Roots.FirstOrDefault();
        if (root is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(root) ? $"/select,\"{root}\"" : $"\"{root}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void RevealItem(LocalItemVm? item)
    {
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task SetItemPolicy(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } arr || arr[0] is not LocalItemVm item || arr[1] is not string modeText) return;
        var mode = Enum.Parse<ItemAccessMode>(modeText, true);
        string? password = null;
        if (mode == ItemAccessMode.Password)
        {
            password = await _main.Dialogs.AskPasswordAsync($"Password for “{item.Name}”", "Others will need this password to watch or download this video.");
            if (password is null) return;
        }
        try
        {
            await _main.Api.PutAsync($"/api/shares/{Selected!.Id}/items/{item.Id}/policy", new ItemOverrideUpsertDto { Mode = mode, Password = password });
            await LoadItemsAsync();
        }
        catch (Exception ex) { _main.Error("Could not change policy", ex); }
    }

    [RelayCommand]
    private async Task RevokeGrant(GrantVm? grant)
    {
        if (grant is null) return;
        try { await _main.Api.DeleteAsync($"/api/access/grants/{grant.Id}"); await RefreshGrantsAsync(); }
        catch (Exception ex) { _main.Error("Could not revoke", ex); }
    }
}
