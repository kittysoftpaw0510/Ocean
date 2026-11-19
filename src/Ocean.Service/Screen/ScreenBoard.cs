using Ocean.Core.Protocol;

namespace Ocean.Service.Screen;

/// <summary>
/// The board of one screen sharing session, kept by its host: everybody in the session (the host and its viewers)
/// draws, writes and pastes on it, and every change goes to everybody (viewers follow <see cref="Since"/>). Gone
/// with the session. Two pages:
/// The drawing: an item is changed only by its author; anybody may wipe one away (the eraser) or clear them all.
/// What goes is kept for a while, so whoever wiped it can bring it back (undo: restore), as it was, by its author.
/// The text: one text everybody types in at once. Edits come with the version they were made on; one made on an
/// older version is transformed past the edits that came in between (<see cref="TextOp.Transform"/>), so nobody's
/// typing is lost and everybody ends up with the same text.
/// </summary>
public sealed class ScreenBoard
{
    private const int KeepOps = 2000, KeepTextEdits = 1000, KeepGone = 3000;
    private const long KeepGoneBytes = 60_000_000;
    private readonly object _lock = new();
    private readonly Dictionary<string, BoardItemDto> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly List<BoardOpDto> _log = new();
    // wiped away (deleted, cleared), the newest last: what restore brings back
    private readonly Dictionary<string, BoardItemDto> _gone = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _goneOrder = new();
    private long _goneBytes;
    private long _rev, _bytes;
    private string _text = "";
    private long _textRev;
    private readonly List<(long Rev, TextOp Edit)> _textLog = new();
    private bool _closed;
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sid;

    public ScreenBoard(string sessionId) => _sid = sessionId;

    public bool Closed { get { lock (_lock) return _closed; } }
    public int Count { get { lock (_lock) return _items.Count; } }

    public BoardDto State()
    {
        lock (_lock) return new BoardDto { SessionId = _sid, Rev = _rev, Items = _order.Select(id => _items[id]).ToList(), Text = _text, TextRev = _textRev };
    }

    /// <summary>A change by <paramref name="byId"/>. Returns it as it was done (with its version), or one with <see cref="BoardOpDto.Error"/> set.</summary>
    public BoardOpDto Apply(BoardOpDto op, string byId, string byName, bool host)
    {
        BoardOpDto Refuse(string why) => new() { Op = op.Op, Id = op.Id ?? op.Item?.Id, SessionId = _sid, Error = why };
        TaskCompletionSource wake;
        BoardOpDto done;
        lock (_lock)
        {
            if (_closed) return Refuse("the sharing is over");
            BoardOpDto Log(BoardOpDto o) { o.Rev = ++_rev; o.SessionId = _sid; o.ById = byId; o.ByName = byName; _log.Add(o); return o; }
            switch (op.Op)
            {
                case BoardOpDto.Put:
                {
                    if (op.Item is not { } it || it.Id is not { Length: > 0 and <= 64 }) return Refuse("no item");
                    if (it.Kind is not ("ink" or "shape" or "text" or "image")) return Refuse("unknown kind");
                    if (it.Kind == "shape" && (!BoardLimits.Shapes.Contains(it.Shape) || it.Points is not { Count: 4 })) return Refuse("not a shape");
                    if (it.Points is { Count: > BoardLimits.MaxPoints }) return Refuse("the stroke is too long");
                    if (it.Text is { Length: > BoardLimits.MaxText }) return Refuse("the text is too long");
                    if (it.Image is { Length: > BoardLimits.MaxImage }) return Refuse("the picture is too large");
                    if (_items.TryGetValue(it.Id, out var old) && old.AuthorId != byId) return Refuse("only its author can change it");
                    if (old is null && _items.Count >= BoardLimits.MaxItems) return Refuse("the board is full (clear it first)");
                    var size = Size(it) - (old is null ? 0 : Size(old));
                    if (_bytes + size > BoardLimits.MaxBytes) return Refuse("the board is full (clear it first)");
                    it.AuthorId = byId; it.AuthorName = byName;
                    it.X = Math.Clamp(it.X, -BoardLimits.Width, 2 * BoardLimits.Width);
                    it.Y = Math.Clamp(it.Y, -BoardLimits.Width, 1_000_000);
                    if (old is null) { _order.Add(it.Id); Revive(it.Id); }
                    _items[it.Id] = it;
                    _bytes += size;
                    done = Log(new BoardOpDto { Op = BoardOpDto.Put, Item = it });
                    break;
                }
                case BoardOpDto.Delete:
                {
                    var ids = op.Ids ?? (op.Id is { } one ? new List<string> { one } : new List<string>());
                    var gone = new List<string>();
                    foreach (var id in ids.Distinct().Take(BoardLimits.MaxItems))
                    {
                        if (!Remove(id)) continue;
                        Log(new BoardOpDto { Op = BoardOpDto.Delete, Id = id });
                        gone.Add(id);
                    }
                    if (gone.Count == 0) return Refuse("not there");
                    done = new BoardOpDto { Op = BoardOpDto.Delete, Id = gone[0], Ids = gone, Rev = _rev, SessionId = _sid, ById = byId, ByName = byName };
                    break;
                }
                case BoardOpDto.Clear:
                {
                    var gone = _order.ToList();
                    foreach (var id in gone) Remove(id);
                    // (everybody gets the clear; the one who cleared also which items went, to bring them back)
                    done = Log(new BoardOpDto { Op = BoardOpDto.Clear });
                    done = new BoardOpDto { Op = BoardOpDto.Clear, Ids = gone, Rev = done.Rev, SessionId = _sid, ById = byId, ByName = byName };
                    break;
                }
                case BoardOpDto.Restore:
                {
                    var back = new List<BoardItemDto>();
                    foreach (var id in (op.Ids ?? new()).Distinct().Take(BoardLimits.MaxItems))
                    {
                        if (_items.ContainsKey(id) || !_gone.TryGetValue(id, out var it)) continue;
                        if (_items.Count >= BoardLimits.MaxItems || _bytes + Size(it) > BoardLimits.MaxBytes) break;
                        Revive(id);
                        _items[id] = it; _order.Add(id); _bytes += Size(it);
                        Log(new BoardOpDto { Op = BoardOpDto.Put, Item = it });
                        back.Add(it);
                    }
                    done = new BoardOpDto { Op = BoardOpDto.Restore, Items = back, Ids = back.Select(i => i.Id).ToList(), Rev = _rev, SessionId = _sid, ById = byId, ByName = byName };
                    break;
                }
                case BoardOpDto.Text:
                {
                    if (TextOp.FromWire(op.Edit) is not { } edit) return Refuse("not an edit");
                    if (op.Base > _textRev || op.Base < _textRev - _textLog.Count) return Refuse("resync");
                    // past the edits made since (theirs first where both typed at the same place: they were here first)
                    var caret = op.CaretAt;
                    try
                    {
                        foreach (var (rev, before) in _textLog)
                        {
                            if (rev <= op.Base) continue;
                            var (mine, theirs) = TextOp.Transform(edit, before);
                            edit = mine;
                            if (caret is { } c) caret = theirs.TransformIndex(c);
                        }
                        if (edit.BaseLength != _text.Length) return Refuse("resync");
                        if (edit.TargetLength > BoardLimits.MaxPage) return Refuse("the text is too long for the board");
                        _text = edit.Apply(_text);
                    }
                    catch (InvalidOperationException) { return Refuse("resync"); }
                    _textRev++;
                    _textLog.Add((_textRev, edit));
                    if (_textLog.Count > KeepTextEdits) _textLog.RemoveRange(0, _textLog.Count - KeepTextEdits);
                    done = Log(new BoardOpDto { Op = BoardOpDto.Text, Id = op.Id, Edit = edit.ToWire(), Base = _textRev - 1, TextRev = _textRev, CaretAt = caret is { } k ? Math.Clamp(k, 0, _text.Length) : null });
                    break;
                }
                case BoardOpDto.Caret:
                {
                    if (op.CaretAt is not { } c || op.Base > _textRev || op.Base < _textRev - _textLog.Count) return Refuse("resync");
                    foreach (var (rev, edit) in _textLog) if (rev > op.Base) c = edit.TransformIndex(c);
                    done = Log(new BoardOpDto { Op = BoardOpDto.Caret, Base = _textRev, TextRev = _textRev, CaretAt = Math.Clamp(c, 0, _text.Length) });
                    break;
                }
                default:
                    return Refuse("unknown change");
            }
            if (_log.Count > KeepOps) _log.RemoveRange(0, _log.Count - KeepOps);
            wake = _more; _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
        return done;
    }

    /// <summary>An item off the drawing, kept a while (for restore).</summary>
    private bool Remove(string id)
    {
        if (!_items.Remove(id, out var it)) return false;
        _order.Remove(id);
        _bytes -= Size(it);
        Revive(id);
        _gone[id] = it; _goneOrder.AddLast(id); _goneBytes += Size(it);
        while (_goneOrder.Count > KeepGone || _goneBytes > KeepGoneBytes)
        {
            var oldest = _goneOrder.First!.Value; _goneOrder.RemoveFirst();
            if (_gone.Remove(oldest, out var o)) _goneBytes -= Size(o);
        }
        return true;
    }

    /// <summary>No longer among the wiped away (it is back).</summary>
    private void Revive(string id)
    {
        if (!_gone.Remove(id, out var it)) return;
        _goneOrder.Remove(id);
        _goneBytes -= Size(it);
    }

    private static long Size(BoardItemDto it) => 200 + (it.Points?.Count ?? 0) * 8L + (it.Text?.Length ?? 0) * 2L + (it.Image?.Length ?? 0);

    /// <summary>The changes after version <paramref name="rev"/>; null when they are no longer kept (start over from <see cref="State"/>).</summary>
    public List<BoardOpDto>? Since(long rev)
    {
        lock (_lock)
        {
            if (rev >= _rev) return new();
            if (_log.Count == 0 || _log[0].Rev > rev + 1) return null;
            return _log.Where(o => o.Rev > rev).ToList();
        }
    }

    public Task More() { lock (_lock) return _more.Task; }

    public void Close() { TaskCompletionSource wake; lock (_lock) { _closed = true; wake = _more; } wake.TrySetResult(); }
}
