using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.App.Services;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>One item's change, for undo: what it was and what it became (null: not on the drawing).</summary>
public sealed record BoardChange(string Id, BoardItemDto? Before, BoardItemDto? After);

/// <summary>
/// The board of a screen sharing session, as this PC sees it (the session's host keeps the real one; see
/// ScreenBoard): two pages, the drawing and the text (<see cref="TextPage"/>), changes from everybody as they come,
/// and my own changes on their way. How the panel looks here (shown, which page, how see-through) is this PC's own
/// business.
/// Changes may come in any order (the answer to my change and the event of somebody else's): each item remembers
/// the version it came with, so an older change never undoes a newer one.
/// Undo and redo take back my own changes to the drawing (what I drew, changed, wiped away or cleared), never
/// somebody else's; a step that no longer applies (somebody wiped my stroke away meanwhile) is passed over.
/// </summary>
public sealed partial class BoardVm : ObservableObject
{
    public const string DrawPage = "draw", TextPageName = "text";
    private readonly Func<BoardOpDto, Task<BoardOpDto?>> _send;
    private readonly Func<Task<BoardDto?>> _load;
    private readonly Dictionary<string, BoardItemDto> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _itemRev = new(StringComparer.Ordinal);
    private readonly List<List<BoardChange>> _undo = new(), _redo = new();
    private long _clearRev;
    private bool _everShown, _stepping;

    public BoardVm(string myId, string myName, bool isHost, string title, Func<BoardOpDto, Task<BoardOpDto?>> send, Func<Task<BoardDto?>> load)
    {
        MyId = myId; MyName = myName; IsHost = isHost; Title = title; _send = send; _load = load;
        MyColor = "#" + Controls.InitialsBrushConverter.ColorOf(myId).ToString()[3..]; // (#AARRGGBB → #RRGGBB)
        var prefs = ScreenShareSettings.Load();
        _opacity = prefs.BoardOpacity;
        TextPage = new BoardTextVm(myId, send, load);
        TextPage.Typed += OnTyped;
        TextPage.Problem += p => { Problem = p; _ = ClearProblemLater(p); };
    }

    public string MyId { get; }
    public string MyName { get; }
    public string MyColor { get; }
    /// <summary>The one sharing.</summary>
    public bool IsHost { get; }
    public string Title { get; }
    public IEnumerable<BoardItemDto> Items => _items.Values;
    /// <summary>The text page everybody types in.</summary>
    public BoardTextVm TextPage { get; }

    /// <summary>Everything changed at once (loaded, cleared).</summary>
    public event Action? Reset;
    public event Action<BoardItemDto>? Put;
    public event Action<string>? Removed;
    /// <summary>Somebody else put something on the board for the first time while it was never shown here: show it.</summary>
    public event Action? Wanted;

    [ObservableProperty] private bool _visible;
    /// <summary>The page shown: <see cref="DrawPage"/> or <see cref="TextPageName"/>.</summary>
    [ObservableProperty] private string _page = DrawPage;
    /// <summary>Things others put on the board while it was hidden.</summary>
    [ObservableProperty] private int _unseen;
    /// <summary>A page changed by others while another one was shown (a dot on its tab).</summary>
    [ObservableProperty] private bool _drawUnseen;
    [ObservableProperty] private bool _textUnseen;
    /// <summary>How solid the panel's background is (0.25 to 1; what is on it is always fully visible).</summary>
    [ObservableProperty] private double _opacity;
    /// <summary>Why my last change didn't go through (null: fine).</summary>
    [ObservableProperty] private string? _problem;
    /// <summary>A short word at the bottom ("Gabe cleared the drawing"); with <see cref="NoticeUndo"/>, an Undo next to it.</summary>
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _noticeUndo;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    public string UnseenText => Unseen > 9 ? "9+" : Unseen.ToString();

    public bool OnDraw => Page == DrawPage;

    partial void OnVisibleChanged(bool value) { if (value) { Unseen = 0; _everShown = true; Seen(); } }
    partial void OnPageChanged(string value) { OnPropertyChanged(nameof(OnDraw)); Seen(); }
    private void Seen() { if (!Visible) return; if (Page == DrawPage) DrawUnseen = false; else TextUnseen = false; }
    partial void OnUnseenChanged(int value) => OnPropertyChanged(nameof(UnseenText));
    partial void OnOpacityChanged(double value)
    {
        var v = Math.Clamp(value, 0.25, 1);
        if (Math.Abs(v - value) > 0.001) { Opacity = v; return; }
        _saveOpacity?.Dispose();
        _saveOpacity = new Timer(_ => { var p = ScreenShareSettings.Load(); p.BoardOpacity = v; p.Save(); }, null, 800, Timeout.Infinite);
    }
    private Timer? _saveOpacity;

    public async Task LoadAsync()
    {
        try
        {
            if (await _load() is not { } b) return;
            _items.Clear(); _itemRev.Clear();
            foreach (var it in b.Items) { _items[it.Id] = it; _itemRev[it.Id] = b.Rev; }
            _clearRev = 0;
            Reset?.Invoke();
            TextPage.Load(b);
        }
        catch (Exception ex) { AppLog.Warn("Board", "the board can't be loaded", ex); }
    }

    /// <summary>A change, from anybody (also my own, coming back).</summary>
    public void Apply(BoardOpDto op)
    {
        if (op.Error is not null) return;
        switch (op.Op)
        {
            case BoardOpDto.Put when op.Item is { } it:
                PutOne(it, op.Rev);
                break;
            case BoardOpDto.Restore when op.Items is { } back:
                foreach (var it in back) PutOne(it, op.Rev);
                break;
            case BoardOpDto.Delete:
                foreach (var id in op.Ids ?? (op.Id is { } one ? new List<string> { one } : new List<string>()))
                {
                    if (_itemRev.TryGetValue(id, out var dr) && dr >= op.Rev) continue;
                    if (_items.Remove(id)) Removed?.Invoke(id);
                    _itemRev[id] = op.Rev; // (a late put of it must not bring it back)
                }
                break;
            case BoardOpDto.Clear:
                if (op.Rev <= _clearRev) return;
                _clearRev = op.Rev;
                foreach (var gone in _items.Where(kv => _itemRev.GetValueOrDefault(kv.Key) < op.Rev).Select(kv => kv.Key).ToList()) { _items.Remove(gone); _itemRev.Remove(gone); }
                Reset?.Invoke();
                if (op.ById != MyId && op.ById.Length > 0) Say($"{(string.IsNullOrEmpty(op.ByName) ? "Somebody" : op.ByName)} cleared the drawing");
                break;
            case BoardOpDto.Text or BoardOpDto.Caret:
                TextPage.Receive(op);
                break;
        }
    }

    private void PutOne(BoardItemDto it, long rev)
    {
        if (rev <= _clearRev || (_itemRev.TryGetValue(it.Id, out var r) && r >= rev)) return;
        var isNew = !_items.ContainsKey(it.Id);
        _items[it.Id] = it; _itemRev[it.Id] = rev;
        Put?.Invoke(it);
        if (isNew && it.AuthorId != MyId) Others(DrawPage);
    }

    /// <summary>Others changed a page: a dot on its tab when another page is shown, a number on the Board button while hidden, the board shown the first time.</summary>
    private void Others(string page)
    {
        var shown = Visible && Page == page;
        if (!shown)
        {
            var wasUnseen = page == DrawPage ? DrawUnseen : TextUnseen;
            if (page == DrawPage) DrawUnseen = true; else TextUnseen = true;
            // (the text: once per burst of typing, not per letter)
            if (!Visible && (page == DrawPage || !wasUnseen)) Unseen++;
        }
        if (!_everShown && !Visible) { _everShown = true; Page = page; Wanted?.Invoke(); }
    }

    private void OnTyped() => Others(TextPageName);

    /// <summary>My change: out to the session's host; what comes back is applied like everybody's. The answer (null: it didn't go through; <see cref="Problem"/> says why).</summary>
    public async Task<BoardOpDto?> SendAsync(BoardOpDto op)
    {
        try
        {
            var done = await _send(op);
            if (done is null || done.Error is not null)
            {
                var e = done?.Error;
                Problem = e is "unknown kind" or "unknown change" ? "The Ocean of the one sharing is older: update it there for this"
                    : e is { Length: > 0 } ? char.ToUpper(e[0]) + e[1..] : "It didn't reach the board";
                _ = ClearProblemLater(Problem);
                return null;
            }
            Problem = null;
            Apply(done);
            return done;
        }
        catch (Exception ex)
        {
            Problem = "It didn't reach the board (" + ex.Message + ")";
            _ = ClearProblemLater(Problem);
            return null;
        }
    }

    private async Task ClearProblemLater(string p) { await Task.Delay(5000); if (Problem == p) Problem = null; }

    public BoardItemDto? Find(string id) => _items.TryGetValue(id, out var it) ? it : null;

    // ------------------------------------------------------------------ undo, redo, clear (the drawing)

    /// <summary>I changed the drawing: one step to undo.</summary>
    public void Record(IEnumerable<BoardChange> changes)
    {
        var list = changes.ToList();
        if (list.Count == 0 || _stepping) return;
        _undo.Add(list);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        Stacks();
    }

    public void Record(BoardChange change) => Record(new[] { change });

    public Task UndoAsync() => StepAsync(_undo, _redo, back: true);
    public Task RedoAsync() => StepAsync(_redo, _undo, back: false);

    private async Task StepAsync(List<List<BoardChange>> from, List<List<BoardChange>> to, bool back)
    {
        if (_stepping) return;
        _stepping = true;
        try
        {
            while (from.Count > 0)
            {
                var step = from[^1]; from.RemoveAt(from.Count - 1);
                if (await MakeAsync(step, back)) { to.Add(step); break; }
            }
        }
        finally { _stepping = false; Stacks(); }
    }

    /// <summary>Makes each item what it was (<paramref name="back"/>) or what it became. False: nothing of it could be done.</summary>
    private async Task<bool> MakeAsync(List<BoardChange> step, bool back)
    {
        var delete = new List<string>();
        var restore = new List<BoardChange>();
        var put = new List<BoardItemDto>();
        foreach (var c in step)
        {
            var want = back ? c.Before : c.After;
            var now = Find(c.Id);
            if (want is null) { if (now is not null) delete.Add(c.Id); }
            else if (now is null) restore.Add(c);
            else if (now.AuthorId == MyId && !Same(now, want)) put.Add(want);
        }
        if (delete.Count + restore.Count + put.Count == 0) return false;
        var did = false;
        if (delete.Count > 0) did |= await SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = delete[0], Ids = delete }) is not null;
        if (restore.Count > 0)
        {
            // back as they were (by their author); mine that were different then are put as they were
            var done = await SendAsync(new BoardOpDto { Op = BoardOpDto.Restore, Ids = restore.Select(c => c.Id).ToList() });
            if (done is not null)
            {
                did |= done.Items is { Count: > 0 };
                var back2 = (done.Items ?? new()).ToDictionary(i => i.Id, StringComparer.Ordinal);
                foreach (var c in restore)
                {
                    var want = (back ? c.Before : c.After)!;
                    if (want.AuthorId != MyId) continue;
                    if (!back2.TryGetValue(c.Id, out var got) || !Same(got, want)) put.Add(want);
                }
            }
        }
        foreach (var it in put) did |= await SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = it }) is not null;
        return did;
    }

    /// <summary>Clears the drawing for everybody; Undo brings it back.</summary>
    public async Task ClearAsync()
    {
        if (_items.Count == 0) return;
        var before = _items.Values.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var done = await SendAsync(new BoardOpDto { Op = BoardOpDto.Clear });
        if (done is null) return;
        Record((done.Ids ?? before.Keys.ToList()).Where(before.ContainsKey).Select(id => new BoardChange(id, before[id], null)));
        Say("Drawing cleared for everybody", undo: true);
    }

    /// <summary>Clears the text page for everybody (undo brings it back); returns the change for the page to show.</summary>
    public TextOp? ClearText()
    {
        var op = TextPage.ClearAll();
        if (op is not null) Say("Text cleared for everybody", undo: true);
        return op;
    }

    private void Stacks() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }

    private static bool Same(BoardItemDto a, BoardItemDto b)
        => a.Kind == b.Kind && a.Shape == b.Shape && a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H && a.Size == b.Size && a.Color == b.Color && a.Text == b.Text
           && (a.Image?.Length ?? 0) == (b.Image?.Length ?? 0) && (a.Points ?? new()).SequenceEqual(b.Points ?? new());

    // ------------------------------------------------------------------ a short word at the bottom

    private int _noticeSeq;

    public void Say(string text, bool undo = false)
    {
        Notice = text; NoticeUndo = undo;
        var mine = ++_noticeSeq;
        _ = Task.Delay(undo ? 6000 : 4000).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_noticeSeq == mine) { Notice = null; NoticeUndo = false; }
        }));
    }

    public void Unsay() { _noticeSeq++; Notice = null; NoticeUndo = false; }
}
