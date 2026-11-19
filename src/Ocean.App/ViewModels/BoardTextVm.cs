using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.App.Services;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>Somebody else's caret on the text page, where it is in my text.</summary>
public sealed class BoardCaret
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public int Index { get; set; }
    /// <summary>When they last typed (their name shows a moment), and when their caret was last heard of.</summary>
    public DateTime LastEdit { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// The board's text page, as this PC sees it: one text everybody types in at once, each change shown to everybody
/// as it is typed. My typing goes to the session's host one change at a time (the next ones wait, joined into one),
/// and the host puts everybody's changes in order; a change of somebody else's that comes meanwhile is transformed
/// past mine (<see cref="TextOp.Transform"/>), so nothing typed is lost and the text ends up the same everywhere.
/// Undo and redo take back my own changes only, also when others typed since.
/// </summary>
public sealed partial class BoardTextVm : ObservableObject
{
    private readonly string _myId;
    private readonly Func<BoardOpDto, Task<BoardOpDto?>> _send;
    private readonly Func<Task<BoardDto?>> _load;
    private string _text = "";
    private long _rev;
    // my change on its way to the host, and what I typed meanwhile
    private TextOp? _inflight, _buffer;
    private string? _inflightId;
    private int _caret;
    // changes from the host that came before the ones before them
    private readonly SortedDictionary<long, BoardOpDto> _ahead = new();
    private DateTime _gapSince;
    // the host's latest changes (a caret said for an earlier text is moved along them)
    private readonly List<(long Rev, TextOp Edit)> _recent = new();
    private readonly List<TextOp> _undo = new(), _redo = new();
    private DateTime _lastLocal, _undoStarted;
    private bool _stepping;
    private readonly Dictionary<string, BoardCaret> _carets = new(StringComparer.Ordinal);
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public BoardTextVm(string myId, Func<BoardOpDto, Task<BoardOpDto?>> send, Func<Task<BoardDto?>> load)
    {
        _myId = myId; _send = send; _load = load;
        _clock.Tick += (_, _) => Tick();
    }

    public string Text => _text;
    public IEnumerable<BoardCaret> Carets => _carets.Values;

    /// <summary>The host's Ocean has a text page (an older one hasn't).</summary>
    [ObservableProperty] private bool _supported = true;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    /// <summary>"Gabe is typing…" (null: nobody).</summary>
    [ObservableProperty] private string? _typing;
    [ObservableProperty] private string? _typingColor;

    /// <summary>A change of somebody else's (or my undo) applied to <see cref="Text"/>: the page shows it, keeping my caret.</summary>
    public event Action<TextOp>? Changed;
    /// <summary>All of the text is new (loaded again).</summary>
    public event Action? Replaced;
    public event Action? CaretsMoved;
    /// <summary>Somebody else typed something (the board may show its page).</summary>
    public event Action? Typed;
    /// <summary>Why something didn't work.</summary>
    public event Action<string>? Problem;

    // ------------------------------------------------------------------ the whole text

    public void Load(BoardDto b)
    {
        if (b.Text is null) { Supported = false; return; }
        Supported = true;
        _text = b.Text; _rev = b.TextRev;
        _inflight = _buffer = null; _inflightId = null;
        foreach (var k in _ahead.Keys.Where(k => k <= _rev).ToList()) _ahead.Remove(k);
        _recent.Clear(); _undo.Clear(); _redo.Clear(); Stacks();
        foreach (var c in _carets.Values) c.Index = Math.Min(c.Index, _text.Length);
        Replaced?.Invoke();
        Drain();
    }

    private async void Resync(string why)
    {
        AppLog.Info("Board", "the text page is loaded again: " + why);
        try { if (await _load() is { } b) Load(b); }
        catch (Exception ex) { AppLog.Warn("Board", "the text page can't be loaded", ex); }
    }

    // ------------------------------------------------------------------ my typing

    /// <summary>I changed the text (the page already shows it); <paramref name="caret"/>: where my caret is now.</summary>
    public void Local(TextOp op, int caret)
    {
        if (!Supported || op.IsNoop) return;
        var before = _text;
        _text = op.Apply(_text);
        foreach (var c in _carets.Values) c.Index = op.TransformIndex(c.Index);
        _caret = caret;
        if (!_stepping)
        {
            // typing in one go is undone in one go (a new line, a pause or a long stretch starts another step)
            var inverse = op.Invert(before);
            var now = DateTime.UtcNow;
            var newLine = op.Parts.Any(p => p.IsInsert && p.S!.Contains('\n'));
            if (_undo.Count > 0 && now - _lastLocal < TimeSpan.FromSeconds(1) && now - _undoStarted < TimeSpan.FromSeconds(5) && !newLine)
                _undo[^1] = inverse.Compose(_undo[^1]);
            else { _undo.Add(inverse); _undoStarted = now; if (_undo.Count > 200) _undo.RemoveAt(0); }
            _lastLocal = newLine ? DateTime.MinValue : now;
            _redo.Clear();
            Stacks();
        }
        if (_inflight is null) SendInflight(op);
        else _buffer = _buffer is null ? op : _buffer.Compose(op);
        CaretsMoved?.Invoke();
    }

    /// <summary>My caret moved (without typing): the others see where (only while nothing of mine is on its way: a change carries it).</summary>
    public void MoveCaret(int caret)
    {
        if (!Supported || caret == _caret) return;
        _caret = caret;
        _caretDue = true;
        if (!_clock.IsEnabled) _clock.Start();
    }
    private bool _caretDue;
    private DateTime _caretSent;

    private void SendCaret()
    {
        _caretDue = false;
        _caretSent = DateTime.UtcNow;
        _ = _send(new BoardOpDto { Op = BoardOpDto.Caret, Base = _rev, CaretAt = _caret });
    }

    private async void SendInflight(TextOp op)
    {
        _inflight = op;
        var id = _inflightId = Guid.NewGuid().ToString("N")[..12];
        BoardOpDto? done;
        try { done = await _send(new BoardOpDto { Op = BoardOpDto.Text, Id = id, Edit = op.ToWire(), Base = _rev, CaretAt = _caret }); }
        catch (Exception ex) { done = new BoardOpDto { Error = ex.Message }; }
        if (_inflightId != id) return; // (loaded again meanwhile)
        if (done is { Error: null }) { Receive(done); return; }
        var why = done?.Error ?? "no answer";
        if (why is "unknown change") { Supported = false; return; }
        if (why != "resync") Problem?.Invoke(char.ToUpper(why[0]) + why[1..]);
        Resync(why);
    }

    // ------------------------------------------------------------------ the host's changes

    /// <summary>A change to the text from the host (somebody's, mine coming back), or where somebody's caret is.</summary>
    public void Receive(BoardOpDto op)
    {
        if (op.Op == BoardOpDto.Caret) { OtherCaret(op); return; }
        if (op.TextRev <= _rev) return;
        _ahead[op.TextRev] = op;
        Drain();
    }

    private void Drain()
    {
        while (_ahead.Remove(_rev + 1, out var next)) Process(next);
        if (_ahead.Count == 0) _gapSince = default;
        else
        {
            // one is missing (it comes on another way, or got lost): a while, then all of the text again
            if (_gapSince == default) _gapSince = DateTime.UtcNow;
            if (!_clock.IsEnabled) _clock.Start();
        }
    }

    private void Process(BoardOpDto op)
    {
        if (TextOp.FromWire(op.Edit) is not { } edit) { Resync("an edit that can't be read"); return; }
        if (_inflightId is not null && op.Id == _inflightId)
        {
            // mine, done
            _rev = op.TextRev; Remember(op.TextRev, edit);
            _inflight = null; _inflightId = null;
            if (_buffer is { } next) { _buffer = null; SendInflight(next); }
            return;
        }
        try
        {
            var s = edit;
            if (_inflight is not null) (_inflight, s) = TextOp.Transform(_inflight, s);
            if (_buffer is not null) (_buffer, s) = TextOp.Transform(_buffer, s);
            _text = s.Apply(_text);
            _rev = op.TextRev; Remember(op.TextRev, edit);
            Transform(_undo, s); Transform(_redo, s); Stacks();
            foreach (var c in _carets.Values) c.Index = s.TransformIndex(c.Index);
            if (op.ById != _myId && op.ById.Length > 0)
            {
                var c = Caret(op.ById, op.ByName);
                c.LastEdit = c.LastSeen = DateTime.UtcNow;
                if (op.CaretAt is { } at) c.Index = Math.Clamp(Mine(at), 0, _text.Length);
                Typed?.Invoke();
            }
            Changed?.Invoke(s);
            CaretsMoved?.Invoke();
            Who();
            if (!_clock.IsEnabled) _clock.Start();
        }
        catch (InvalidOperationException ex) { Resync(ex.Message); }
    }

    private void Remember(long rev, TextOp edit) { _recent.Add((rev, edit)); if (_recent.Count > 200) _recent.RemoveAt(0); }

    /// <summary>A place in the host's text, in mine (past what of mine is still on its way).</summary>
    private int Mine(int at)
    {
        if (_inflight is not null) at = _inflight.TransformIndex(at);
        if (_buffer is not null) at = _buffer.TransformIndex(at);
        return at;
    }

    private void OtherCaret(BoardOpDto op)
    {
        if (op.ById == _myId || op.CaretAt is not { } at || op.TextRev > _rev) return;
        if (op.TextRev < _rev)
        {
            if (_recent.Count == 0 || _recent[0].Rev > op.TextRev + 1) return;
            foreach (var (rev, edit) in _recent) if (rev > op.TextRev) at = edit.TransformIndex(at);
        }
        var c = Caret(op.ById, op.ByName);
        c.Index = Math.Clamp(Mine(at), 0, _text.Length);
        c.LastSeen = DateTime.UtcNow;
        CaretsMoved?.Invoke();
        if (!_clock.IsEnabled) _clock.Start();
    }

    private BoardCaret Caret(string id, string name)
    {
        if (!_carets.TryGetValue(id, out var c)) _carets[id] = c = new BoardCaret { Id = id };
        c.Name = string.IsNullOrEmpty(name) ? "Somebody" : name;
        return c;
    }

    private static void Transform(List<TextOp> stack, TextOp op)
    {
        // from the newest down: each one is for the text the one above it leaves
        var result = new List<TextOp>(stack.Count);
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            var (e, o) = TextOp.Transform(stack[i], op);
            if (!e.IsNoop) result.Add(e);
            op = o;
        }
        result.Reverse();
        stack.Clear(); stack.AddRange(result);
    }

    // ------------------------------------------------------------------ undo, redo, clear

    /// <summary>Takes back my last change (null: nothing to take back). The text is changed; the page shows the change it returns.</summary>
    public TextOp? Undo() => Step(_undo, _redo);
    public TextOp? Redo() => Step(_redo, _undo);

    private TextOp? Step(List<TextOp> from, List<TextOp> to)
    {
        if (!Supported) return null;
        while (from.Count > 0)
        {
            var op = from[^1]; from.RemoveAt(from.Count - 1);
            if (op.IsNoop) continue;
            to.Add(op.Invert(_text));
            _stepping = true;
            try { Local(op, EndOf(op)); }
            finally { _stepping = false; }
            _lastLocal = DateTime.MinValue;
            Stacks();
            return op;
        }
        Stacks();
        return null;
    }

    /// <summary>Everything on the page goes (undo brings it back). Returns the change (null: it was empty).</summary>
    public TextOp? ClearAll()
    {
        if (!Supported || _text.Length == 0) return null;
        var op = TextOp.Splice(_text.Length, 0, _text.Length, "");
        _lastLocal = DateTime.MinValue;
        Local(op, 0);
        _lastLocal = DateTime.MinValue;
        return op;
    }

    /// <summary>Where a change ends (a caret after it).</summary>
    public static int EndOf(TextOp op)
    {
        int at = 0, end = 0;
        foreach (var p in op.Parts)
        {
            if (p.IsKeep) at += p.N;
            else if (p.IsInsert) { at += p.S!.Length; end = at; }
            else end = at;
        }
        return end;
    }

    private void Stacks() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }

    // ------------------------------------------------------------------ who is typing

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (_caretDue && _inflight is null && now - _caretSent > TimeSpan.FromMilliseconds(250)) SendCaret();
        if (_gapSince != default && now - _gapSince > TimeSpan.FromSeconds(5)) { _gapSince = default; _ahead.Clear(); Resync("a change didn't come"); }
        // carets of people gone quiet for long go
        var stale = _carets.Values.Where(c => now - c.LastSeen > TimeSpan.FromMinutes(3)).Select(c => c.Id).ToList();
        foreach (var id in stale) _carets.Remove(id);
        Who();
        CaretsMoved?.Invoke();
        if (!_caretDue && _gapSince == default && Typing is null) _clock.Stop();
    }

    private void Who()
    {
        var now = DateTime.UtcNow;
        var typing = _carets.Values.Where(c => now - c.LastEdit < TimeSpan.FromSeconds(2.5)).OrderByDescending(c => c.LastEdit).ToList();
        Typing = typing.Count switch
        {
            0 => null,
            1 => $"{typing[0].Name} is typing…",
            2 => $"{typing[0].Name} and {typing[1].Name} are typing…",
            _ => $"{typing[0].Name} and {typing.Count - 1} others are typing…",
        };
        TypingColor = typing.Count > 0 ? "#" + Controls.InitialsBrushConverter.ColorOf(typing[0].Id).ToString()[3..] : null;
    }
}
