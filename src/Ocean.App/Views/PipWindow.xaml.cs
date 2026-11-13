using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>
/// Picture in picture: a small borderless window the player's video element moves into (playback goes on as it
/// is, nothing reloads), so Ocean and other programs stay usable. While pinned it is kept on top and on every
/// virtual desktop. Drag it by the video,
/// resize it at the edges; double-click or Esc returns to the full player. The player (PlayerView) does the
/// playing; this window only hosts the picture and passes the controls on.
/// </summary>
public partial class PipWindow : Window
{
    private readonly PlayerViewModel _vm;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _closingFromCode;
    /// <summary>While pinned it is also on every virtual desktop, so switching desktops keeps it in view.</summary>
    private readonly Services.AllDesktops _allDesktops;
    /// <summary>Where the window was last time (this session), so it comes back there.</summary>
    private static Rect? _lastBounds;

    public event Action? PlayPause;
    public event Action<double>? Step;
    public event Action? BackToPlayer;
    public event Action? CloseVideo;

    public PipWindow(PlayerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        // on top while pinned (set here, not bound: it must hold from the first moment the window shows)
        Topmost = vm.PipPinned;
        _allDesktops = new Services.AllDesktops(this);
        _allDesktops.Set(vm.PipPinned);
        vm.PropertyChanged += OnVmChanged;
        try { Icon = Services.IconFactory.WindowIcon(); } catch { } // an icon is not worth failing the window over
        if (_lastBounds is { } b) { Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height; }
        else
        {
            // bottom right of the screen's work area, clear of the taskbar
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24; Top = area.Bottom - Height - 24;
        }
        _idle.Tick += (_, _) => { _idle.Stop(); if (!IsMouseOver) ShowControls(false); else if (Mouse.LeftButton == MouseButtonState.Released) ShowControls(false); };
        MouseMove += (_, _) => { ShowControls(true); _idle.Stop(); _idle.Start(); };
        MouseLeave += (_, _) => { _idle.Stop(); ShowControls(false); };
        MouseLeftButtonDown += OnMouseDown;
        KeyDown += OnKey;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.PipPinned)) return;
        Topmost = _vm.PipPinned;
        _allDesktops.Set(_vm.PipPinned);
    }

    /// <summary>Puts the video element in (behind the controls).</summary>
    public void Attach(UIElement media) => Host.Children.Insert(0, media);
    public void Detach(UIElement media) => Host.Children.Remove(media);

    /// <summary>Closed by the player (back to the full player, or the video closed).</summary>
    public void CloseFromPlayer() { _closingFromCode = true; Close(); }

    private void ShowControls(bool show)
    {
        Controls.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        IdleProgress.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        Cursor = show ? null : Cursors.None;
    }

    /// <summary>On the picture (also the video element's own clicks, see PlayerView): drag; double-click: back.</summary>
    public void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (e.ClickCount == 2) { BackToPlayer?.Invoke(); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: case Key.K: PlayPause?.Invoke(); break;
            case Key.Left: case Key.J: Step?.Invoke(-10); break;
            case Key.Right: case Key.L: Step?.Invoke(+10); break;
            case Key.Escape: case Key.P: BackToPlayer?.Invoke(); break;
            case Key.T: _vm.PipPinned = !_vm.PipPinned; break;
            default: return;
        }
        e.Handled = true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPause?.Invoke();
    private void Rewind_Click(object sender, RoutedEventArgs e) => Step?.Invoke(-10);
    private void Forward_Click(object sender, RoutedEventArgs e) => Step?.Invoke(+10);
    private void Pin_Click(object sender, RoutedEventArgs e) => _vm.PipPinned = !_vm.PipPinned;
    private void Back_Click(object sender, RoutedEventArgs e) => BackToPlayer?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseVideo?.Invoke();

    protected override void OnClosing(CancelEventArgs e)
    {
        _lastBounds = new Rect(Left, Top, Width, Height);
        if (!_closingFromCode)
        {
            // Alt+F4 or the taskbar's "Close window": stop the video, like the × button
            e.Cancel = true;
            CloseVideo?.Invoke();
            return;
        }
        _idle.Stop();
        _vm.PropertyChanged -= OnVmChanged;
        _allDesktops.Dispose();
        base.OnClosing(e);
    }
}
