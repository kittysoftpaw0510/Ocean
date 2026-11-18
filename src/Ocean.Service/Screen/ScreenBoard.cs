using Ocean.Core.Protocol;

namespace Ocean.Service.Screen;

/// <summary>
/// The board of one screen sharing session, kept by its host: everybody in the session (the host and its viewers)
/// writes, draws and pastes on it, and every change goes to everybody (viewers follow <see cref="Since"/>). An item
/// is changed only by its author; anybody may wipe one away (the eraser); clearing everything is the host's. Gone
/// with the session.
/// </summary>
public sealed class ScreenBoard
{
    private const int KeepOps = 400;
    private readonly object _lock = new();
    private readonly Dictionary<string, BoardItemDto> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly List<BoardOpDto> _log = new();
    private long _rev, _bytes;
    private bool _closed;
    private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sid;

    public ScreenBoard(string sessionId) => _sid = sessionId;

    public bool Closed { get { lock (_lock) return _closed; } }
    public int Count { get { lock (_lock) return _items.Count; } }

    public BoardDto State()
    {
        lock (_lock) return new BoardDto { SessionId = _sid, Rev = _rev, Items = _order.Select(id => _items[id]).ToList() };
    }

    /// <summary>A change by <paramref name="byId"/>. Returns it as everybody gets it (with its version), or one with <see cref="BoardOpDto.Error"/> set.</summary>
    public BoardOpDto Apply(BoardOpDto op, string byId, string byName, bool host)
    {
        BoardOpDto Refuse(string why) => new() { Op = op.Op, Id = op.Id ?? op.Item?.Id, SessionId = _sid, Error = why };
        TaskCompletionSource wake;
        BoardOpDto done;
        lock (_lock)
        {
            if (_closed) return Refuse("the sharing is over");
            switch (op.Op)
            {
                case BoardOpDto.Put:
                {
                    if (op.Item is not { } it || it.Id is not { Length: > 0 and <= 64 }) return Refuse("no item");
                    if (it.Kind is not ("ink" or "text" or "image")) return Refuse("unknown kind");
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
                    if (old is null) _order.Add(it.Id);
                    _items[it.Id] = it;
                    _bytes += size;
                    break;
                }
                case BoardOpDto.Delete:
                {
                    if (op.Id is null || !_items.Remove(op.Id, out var gone)) return Refuse("not there");
                    _order.Remove(op.Id);
                    _bytes -= Size(gone);
                    break;
                }
                case BoardOpDto.Clear:
                    if (!host) return Refuse("only the one sharing can clear the board");
                    _items.Clear(); _order.Clear(); _bytes = 0;
                    break;
                default:
                    return Refuse("unknown change");
            }
            done = new BoardOpDto { Op = op.Op, Item = op.Op == BoardOpDto.Put ? op.Item : null, Id = op.Op == BoardOpDto.Delete ? op.Id : null, Rev = ++_rev, SessionId = _sid, ById = byId, ByName = byName };
            _log.Add(done);
            if (_log.Count > KeepOps) _log.RemoveRange(0, _log.Count - KeepOps);
            wake = _more; _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
        return done;
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
