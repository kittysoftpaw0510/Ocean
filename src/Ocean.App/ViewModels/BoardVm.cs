using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.App.Services;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>
/// The board of a screen sharing session, as this PC sees it (the session's host keeps the real one; see
/// ScreenBoard): its items, changes from everybody as they come, and my own changes on their way. How the panel
/// looks here (shown, how see-through) is this PC's own business.
/// Changes may come in any order (the answer to my change and the event of somebody else's): each item remembers
/// the version it came with, so an older change never undoes a newer one.
/// </summary>
public sealed partial class BoardVm : ObservableObject
{
    private readonly Func<BoardOpDto, Task<BoardOpDto?>> _send;
    private readonly Func<Task<BoardDto?>> _load;
    private readonly Dictionary<string, BoardItemDto> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _itemRev = new(StringComparer.Ordinal);
    private long _clearRev;
    private bool _everShown;

    public BoardVm(string myId, string myName, bool isHost, string title, Func<BoardOpDto, Task<BoardOpDto?>> send, Func<Task<BoardDto?>> load)
    {
        MyId = myId; MyName = myName; IsHost = isHost; Title = title; _send = send; _load = load;
        MyColor = "#" + Controls.InitialsBrushConverter.ColorOf(myId).ToString()[3..]; // (#AARRGGBB → #RRGGBB)
        var prefs = ScreenShareSettings.Load();
        _opacity = prefs.BoardOpacity;
    }

    public string MyId { get; }
    public string MyName { get; }
    public string MyColor { get; }
    /// <summary>The one sharing: may clear the board.</summary>
    public bool IsHost { get; }
    public string Title { get; }
    public IEnumerable<BoardItemDto> Items => _items.Values;

    /// <summary>Everything changed at once (loaded, cleared).</summary>
    public event Action? Reset;
    public event Action<BoardItemDto>? Put;
    public event Action<string>? Removed;
    /// <summary>Somebody else put something on the board for the first time while it was never shown here: show it.</summary>
    public event Action? Wanted;

    [ObservableProperty] private bool _visible;
    /// <summary>Things others put on the board while it was hidden.</summary>
    [ObservableProperty] private int _unseen;
    /// <summary>How solid the panel's background is (0.25 to 1; what is on it is always fully visible).</summary>
    [ObservableProperty] private double _opacity;
    /// <summary>Why my last change didn't go through (null: fine).</summary>
    [ObservableProperty] private string? _problem;
    public string UnseenText => Unseen > 9 ? "9+" : Unseen.ToString();

    partial void OnVisibleChanged(bool value) { if (value) { Unseen = 0; _everShown = true; } }
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
                if (op.Rev <= _clearRev || (_itemRev.TryGetValue(it.Id, out var r) && r >= op.Rev)) return;
                var isNew = !_items.ContainsKey(it.Id);
                _items[it.Id] = it; _itemRev[it.Id] = op.Rev;
                Put?.Invoke(it);
                if (isNew && it.AuthorId != MyId)
                {
                    if (!Visible) Unseen++;
                    if (!_everShown && !Visible) { _everShown = true; Wanted?.Invoke(); }
                }
                break;
            case BoardOpDto.Delete when op.Id is { } id:
                if (_itemRev.TryGetValue(id, out var dr) && dr >= op.Rev) return;
                if (_items.Remove(id)) Removed?.Invoke(id);
                _itemRev[id] = op.Rev; // (a late put of it must not bring it back)
                break;
            case BoardOpDto.Clear:
                if (op.Rev <= _clearRev) return;
                _clearRev = op.Rev;
                foreach (var gone in _items.Where(kv => _itemRev.GetValueOrDefault(kv.Key) < op.Rev).Select(kv => kv.Key).ToList()) { _items.Remove(gone); _itemRev.Remove(gone); }
                Reset?.Invoke();
                break;
        }
    }

    /// <summary>My change: out to the session's host; what comes back is applied like everybody's.</summary>
    public async Task<bool> SendAsync(BoardOpDto op)
    {
        try
        {
            var done = await _send(op);
            if (done is null || done.Error is not null)
            {
                Problem = done?.Error is { } e ? char.ToUpper(e[0]) + e[1..] : "It didn't reach the board";
                _ = ClearProblemLater(Problem);
                return false;
            }
            Problem = null;
            Apply(done);
            return true;
        }
        catch (Exception ex)
        {
            Problem = "It didn't reach the board (" + ex.Message + ")";
            _ = ClearProblemLater(Problem);
            return false;
        }
    }

    private async Task ClearProblemLater(string p) { await Task.Delay(5000); if (Problem == p) Problem = null; }

    public BoardItemDto? Find(string id) => _items.TryGetValue(id, out var it) ? it : null;
}
