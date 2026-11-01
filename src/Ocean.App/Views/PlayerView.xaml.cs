using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

public partial class PlayerView : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _osd = new() { Interval = TimeSpan.FromSeconds(1.1) };
    private PlayerViewModel? _vm;
    private bool _dragging; // the seek bar's thumb is being dragged
    private bool _pressed;  // the mouse is down on the seek bar: leave the bar where the user put it
    private bool _opened;   // the current video opened (a resume point read before that would be 0)
    private bool _ended;    // reached the end and rewound: keep "watched" instead of recording 0
    private bool _syncing;  // a multicast follower being kept in step (not the user seeking)

    // A jump the player hasn't carried out yet. Over a network stream the player can drop a position change (it
    // arrives while it is still opening or fetching another part), so the jump is checked and repeated until the
    // video is really there; meanwhile the bar shows the target instead of snapping back.
    private double? _seekTarget;
    private double _seekFrom;        // where the player was when the jump was asked for
    private double? _queuedSeek;     // asked for before the video opened
    private DateTime _seekStarted, _seekIssued;
    private int _seekTries;

    public PlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnVisibleChanged;
        _tick.Tick += (_, _) => UpdatePosition();
        _idle.Tick += (_, _) => { if (_vm is { IsFullscreen: true, IsPlaying: true }) SetChrome(false); };
        _osd.Tick += (_, _) => { _osd.Stop(); SpeedOsd.Visibility = Visibility.Collapsed; };
        MouseMove += (_, _) => { SetChrome(true); _idle.Stop(); _idle.Start(); };
        PreviewKeyDown += OnKey;
        // The slider handles a click on its track itself (it jumps the thumb there), so listen to handled clicks too.
        Seek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_MouseDown), handledEventsToo: true);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = e.NewValue as PlayerViewModel;
        if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) { Focus(); Keyboard.Focus(this); }
        else Stop();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Generation):
                Start();
                break;
            case nameof(PlayerViewModel.Volume):
                Media.Volume = _vm.Volume;
                break;
            case nameof(PlayerViewModel.IsMuted):
                Media.IsMuted = _vm.IsMuted;
                break;
            case nameof(PlayerViewModel.LoopA):
            case nameof(PlayerViewModel.LoopB):
            case nameof(PlayerViewModel.DurationSeconds):
                UpdateLoopMarks();
                // check the position more often while looping so the jump back happens right at B
                _tick.Interval = TimeSpan.FromMilliseconds(_vm.LoopActive ? 100 : 250);
                break;
            case nameof(PlayerViewModel.Speed):
                ApplySpeed();
                ShowOsd(_vm.Speed == 1.0 ? "Normal speed" : _vm.SpeedText);
                break;
            case nameof(PlayerViewModel.IsFullscreen):
                SetChrome(true);
                break;
            case nameof(PlayerViewModel.IsOpen):
                if (!_vm.IsOpen) Stop();
                break;
            case nameof(PlayerViewModel.Notice):
                if (_vm.Notice is { Length: > 0 } notice) ShowOsd(notice);
                break;
            case nameof(PlayerViewModel.ControlsLocked):
                Seek.IsEnabled = !_vm.ControlsLocked;
                break;
        }
    }

    private void Start()
    {
        if (_vm?.Url is null) return;
        try
        {
            _opened = false; _ended = false;
            _seekTarget = null; _queuedSeek = null;
            Media.Stop();
            Media.Close();
            Media.Volume = _vm.Volume;
            Media.IsMuted = _vm.IsMuted;
            Media.Source = new Uri(_vm.Url);
            Media.Play();
            ApplySpeed();
            _vm.IsPlaying = true;
            _vm.IsBuffering = true;
            _tick.Start();
            SetChrome(true);
            Focus();
        }
        catch (Exception ex) { _vm.Error = ex.Message; }
    }

    private void Stop()
    {
        RememberNow();
        _opened = false;
        _seekTarget = null; _queuedSeek = null; _pressed = false;
        _tick.Stop();
        _idle.Stop();
        try { Media.Stop(); Media.Close(); Media.Source = null; } catch { }
        if (_vm is not null) { _vm.IsPlaying = false; _vm.IsBuffering = false; }
    }

    private void SetChrome(bool visible)
    {
        TopBar.Visibility = BottomBar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
    }

    private void UpdatePosition()
    {
        if (_vm is null || _dragging) return;
        if (_pressed)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) return;
            // released somewhere we didn't hear about: still go where the bar was put
            _pressed = false;
            SeekTo(Seek.Value);
        }
        try
        {
            if (Media.NaturalDuration.HasTimeSpan)
            {
                var d = Media.NaturalDuration.TimeSpan.TotalSeconds;
                if (d > 0 && Math.Abs(d - _vm.DurationSeconds) > 0.5) _vm.DurationSeconds = d;
            }
            var pos = Media.Position.TotalSeconds;
            if (_vm.CastRole == PlayerViewModel.CastRoles.Host) _vm.ReportHost(pos, _vm.IsPlaying, force: false);
            else if (_vm.CastRole == PlayerViewModel.CastRoles.Follower && _vm.FollowValid && _opened) FollowHost(pos);
            if (_seekTarget is { } target && !CheckSeek(pos, target))
            {
                _vm.PositionSeconds = target; // not there yet: keep the bar at the target
                return;
            }
            if (_vm is { LoopActive: true, LoopA: { } a, LoopB: { } b } && pos >= b)
            {
                Media.Position = TimeSpan.FromSeconds(a); // A-B repeat
                pos = a;
            }
            _vm.PositionSeconds = pos;
            if (_opened && _vm.IsPlaying) _vm.Remember(pos);
        }
        catch { }
    }

    /// <summary>Multicast follower: keep in step with the host, a little behind so the data is already here.</summary>
    private void FollowHost(double pos)
    {
        if (_vm is null) return;
        var target = Math.Max(0, _vm.FollowTarget());
        _syncing = true;
        try
        {
            if (_vm.FollowPlaying)
            {
                if (!_vm.IsPlaying && !_ended) { Media.Play(); _vm.IsPlaying = true; ApplySpeed(); }
                if (_seekTarget is null && Math.Abs(pos - target) > 2.5) SeekTo(target);
            }
            else
            {
                if (_vm.IsPlaying) { Media.Pause(); _vm.IsPlaying = false; }
                if (_seekTarget is null && Math.Abs(pos - target) > 1.5) SeekTo(target);
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>Saves where the current video stands (on stop, close, or switching to another video).</summary>
    private void RememberNow()
    {
        if (_vm is null || !_opened || _ended) return;
        _vm.Remember(CurrentSeconds(), force: true);
    }

    // ---- A-B repeat ------------------------------------------------------------------

    private double CurrentSeconds()
    {
        if (_seekTarget is { } target) return target; // where the player is about to be
        try { return Media.Position.TotalSeconds; } catch { return _vm?.PositionSeconds ?? 0; }
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        ShowOsd(_vm.CycleLoop(CurrentSeconds()));
        Focus();
    }

    private void SeekHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLoopMarks();

    /// <summary>Places the A/B markers and the highlighted band over the seek bar.</summary>
    private void UpdateLoopMarks()
    {
        if (_vm is null) return;
        var d = _vm.DurationSeconds;
        var w = SeekHost.ActualWidth;
        const double pad = 7; // half the slider thumb: where the track starts and ends
        double X(double t) => pad + Math.Max(0, w - 2 * pad) * Math.Clamp(t / d, 0, 1);
        void Place(FrameworkElement mark, double? t)
        {
            if (t is null || d <= 0 || w <= 0) { mark.Visibility = Visibility.Collapsed; return; }
            mark.Visibility = Visibility.Visible;
            Canvas.SetLeft(mark, X(t.Value) - 4);
            Canvas.SetTop(mark, -14);
        }
        Place(MarkA, _vm.LoopA);
        Place(MarkB, _vm.LoopB);
        if (_vm is { LoopA: { } a, LoopB: { } b } && d > 0 && w > 0)
        {
            LoopBand.Visibility = Visibility.Visible;
            Canvas.SetLeft(LoopBand, X(a));
            LoopBand.Width = Math.Max(2, X(b) - X(a));
        }
        else LoopBand.Visibility = Visibility.Collapsed;
    }

    private void TogglePlay()
    {
        if (_vm is null) return;
        if (_vm.ControlsLocked && !_syncing) { ShowOsd("The host controls playback"); return; }
        if (_vm.IsPlaying) { Media.Pause(); _vm.IsPlaying = false; }
        else
        {
            if (Media.Source is null && _vm.Url is not null) { Start(); return; }
            Media.Play(); _vm.IsPlaying = true;
            _ended = false;
            ApplySpeed(); // MediaElement may reset the rate when playback restarts
        }
        _vm.ReportHost(CurrentSeconds(), _vm.IsPlaying, force: true);
    }

    // ---- speed ---------------------------------------------------------------------

    private void ApplySpeed()
    {
        if (_vm is null) return;
        try { Media.SpeedRatio = _vm.Speed; } catch { }
    }

    private void ShowOsd(string text)
    {
        SpeedOsdText.Text = text;
        SpeedOsd.Visibility = Visibility.Visible;
        _osd.Stop(); _osd.Start();
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu { PlacementTarget = SpeedButton, Placement = PlacementMode.Top };
        foreach (var s in PlayerViewModel.Speeds)
        {
            var value = s;
            var item = new MenuItem
            {
                Header = value == 1.0 ? "Normal" : PlayerViewModel.FormatSpeed(value),
                Icon = Math.Abs(value - _vm.Speed) < 0.001
                    ? new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Accent") }
                    : null,
            };
            item.Click += (_, _) => _vm.Speed = value;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SeekTo(double seconds)
    {
        if (_vm is null) return;
        if (_vm.ControlsLocked && !_syncing) { ShowOsd("The host controls playback"); return; }
        var d = _vm.DurationSeconds;
        seconds = Math.Max(0, d > 0 ? Math.Min(seconds, d) : seconds);
        // A deliberate jump out of the A-B range ends the repeat instead of snapping back to A.
        if (_vm.EndLoopIfOutside(seconds)) ShowOsd("Repeat off");
        _ended = false;
        _vm.PositionSeconds = seconds;
        if (!_syncing) _vm.ReportHost(seconds, _vm.IsPlaying, force: true);
        if (!_opened) { _queuedSeek = seconds; return; } // still opening: the player would ignore it now
        BeginSeek(seconds);
    }

    private void BeginSeek(double seconds)
    {
        try { _seekFrom = Media.Position.TotalSeconds; } catch { _seekFrom = 0; }
        _seekTarget = seconds;
        _seekStarted = DateTime.UtcNow;
        _seekTries = 0;
        IssueSeek(seconds);
    }

    private void IssueSeek(double seconds)
    {
        _seekTries++;
        _seekIssued = DateTime.UtcNow;
        if (_seekTries > 1) Services.AppLog.Info("Player", $"jump to {seconds:0.#} s not done yet, asking again (try {_seekTries})");
        try { Media.Position = TimeSpan.FromSeconds(seconds); } catch { }
    }

    /// <summary>
    /// True once the player has made the pending jump (or we gave up on it); otherwise asks again now and then.
    /// </summary>
    private bool CheckSeek(double pos, double target)
    {
        var now = DateTime.UtcNow;
        var since = (now - _seekStarted).TotalSeconds;
        var played = 1.5 + since * Math.Max(1, _vm!.Speed); // how far playback could have moved on by itself
        // at the target (or playing on from it), or somewhere the old position couldn't have reached on its own
        // (the player starts at the key frame before the target, which can be several seconds earlier)
        var landed = (pos >= target - 1.5 && pos <= target + played) || Math.Abs(pos - _seekFrom) > played;
        if (landed || since > 30)
        {
            if (!landed) Services.AppLog.Warn("Player", $"jump to {target:0.#} s didn't happen (player stays at {pos:0.#} s): {_vm.Title}");
            _seekTarget = null;
            return true;
        }
        // ask again after 1.5, 3, 6, 12 s; longer while the player says it is fetching (it may be on its way)
        var wait = 1.5 * Math.Pow(2, Math.Min(_seekTries - 1, 3));
        if (_vm.IsBuffering) wait = Math.Max(wait, 5);
        if ((now - _seekIssued).TotalSeconds >= wait) IssueSeek(target);
        return false;
    }

    // ---- events ----------------------------------------------------------------

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.IsBuffering = false;
        if (Media.NaturalDuration.HasTimeSpan) _vm.DurationSeconds = Media.NaturalDuration.TimeSpan.TotalSeconds;
        ApplySpeed();
        // A jump asked for while opening, else Continue watching: where the user stopped last time.
        var resume = _vm.StartAt;
        _vm.StartAt = 0;
        var start = _queuedSeek ?? resume;
        _queuedSeek = null;
        if (start > 0 && (_vm.DurationSeconds <= 0 || start < _vm.DurationSeconds - 1))
        {
            _vm.PositionSeconds = start;
            BeginSeek(start);
            if (start == resume && !_vm.QuietStart) ShowOsd("Resumed at " + Core.Format.Duration(TimeSpan.FromSeconds(start)));
        }
        _vm.QuietStart = false;
        _opened = true;
    }

    /// <summary>At the end: rewind to the start and wait (first frame shown, slider at 0, Play starts over).</summary>
    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _seekTarget = null; // a jump to the very end lands here
        if (_vm is { LoopActive: true, LoopA: { } a })
        {
            // B was at (or past) the very end: keep repeating from A
            try { Media.Position = TimeSpan.FromSeconds(a); Media.Play(); ApplySpeed(); } catch { }
            _vm.PositionSeconds = a;
            return;
        }
        try
        {
            Media.Pause();
            Media.Position = TimeSpan.Zero; // ScrubbingEnabled renders the first frame while paused
        }
        catch { }
        _vm.Remember(_vm.DurationSeconds, finished: true, force: true);
        _ended = true;
        _vm.IsPlaying = false;
        _vm.PositionSeconds = 0;
        SetChrome(true);
    }

    private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_vm is null) return;
        _seekTarget = null; _queuedSeek = null;
        _vm.IsPlaying = false; _vm.IsBuffering = false;
        _vm.Error = e.ErrorException?.Message ?? "Unknown playback error";
        Services.AppLog.Warn("Player", $"playback failed: {_vm.Title} ({_vm.Peer?.Label})", e.ErrorException);
        SetChrome(true);
    }

    private void Media_BufferingStarted(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.IsBuffering = true;
        if (_opened && _seekTarget is null) _vm.NoteStall(CurrentSeconds()); // a stall mid-video (not a seek): the lighter copy may take over
    }
    private void Media_BufferingEnded(object sender, RoutedEventArgs e) { if (_vm is not null) _vm.IsBuffering = false; }

    private void Media_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) _vm?.ToggleFullscreenCommand.Execute(null);
        else TogglePlay();
        Focus();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Back_Click(object sender, RoutedEventArgs e) => SeekTo((_vm?.PositionSeconds ?? 0) - 10);
    private void Fwd_Click(object sender, RoutedEventArgs e) => SeekTo((_vm?.PositionSeconds ?? 0) + 10);

    // Seek bar. Dragging the thumb jumps when it is let go. A click on the track moves the thumb there at once
    // (IsMoveToPointEnabled) and jumps when the button is released, wherever that happens: the bar takes the mouse
    // until then, and position updates leave the bar alone meanwhile so the click can't be undone.
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        if (e.Handled && !_dragging) Seek.CaptureMouse(); // a track click (the thumb takes the mouse itself)
    }

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed || _dragging) return; // a thumb drag ends in Seek_DragCompleted
        _pressed = false;
        if (Seek.IsMouseCaptured) Seek.ReleaseMouseCapture();
        SeekTo(Seek.Value);
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false; _pressed = false;
        if (e.HorizontalChange != 0) SeekTo(Seek.Value); // a click on the thumb without moving it isn't a jump
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is null || !_vm.IsOpen) return;
        // Shift+> / Shift+< change speed (same keys as most web players)
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.OemPeriod or Key.OemComma)
        {
            _vm.StepSpeed(e.Key == Key.OemPeriod ? +1 : -1);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.OemOpenBrackets: ShowOsd(_vm.SetLoopA(CurrentSeconds())); break;      // [  set A
            case Key.OemCloseBrackets: ShowOsd(_vm.SetLoopB(CurrentSeconds())); break;     // ]  set B
            case Key.Oem5: case Key.OemBackslash:                                         // \  clear
                if (_vm.LoopA is not null) { _vm.ClearLoop(); ShowOsd("Repeat off"); }
                break;
            case Key.Escape:
                if (_vm.IsFullscreen) _vm.IsFullscreen = false; else _vm.CloseCommand.Execute(null);
                break;
            case Key.Space: case Key.K: TogglePlay(); break;
            case Key.Left: case Key.J: SeekTo(_vm.PositionSeconds - 10); break;
            case Key.Right: case Key.L: SeekTo(_vm.PositionSeconds + 10); break;
            case Key.Up: _vm.Volume = Math.Min(1, _vm.Volume + 0.05); break;
            case Key.Down: _vm.Volume = Math.Max(0, _vm.Volume - 0.05); break;
            case Key.F: _vm.ToggleFullscreenCommand.Execute(null); break;
            case Key.M: _vm.ToggleMuteCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
