using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>
/// The notes of one video: shown beside the player and in a window from a video's menu. The owner's PC keeps them;
/// this reads them when opened and every 20 s while open. Sorted Top (most useful), Newest or By moment; a long list
/// shows 30 at a time; long notes are folded. A note written in the player is tied to the moment the writer started
/// it ("at 12:34"), which plays from there when clicked.
/// </summary>
public partial class NotesViewModel : ObservableObject
{
    public enum SortMode { Top, Newest, Moment }
    private const int Page = 30;

    private readonly MainViewModel _main;
    private List<NoteDto> _all = new();
    private int _shown = Page;
    private System.Windows.Threading.DispatcherTimer? _poll;

    /// <summary>The owner's node id, or "me" for my own video.</summary>
    public string PeerId { get; }
    public string ItemId { get; }
    public string ItemName { get; }
    /// <summary>Whose video ("hawk", or "You").</summary>
    public string OwnerLabel { get; }

    /// <summary>Set by the player: jump to a note's moment, and where playback is now (both null outside the player).</summary>
    public Action<double>? SeekTo { get; set; }
    public Func<double>? CurrentPosition { get; set; }
    /// <summary>The number of notes changed (the video's row shows it).</summary>
    public Action<int>? CountChanged { get; set; }

    public ObservableCollection<NoteVm> Notes { get; } = new();
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private bool _isOwner;
    [ObservableProperty] private string? _problemText;
    [ObservableProperty] private bool _canWrite = true;
    [ObservableProperty] private SortMode _sort = SortMode.Top;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private double? _draftAt;
    [ObservableProperty] private bool _posting;
    [ObservableProperty] private string? _writeError;

    public NotesViewModel(MainViewModel main, string peerId, string itemId, string itemName, string ownerLabel, int count)
    {
        _main = main; PeerId = peerId; ItemId = itemId; ItemName = itemName; OwnerLabel = ownerLabel; _count = count;
    }

    public bool InPlayer => CurrentPosition is not null;
    public string Header => Count == 0 ? "Notes" : $"Notes  ·  {Count}";
    public bool IsEmpty => Loaded && _all.Count == 0;
    public string EmptyText => CanWrite ? "No notes yet. Leave the first one: feedback, a question, a moment worth seeing." : "No notes to show.";
    public bool HasMore => _all.Count > _shown;
    public string MoreText => $"Show {Math.Min(Page, _all.Count - _shown)} more of {_all.Count - _shown}";
    public bool HasDraft => Draft.Length > 0;
    public string DraftAtText => DraftAt is { } at ? "at " + Format.Duration(TimeSpan.FromSeconds(at)) : "";
    public bool HasDraftAt => DraftAt is not null;
    /// <summary>"At this moment": in the player, writing, and the note isn't tied to a moment (any more).</summary>
    public bool ShowUseMoment => InPlayer && HasDraft && !HasDraftAt;
    public bool CanPost => CanWrite && !Posting && Draft.Trim().Length > 0 && Draft.Length <= NoteLimits.MaxLength;
    public string CounterText => Draft.Length >= NoteLimits.MaxLength - 200 ? $"{Draft.Length} / {NoteLimits.MaxLength}" : "";
    public string Placeholder => InPlayer ? "Leave a note… (it is tied to this moment of the video)" : "Leave a note…";

    partial void OnCountChanged(int value) { OnPropertyChanged(nameof(Header)); CountChanged?.Invoke(value); }
    partial void OnSortChanged(SortMode value) { _shown = Page; Rebuild(); }
    partial void OnCanWriteChanged(bool value) { OnPropertyChanged(nameof(CanPost)); OnPropertyChanged(nameof(EmptyText)); }
    partial void OnPostingChanged(bool value) => OnPropertyChanged(nameof(CanPost));
    partial void OnDraftAtChanged(double? value) { OnPropertyChanged(nameof(DraftAtText)); OnPropertyChanged(nameof(HasDraftAt)); OnPropertyChanged(nameof(ShowUseMoment)); }
    partial void OnDraftChanged(string value)
    {
        // a note started in the player belongs to the moment it was started
        if (value.Length > 0 && DraftAt is null && !_atRemoved && CurrentPosition?.Invoke() is { } now) DraftAt = now;
        if (value.Length == 0) { DraftAt = null; _atRemoved = false; }
        WriteError = null;
        OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(CanPost)); OnPropertyChanged(nameof(CounterText)); OnPropertyChanged(nameof(ShowUseMoment));
    }
    private bool _atRemoved;

    // ------------------------------------------------------------------ open / close

    /// <summary>The panel or window shows: read now and keep reading every 20 s.</summary>
    public void Opened()
    {
        _ = LoadAsync();
        _poll ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(20), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { if (!Loading && !Posting && !Notes.Any(n => n.IsEditing)) _ = LoadAsync(); }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _poll.Start();
    }

    public void Closed() => _poll?.Stop();

    private string Base => $"/api/notes/{Uri.EscapeDataString(PeerId)}/{Uri.EscapeDataString(ItemId)}";

    [RelayCommand]
    public async Task LoadAsync()
    {
        Loading = true;
        try { Apply(await _main.Api.GetAsync<NotesDto>(Base)); }
        catch (Exception ex) when (ex is HttpRequestException or Services.ApiException or TaskCanceledException) { ProblemText = "Notes can't be read right now."; }
        finally { Loading = false; }
    }

    private void Apply(NotesDto? dto)
    {
        if (dto is null) return;
        IsOwner = dto.IsOwner;
        ProblemText = dto.Problem switch
        {
            null => null,
            "offline" => $"{OwnerLabel} is offline: notes are kept on their PC." + (dto.FetchedAt is { } t ? $" Showing what was there {Format.Ago(t)}." : ""),
            "old" => $"{OwnerLabel}'s Ocean is older and has no notes yet. They show once it is updated.",
            "locked" => "Unlock this video to read and leave notes.",
            "gone" => "This video is no longer shared.",
            "refused" => null,
            _ => $"{OwnerLabel} can't be reached right now." + (dto.FetchedAt is { } t2 ? $" Showing what was there {Format.Ago(t2)}." : ""),
        };
        CanWrite = dto.Problem is null or "refused";
        if (dto.Error is { } err) WriteError = err;
        if (dto.Problem is null || dto.Notes.Count > 0 || !Loaded) _all = dto.Notes;
        if (dto.Problem is null) Count = _all.Count;
        Loaded = true;
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<NoteDto> q = Sort switch
        {
            SortMode.Newest => _all.OrderByDescending(n => n.CreatedAt),
            SortMode.Moment => _all.OrderBy(n => n.AtSeconds is null ? 1 : 0).ThenBy(n => n.AtSeconds ?? 0).ThenBy(n => n.CreatedAt),
            // Top: most useful first; a fresh note gets a little lift so it is seen before it can collect votes
            _ => _all.OrderByDescending(n => n.Up - n.Down + (DateTimeOffset.UtcNow - n.CreatedAt < TimeSpan.FromHours(6) ? 0.5 : 0)).ThenByDescending(n => n.CreatedAt),
        };
        var shown = q.Take(_shown).ToList();
        // keep the row objects that stay (expanded notes stay expanded, an edit in progress isn't lost)
        var byId = Notes.ToDictionary(n => n.Id);
        Notes.Clear();
        foreach (var dto in shown)
        {
            if (byId.TryGetValue(dto.Id, out var vm)) vm.Update(dto, IsOwner); else vm = new NoteVm(dto, IsOwner);
            Notes.Add(vm);
        }
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyText)); OnPropertyChanged(nameof(HasMore)); OnPropertyChanged(nameof(MoreText));
    }

    [RelayCommand] private void ShowMore() { _shown += Page; Rebuild(); }
    [RelayCommand] private void SetSort(string mode) => Sort = Enum.Parse<SortMode>(mode, true);

    // ------------------------------------------------------------------ writing

    [RelayCommand] private void RemoveDraftAt() { DraftAt = null; _atRemoved = true; }
    [RelayCommand] private void UseCurrentMoment() { if (CurrentPosition?.Invoke() is { } now) { DraftAt = now; _atRemoved = false; } }

    [RelayCommand]
    private async Task PostAsync()
    {
        if (!CanPost) return;
        Posting = true;
        try
        {
            var dto = await _main.Api.PostAsync<NotesDto>(Base, new NoteWriteDto { Text = Draft.Trim(), AtSeconds = DraftAt });
            var refused = dto?.Error is not null || dto?.Problem is not null and not "refused";
            Apply(dto);
            if (!refused)
            {
                Draft = "";
                if (Sort == SortMode.Top && _all.Count > 1) Sort = SortMode.Newest; // so the writer sees their note at the top
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or Services.ApiException or TaskCanceledException) { WriteError = "The note couldn't be sent. Try again."; }
        finally { Posting = false; }
    }

    [RelayCommand]
    private async Task Vote(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } arr || arr[0] is not NoteVm note || arr[1] is not string dir) return;
        if (note.Mine) return;
        var want = dir == "up" ? 1 : -1;
        var vote = note.MyVote == want ? 0 : want; // pressing the lit one takes it back
        note.Preview(vote);                        // shows at once; the owner's answer follows
        try { Apply(await _main.Api.PostAsync<NotesDto>($"{Base}/{Uri.EscapeDataString(note.Id)}/vote", new NoteVoteDto { Vote = vote })); }
        catch (Exception ex) when (ex is HttpRequestException or Services.ApiException or TaskCanceledException) { await LoadAsync(); }
    }

    [RelayCommand]
    private void Seek(NoteVm? note)
    {
        if (note?.AtSeconds is { } at) SeekTo?.Invoke(at);
    }

    [RelayCommand] private void BeginEdit(NoteVm? note) { if (note is { Mine: true }) { note.EditText = note.Text; note.IsEditing = true; } }
    [RelayCommand] private void CancelEdit(NoteVm? note) { if (note is not null) note.IsEditing = false; }

    [RelayCommand]
    private async Task SaveEdit(NoteVm? note)
    {
        if (note is null || note.EditText.Trim().Length == 0) return;
        try
        {
            var dto = await _main.Api.PostAsync<NotesDto>($"{Base}/{Uri.EscapeDataString(note.Id)}/edit", new NoteWriteDto { Text = note.EditText.Trim(), AtSeconds = note.AtSeconds });
            if (dto?.Error is null) note.IsEditing = false;
            Apply(dto);
        }
        catch (Exception ex) when (ex is HttpRequestException or Services.ApiException or TaskCanceledException) { WriteError = "The change couldn't be sent. Try again."; }
    }

    [RelayCommand]
    private async Task Delete(NoteVm? note)
    {
        if (note is null || !note.CanDelete) return;
        var whose = note.Mine ? "your note" : $"{note.Author}'s note";
        if (!await _main.Dialogs.ConfirmAsync("Remove the note?", $"Remove {whose}? Everyone stops seeing it.", "Remove", danger: true)) return;
        try { Apply(await _main.Api.PostAsync<NotesDto>($"{Base}/{Uri.EscapeDataString(note.Id)}/delete", null)); }
        catch (Exception ex) when (ex is HttpRequestException or Services.ApiException or TaskCanceledException) { WriteError = "The note couldn't be removed. Try again."; }
    }
}

/// <summary>One note in the list.</summary>
public partial class NoteVm : ObservableObject
{
    private const int FoldAt = 280;

    public NoteVm(NoteDto dto, bool viewerIsOwner) { Update(dto, viewerIsOwner); }

    public NoteDto Dto { get; private set; } = new();
    public string Id => Dto.Id;
    public bool Mine => Dto.Mine;
    public string Author => Dto.Mine ? "You" : Dto.AuthorName;
    public string AuthorTip => Dto.AuthorHost.Length > 0 ? $"{Dto.AuthorName} @ {Dto.AuthorHost}" : Dto.AuthorName;
    /// <summary>For the avatar colour: the writer's node id (the same writer always gets the same colour).</summary>
    public string AuthorId => Dto.AuthorId;
    public string Initials
    {
        get
        {
            var parts = Dto.AuthorName.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
            var s = Dto.AuthorName.Trim();
            return s.Length >= 2 ? s[..2].ToUpperInvariant() : s.ToUpperInvariant();
        }
    }
    public string Text => Dto.Text;
    public string When => Format.Ago(Dto.CreatedAt) + (Dto.EditedAt is not null ? "  ·  edited" : "");
    public string WhenTip => Dto.CreatedAt.ToLocalTime().ToString("f") + (Dto.EditedAt is { } e ? $"\nEdited {e.ToLocalTime():f}" : "");
    public double? AtSeconds => Dto.AtSeconds;
    public bool HasAt => Dto.AtSeconds is not null;
    public string AtText => Dto.AtSeconds is { } at ? Format.Duration(TimeSpan.FromSeconds(at)) : "";
    public int Up => Dto.Up;
    public int Down => Dto.Down;
    public int MyVote => Dto.MyVote;
    public bool IsUp => Dto.MyVote > 0;
    public bool IsDown => Dto.MyVote < 0;
    public bool CanVote => !Dto.Mine;
    public string UpTip => Dto.Mine ? "Votes on your note" : IsUp ? "You found this useful (click to take it back)" : "Useful";
    public string DownTip => Dto.Mine ? "Votes on your note" : IsDown ? "You voted this down (click to take it back)" : "Not useful";
    public bool CanEdit => Dto.Mine;
    public bool CanDelete { get; private set; }
    public bool HasMenu => CanEdit || CanDelete;

    /// <summary>Long notes are folded to a few lines until "more" is clicked.</summary>
    public bool IsLong => Dto.Text.Length > FoldAt || Dto.Text.Count(c => c == '\n') >= 4;
    [ObservableProperty] private bool _isExpanded;
    public bool IsFolded => IsLong && !IsExpanded;
    public string FoldText => IsExpanded ? "less" : "more";
    partial void OnIsExpandedChanged(bool value) { OnPropertyChanged(nameof(IsFolded)); OnPropertyChanged(nameof(FoldText)); }
    public bool ShowFold => IsLong && !IsEditing;
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(ShowFold));
    [RelayCommand] private void ToggleFold() => IsExpanded = !IsExpanded;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    public void Update(NoteDto dto, bool viewerIsOwner)
    {
        Dto = dto;
        CanDelete = dto.Mine || viewerIsOwner;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>My vote shows at once, before the owner confirms it.</summary>
    public void Preview(int vote)
    {
        var up = Dto.Up - (Dto.MyVote > 0 ? 1 : 0) + (vote > 0 ? 1 : 0);
        var down = Dto.Down - (Dto.MyVote < 0 ? 1 : 0) + (vote < 0 ? 1 : 0);
        Dto = new NoteDto
        {
            Id = Dto.Id, AuthorId = Dto.AuthorId, AuthorName = Dto.AuthorName, AuthorHost = Dto.AuthorHost, Text = Dto.Text, AtSeconds = Dto.AtSeconds,
            CreatedAt = Dto.CreatedAt, EditedAt = Dto.EditedAt, Up = up, Down = down, MyVote = vote, Mine = Dto.Mine,
        };
        OnPropertyChanged(string.Empty);
    }
}
