using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

public enum ViewMode { Grid, Tiles, List }
public enum SortMode { Newest, Oldest, Name, Size, Duration, Folder }

/// <summary>Dialogs the view layer provides to view models.</summary>
public interface IDialogs
{
    Task<string?> AskPasswordAsync(string title, string subtitle);
    /// <summary>A new password for one of my videos: typed twice, never empty ("Set password"). Null = cancelled.</summary>
    Task<string?> NewPasswordAsync(string title, string subtitle);
    Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", bool danger = false);
    /// <summary>Two ways forward and Cancel: 1 = <paramref name="okLabel"/>, 2 = <paramref name="altLabel"/>, 0 = cancelled.</summary>
    Task<int> ChooseAsync(string title, string message, string okLabel, string altLabel, bool altDanger = false);
    Task<ShareUpsertDto?> EditShareAsync(ShareEditorViewModel editor);
    Task<string?> PromptAsync(string title, string message, string initial, string okLabel);
    /// <summary>Lets the user pick a folder of a share ("" = its root), optionally creating a new one. Null = cancelled.</summary>
    Task<string?> PickShareFolderAsync(string title, string shareName, IReadOnlyList<string> folders, string current);
    string? PickFolder(string title);
    string[]? PickFiles(string title);
    void ShowError(string title, string message);
    /// <summary>The "Node info" window for one node.</summary>
    void ShowPeerInfo(PeerInfoViewModel info);
    /// <summary>The notes of one video in a window of their own (one per video).</summary>
    void ShowNotes(NotesViewModel notes);
}

public partial class PeerVm : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _itemCount;
    [ObservableProperty] private int _shareCount;
    [ObservableProperty] private bool _catalogLoaded;
    [ObservableProperty] private DateTimeOffset _firstSeen;
    [ObservableProperty] private bool _online = true;
    [ObservableProperty] private DateTimeOffset? _offlineSince;
    /// <summary>Name of the PC it is reached through (no network path from here), or null.</summary>
    [ObservableProperty] private string? _via;
    [ObservableProperty] private int _viaCount;
    /// <summary>The multicast this node hosts right now (null when none).</summary>
    [ObservableProperty] private CastInfoDto? _cast;
    public bool IsLive => Cast is not null && Online;
    public string LiveText => Cast is null ? "" : $"Multicasting “{Cast.ItemName}”";
    partial void OnCastChanged(CastInfoDto? value) { OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(LiveText)); OnPlanChanged(); }

    /// <summary>The multicast this node has scheduled to start later (null when none).</summary>
    [ObservableProperty] private CastPlanDto? _castPlan;
    /// <summary>A multicast is coming (and isn't running yet).</summary>
    public bool IsScheduled => CastPlan is not null && Cast is null && Online;
    public string PlanTime => CastPlan is null ? "" : CastPlan.At.ToLocalTime().ToString("HH:mm");
    public string PlanText => CastPlan is null ? "" : $"Multicast of “{CastPlan.ItemName}” at {Format.ClockTime(CastPlan.At)} ({Format.Until(CastPlan.At)})";
    partial void OnCastPlanChanged(CastPlanDto? value) => OnPlanChanged();
    /// <summary>Also every half minute (the countdown).</summary>
    public void OnPlanChanged() { OnPropertyChanged(nameof(IsScheduled)); OnPropertyChanged(nameof(PlanTime)); OnPropertyChanged(nameof(PlanText)); }

    public PeerVm(PeerDto d) { Id = d.Id; Update(d); }

    /// <summary>"Offline · last seen 5 min ago" (refreshes whenever the list is updated).</summary>
    public string OfflineText => OfflineSince is { } t ? $"Offline  ·  last seen {Format.Ago(t)}" : "Offline";

    public string Initials
    {
        get
        {
            var src = string.IsNullOrWhiteSpace(User) ? Name : User;
            var parts = src.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
            return src.Length >= 2 ? src[..2].ToUpperInvariant() : src.ToUpperInvariant();
        }
    }

    public string Subtitle => (string.IsNullOrEmpty(User) || User == Name ? Host : $"{User} @ {Host}")
                              + (Via is null ? "" : ViaCount > 1 ? $"  ·  via {Via} and {ViaCount - 1} more" : $"  ·  via {Via}");
    public string Label => DisplayNames.Of(User, Name);

    public void Update(PeerDto d)
    {
        Name = d.Name; User = d.User; Host = d.Host; Address = d.Address;
        NewCount = d.NewCount; ItemCount = d.ItemCount; ShareCount = d.ShareCount; CatalogLoaded = d.CatalogLoaded; FirstSeen = d.FirstSeen;
        Online = d.Online; OfflineSince = d.OfflineSince; Via = d.Via; ViaCount = d.ViaCount; Cast = d.Cast; CastPlan = d.CastPlan;
        OnPropertyChanged(nameof(Initials)); OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(OfflineText)); OnPropertyChanged(nameof(IsLive));
        OnPlanChanged();
    }
}

/// <summary>A multicast on the network, for the home page's "Live now" list.</summary>
/// <summary>Home, "Coming up": a multicast a node has scheduled.</summary>
public sealed partial class UpcomingCastVm : ObservableObject
{
    public UpcomingCastVm(PeerVm peer, CastPlanDto plan) { Peer = peer; Plan = plan; }
    public PeerVm Peer { get; }
    public CastPlanDto Plan { get; }
    public string Title => Plan.ItemName;
    public string Who => $"{Peer.Label} will multicast";
    public string Time => Format.ClockTime(Plan.At);
    public string Meta => $"Starts {Format.Until(Plan.At)}  ·  you'll get a notice when it does";
    public void Tick() => OnPropertyChanged(nameof(Meta));
}

public sealed class LiveCastVm
{
    public LiveCastVm(PeerVm peer, CastInfoDto cast) { Peer = peer; Cast = cast; }
    public PeerVm Peer { get; }
    public CastInfoDto Cast { get; }
    public string Title => Cast.ItemName;
    public string Who => $"{Peer.Label} is multicasting";
    public string Meta => $"{Cast.Viewers} watching  ·  started {Format.Ago(Cast.StartedAt)}" + (Cast.Light ? "  ·  lighter copy" : "");
}

public partial class ItemVm : ObservableObject
{
    private readonly Func<ItemVm, Task<BitmapImage?>>? _thumbLoader;
    private BitmapImage? _thumb;
    private bool _loading;

    public ItemDto Dto { get; private set; }
    public string PeerId { get; }
    public string ShareName { get; }

    public ItemVm(ItemDto dto, string peerId, string shareName, Func<ItemVm, Task<BitmapImage?>>? thumbLoader)
    {
        Dto = dto; PeerId = peerId; ShareName = shareName; _thumbLoader = thumbLoader;
    }

    public string Id => Dto.Id;
    public string Name => Dto.Name;
    public string Stem => Path.GetFileNameWithoutExtension(Dto.Name);
    public string Ext => Path.GetExtension(Dto.Name).TrimStart('.').ToUpperInvariant();
    public string Folder => Core.Naming.FolderPath.Display(Dto.Folder);
    public string Location => Folder.Length > 0 ? $"{ShareName}  ›  {Folder}" : ShareName;
    public string SizeText => Format.Bytes(Dto.Size);
    public string DurationText => Format.Duration(Dto.DurationMs);
    public string ResolutionText => Format.Resolution(Dto.Width, Dto.Height);
    public string AddedText => Format.Ago(Dto.AddedAt);
    public string AddedFull => Dto.AddedAt.LocalDateTime.ToString("f");
    public DateTimeOffset AddedAt => Dto.AddedAt;
    public long Size => Dto.Size;
    public long DurationMs => Dto.DurationMs ?? 0;
    public bool IsLocked => Dto.Lock is not null && !Dto.Unlocked;
    public bool IsNew => Dto.IsNew;
    public bool IsMine => Dto.IsMine;
    public string LockText => Dto.Lock?.Mode == AccessMode.Password ? "Password protected" : Dto.Lock?.Mode == AccessMode.Ask ? "Owner approval required" : "";
    /// <summary>"  ·  Light" when the owner has a lighter copy for watching, "  ·  Preparing…" while it is being made.</summary>
    public string LightTag => Dto.Light == LightVariant.Ready ? "  ·  Light" : Dto.Light == LightVariant.Preparing ? "  ·  Preparing…" : "";
    public string Tooltip => $"{Dto.Name}\n{ShareName}{(Folder.Length > 0 ? "  ›  " + Folder : "")}\n{SizeText}{(DurationText.Length > 0 ? "  ·  " + DurationText : "")}{(ResolutionText.Length > 0 ? "  ·  " + ResolutionText : "")}\nAdded {AddedFull}"
                             + (HasVotes ? "\n" + VotesTip : "") + (HasTags ? "\n" + TagsText : "");

    // ---- tags (set by the owner) and votes (anyone's, counted by the owner) ----
    public IReadOnlyList<string> Tags => Dto.Tags ?? (IReadOnlyList<string>)Array.Empty<string>();
    public bool HasTags => Dto.Tags is { Count: > 0 };
    public string TagsText => Votes.TagsText(Dto.Tags);
    public int Up => Dto.Up;
    public int Down => Dto.Down;
    public bool HasVotes => Dto.Up + Dto.Down > 0;
    public string VotesTip => Votes.Tip(Dto);
    // ---- notes (kept by the owner; the count travels in its catalog) ----
    public int NoteCount => Dto.NoteCount;
    public bool HasNotes => Dto.NoteCount > 0;
    public string NotesMenu => Dto.NoteCount > 0 ? $"Notes ({Dto.NoteCount})…" : "Notes…";
    public string NotesTip => Dto.NoteCount == 1 ? "1 note: open them from the video's menu or in the player" : $"{Dto.NoteCount} notes: open them from the video's menu or in the player";
    /// <summary>The notes panel counted them anew (before the owner's next catalog arrives).</summary>
    public void SetNoteCount(int count) { if (Dto.NoteCount == count) return; Dto.NoteCount = count; OnPropertyChanged(nameof(NoteCount)); OnPropertyChanged(nameof(HasNotes)); OnPropertyChanged(nameof(NotesTip)); OnPropertyChanged(nameof(NotesMenu)); }
    /// <summary>My thumbs up (1), down (-1) or nothing yet (0).</summary>
    public int MyVote => Dto.MyVote;
    public bool IsUp => Dto.MyVote > 0;
    public bool IsDown => Dto.MyVote < 0;
    /// <summary>No voting on my own videos (copies of mine shared back by others included).</summary>
    public bool CanVote => !IsMine;
    public string LikeMenu => IsUp ? "Take back my like" : "Like";
    public string DislikeMenu => IsDown ? "Take back my dislike" : "Dislike";
    public bool HasTag(string tag) => Dto.Tags?.Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)) == true;

    /// <summary>The owner's answer to my vote (or the service's note while the owner is away).</summary>
    public void ApplyRating(MyRatingDto r)
    {
        Dto.MyVote = r.MyVote;
        if (r.Totals is { } t) { Dto.Up = t.Up; Dto.Down = t.Down; Dto.Finished = t.Finished; Dto.Score = t.Score; }
        OnPropertyChanged(string.Empty);
    }

    [ObservableProperty] private bool _isSelected;

    /// <summary>Lazily loads the thumbnail the first time a realized tile binds to it.</summary>
    public BitmapImage? Thumb
    {
        get
        {
            if (_thumb is null && !_loading && !IsLocked && _thumbLoader is not null)
            {
                _loading = true;
                _ = LoadThumbAsync();
            }
            return _thumb;
        }
    }

    public bool HasThumb => _thumb is not null;

    private async Task LoadThumbAsync()
    {
        try
        {
            var img = await _thumbLoader!(this);
            if (img is not null)
            {
                _thumb = img;
                OnPropertyChanged(nameof(Thumb));
                OnPropertyChanged(nameof(HasThumb));
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                _loading = false; // allow a retry later (thumbnail may be generated by then)
            }
        }
        catch { _loading = false; }
    }

    public void Update(ItemDto dto)
    {
        var wasLocked = IsLocked;
        Dto = dto;
        OnPropertyChanged(string.Empty);
        if (wasLocked && !IsLocked) { _loading = false; OnPropertyChanged(nameof(Thumb)); }
    }
}

/// <summary>A folder shown while browsing a share (sub-folder of the current folder).</summary>
public partial class FolderVm : ObservableObject
{
    public string Path { get; }
    public string Name => Core.Naming.FolderPath.Name(Path);
    public int Count { get; }
    public int NewCount { get; }
    public long TotalBytes { get; }
    public DateTimeOffset Newest { get; }
    /// <summary>Most recent video inside, used as the folder's cover image.</summary>
    public ItemVm? Cover { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDropTarget;

    public FolderVm(string path, IReadOnlyCollection<ItemVm> contents)
    {
        Path = path;
        Count = contents.Count;
        NewCount = contents.Count(i => i.IsNew);
        TotalBytes = contents.Sum(i => i.Size);
        Newest = contents.Count == 0 ? DateTimeOffset.MinValue : contents.Max(i => i.AddedAt);
        Cover = contents.Where(i => !i.IsLocked).OrderByDescending(i => i.AddedAt).FirstOrDefault();
        if (Cover is not null)
            Cover.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ItemVm.Thumb) or nameof(ItemVm.HasThumb) or "") { OnPropertyChanged(nameof(Thumb)); OnPropertyChanged(nameof(HasThumb)); } };
    }

    public BitmapImage? Thumb => Cover?.Thumb;
    public bool HasThumb => Cover?.HasThumb == true;
    public string Summary => Count == 0 ? "Empty folder" : $"{Count} video{(Count == 1 ? "" : "s")}  ·  {Format.Bytes(TotalBytes)}";
    public string Tooltip => $"{Name}\n{Summary}";
}

/// <summary>One step of a breadcrumb trail ("Family" › "2024" › "Beach").</summary>
public sealed record Crumb(string Name, string Path, bool IsLast);

/// <summary>Text for votes and tags shared by the browse tiles, My shares and the home page.</summary>
public static class Votes
{
    public static string TagsText(List<string>? tags) => tags is { Count: > 0 } ? string.Join("  ", tags.Select(t => "#" + t)) : "";

    public static string Tip(ItemDto d)
    {
        var parts = new List<string>();
        if (d.Up > 0) parts.Add(d.Up == 1 ? "1 person liked it" : $"{d.Up} people liked it");
        if (d.Down > 0) parts.Add(d.Down == 1 ? "1 didn't" : $"{d.Down} didn't");
        if (d.Finished > 0) parts.Add(d.Finished == 1 ? "1 watched it to the end" : $"{d.Finished} watched it to the end");
        return parts.Count == 0 ? "No votes yet" : string.Join("  ·  ", parts);
    }
}

/// <summary>A tag chip: filter by it in a node's list or in "Top on the network".</summary>
public partial class TagChipVm : ObservableObject
{
    public TagChipVm(string tag, int count) { Tag = tag; Count = count; }
    public string Tag { get; }
    public string Label => "#" + Tag;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A video somewhere on the network: a row of "Top on the network" (with its rank) or of the search page.</summary>
public sealed partial class NetItemVm : ObservableObject
{
    public NetItemVm(NetItemDto dto, MainViewModel main)
    {
        Dto = dto;
        Item = new ItemVm(dto.Item, dto.PeerId, dto.ShareName,
            i => main.Thumbs.GetAsync(dto.Mine ? main.Api.LocalThumbPath(i.Id) : main.Api.PeerThumbPath(dto.PeerId, i.Id)));
        Item.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty); // a vote from the row or the player
    }

    public NetItemDto Dto { get; private set; }
    public ItemVm Item { get; }
    [ObservableProperty] private int _rank;

    public static string KeyOf(NetItemDto d) => d.PeerId + "/" + d.Item.Id;
    public string Key => KeyOf(Dto);
    public string Title => Dto.Item.Name;
    public string Who => Dto.Mine ? "Your video" : Dto.Online ? Dto.PeerName : Dto.PeerName + "  ·  offline";
    /// <summary>"Node  ›  Share  ›  Folder" (search rows).</summary>
    public string Where => (Dto.Mine ? "My shares" : Dto.PeerName + (Dto.Online ? "" : " (offline)")) + "  ›  " + Dto.ShareName + (Item.Folder.Length > 0 ? "  ›  " + Item.Folder : "");
    public string Details => Item.SizeText + (Item.DurationText.Length > 0 ? "  ·  " + Item.DurationText : "") + (Item.ResolutionText.Length > 0 ? "  ·  " + Item.ResolutionText : "") + "  ·  added " + Item.AddedText;
    public bool Online => Dto.Online;
    public bool CanDownload => !Dto.Mine;
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Dto.Item.Up > 0) parts.Add($"{Dto.Item.Up} liked");
            if (Dto.Item.Finished > 0) parts.Add($"{Dto.Item.Finished} watched to the end");
            if (Item.DurationText.Length > 0) parts.Add(Item.DurationText);
            return string.Join("  ·  ", parts);
        }
    }
    public string Tooltip => Item.Tooltip;
    public string ActionText => Dto.Mine ? "Play" : IsLocked ? "Unlock" : "Watch";
    /// <summary>
    /// Locked for me (password or the owner's approval): no thumbnail (the owner never sends it to whoever can't open
    /// the video), "Unlock" instead of "Watch", no votes (the owner would refuse them). Unlocking runs the usual
    /// password / approval request, then plays.
    /// </summary>
    public bool IsLocked => !Dto.Mine && Item.IsLocked;
    /// <summary>Short, under the lock in place of the thumbnail.</summary>
    public string LockLabel => Item.Dto.Lock?.Mode == AccessMode.Password ? "Password" : "Approval";
    public string PlayTip => IsLocked ? Item.LockText + ": click to unlock" : "Play";
    public string PlayGlyph => IsLocked ? "\uE72E" : "\uE768";
    // my vote, mirrored from the item for the buttons on the row
    public bool CanVote => Item.CanVote && !IsLocked;
    public bool IsUp => Item.IsUp;
    public bool IsDown => Item.IsDown;
    public string LikeMenu => Item.LikeMenu;
    public string DislikeMenu => Item.DislikeMenu;

    public NetItemVm Update(NetItemDto d)
    {
        Dto = d;
        Item.Update(d.Item);
        OnPropertyChanged(string.Empty);
        return this;
    }
}

public partial class ShareChipVm : ObservableObject
{
    public string? Id { get; }
    public string LockId => "s:" + Id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private AccessMode _mode;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPending;

    public ShareChipVm(string? id) { Id = id; }
    public string SizeText => Format.Bytes(TotalBytes);
    public string CountText => Count == 1 ? "1 video" : $"{Count} videos";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
    public string LockGlyph => Mode == AccessMode.Password ? "" : Mode == AccessMode.Ask ? "" : "";
}

public partial class ToastVm : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "info"; // info | success | warn | error
    public Action? OnClick { get; init; }
    public string? ActionLabel { get; init; }
    public string Glyph => Kind switch { "success" => "", "warn" => "", "error" => "", _ => "" };
}

public partial class PendingAccessVm : ObservableObject
{
    public PendingAccessDto Dto { get; }
    public PendingAccessVm(PendingAccessDto d) { Dto = d; }
    public string RequestId => Dto.RequestId;
    public string Who => DisplayNames.Of(Dto.ClientUser, Dto.ClientName);
    public string What => Dto.ItemName is null ? $"share “{Dto.ShareName}”" : $"“{Dto.ItemName}” in “{Dto.ShareName}”";
    public string Title => $"{Who} wants to access {What}";
    public string Since => Format.Ago(Dto.CreatedAt);
    [ObservableProperty] private bool _busy;
}
