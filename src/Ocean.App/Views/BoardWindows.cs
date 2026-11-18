using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

/// <summary>
/// The board of my sharing, floating on my desktop: on top, on every virtual desktop, kept out of the capture (the
/// viewers have it in their own window, not in the picture). Moved by its bar, resized at its corner; hiding it
/// keeps the board going. It comes back where it was.
/// </summary>
public sealed class BoardWindow : Window
{
    private static Rect? _last;
    private readonly BoardView _view = new();
    private readonly AllDesktops _allDesktops;
    private bool _closing;

    public BoardVm Board { get; }

    public BoardWindow(BoardVm board)
    {
        Board = board;
        Title = "Ocean: board";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip; Topmost = true; ShowInTaskbar = false;
        MinWidth = 260; MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        var r = _last ?? new Rect(area.Right - 440 - 24, area.Top + 90, 440, Math.Min(560, area.Height - 140));
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
        _view.Attach(board);
        Content = _view;
        _view.DragArea.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
        SourceInitialized += (_, _) => ScreenSources.HideFromCapture(this);
        _allDesktops = new AllDesktops(this);
        _allDesktops.Set(true);
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
    }

    private void Remember() { if (IsLoaded && WindowState == WindowState.Normal) _last = new Rect(Left, Top, Width, Height); }

    public void CloseForGood() { _closing = true; _view.Detach(); _allDesktops.Dispose(); Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides it like its own button (the board goes on)
        if (!_closing) { e.Cancel = true; Board.Visible = false; }
        base.OnClosing(e);
    }
}

/// <summary>
/// Where viewers point, shown on my screen: over exactly what I share (a screen, a part of one, a window), on top,
/// never in the way of the mouse. A click is a ripple with the viewer's name in their colour; a held button is a
/// pointer that follows. It is part of the shared picture (for a screen or a part of one), so everybody watching
/// sees it too; a window's own capture doesn't take other windows, so there only I see it.
/// </summary>
public sealed class PointerOverlay : IDisposable
{
    private Window? _w;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private Int32Rect _area;
    private readonly Dictionary<string, (FrameworkElement El, DateTime At)> _lasers = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _tidy = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastActivity;

    public PointerOverlay()
    {
        _tidy.Tick += (_, _) =>
        {
            // a pointer that stopped coming (the viewer's "up" was lost) goes; an idle overlay is hidden
            foreach (var (id, l) in _lasers.ToList()) if (DateTime.UtcNow - l.At > TimeSpan.FromSeconds(3)) End(id);
            if (_w is not null && _lasers.Count == 0 && DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(6)) { _w.Hide(); _tidy.Stop(); }
        };
    }

    public void Show(ScreenPointDto p, ScreenPick pick)
    {
        if (Area(pick) is not { } area) return;
        Ensure(area);
        if (_w is null) return;
        var (px, py) = Map(p.X, p.Y, pick, area);
        var dpi = VisualTreeHelper.GetDpi(_w);
        var x = (px - area.X) / dpi.DpiScaleX;
        var y = (py - area.Y) / dpi.DpiScaleY;
        var color = Controls.InitialsBrushConverter.ColorOf(p.PeerId);
        _lastActivity = DateTime.UtcNow;
        switch (p.Kind)
        {
            case ScreenPointDto.Move: Laser(p.PeerId, p.PeerName, color, x, y); break;
            case ScreenPointDto.Up: End(p.PeerId); break;
            default: Ripple(p.PeerName, color, x, y); break;
        }
    }

    /// <summary>What I share, in screen pixels now (a window may have moved).</summary>
    private static Int32Rect? Area(ScreenPick pick)
    {
        var mon = ScreenSources.Monitors();
        switch (pick.Kind)
        {
            case ScreenPick.Kinds.Window:
                return ScreenSources.IsOnScreen(pick.Window) ? ScreenSources.WindowBounds(pick.Window) : null;
            case ScreenPick.Kinds.Region when mon.FirstOrDefault(m => m.Handle == pick.Monitor) is { } m:
                return new Int32Rect(m.Bounds.X + pick.CropLeft, m.Bounds.Y + pick.CropTop, pick.SourceWidth, pick.SourceHeight);
            default:
                return mon.FirstOrDefault(m => m.Handle == pick.Monitor)?.Bounds;
        }
    }

    /// <summary>
    /// A spot of the picture (parts of its width and height) on the screen. The picture keeps the size it started
    /// with; a window resized since then sits in it scaled, with bars: those are taken off.
    /// </summary>
    private static (double X, double Y) Map(double x, double y, ScreenPick pick, Int32Rect area)
    {
        var (w, h) = pick.SendSize();
        double pic = (double)w / h, real = (double)area.Width / Math.Max(1, area.Height);
        double cw = 1, ch = 1;
        if (real > pic) ch = pic / real; else cw = real / pic;
        var nx = Math.Clamp((x - (1 - cw) / 2) / cw, 0, 1);
        var ny = Math.Clamp((y - (1 - ch) / 2) / ch, 0, 1);
        return (area.X + nx * area.Width, area.Y + ny * area.Height);
    }

    private void Ensure(Int32Rect area)
    {
        if (_w is null)
        {
            _w = new Window
            {
                Title = "Ocean: pointing", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Content = _canvas, Left = -32000, Top = -32000, Width = 10, Height = 10,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            _w.SourceInitialized += (_, _) => ClickThrough(_w);
            _area = default;
        }
        if (!_w.IsVisible) _w.Show();
        if (!_area.Equals(area)) { _area = area; ScreenSources.MoveTo(_w, area); }
        _tidy.Start();
    }

    private static Border Tag(string name, Color color) => new()
    {
        Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 8, 3),
        Child = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x11, 0x20)), FontFamily = new FontFamily("Segoe UI") },
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.45 },
    };

    /// <summary>A click: rings that spread from the spot, a dot, and the name beside it; gone after about 1.6 s.</summary>
    private void Ripple(string name, Color color, double x, double y)
    {
        var brush = new SolidColorBrush(color);
        var group = new Canvas();
        for (var i = 0; i < 2; i++)
        {
            var ring = new System.Windows.Shapes.Ellipse { Stroke = brush, StrokeThickness = 3, Width = 80, Height = 80, RenderTransformOrigin = new Point(0.5, 0.5) };
            var scale = new ScaleTransform(0.1, 0.1);
            ring.RenderTransform = scale;
            Canvas.SetLeft(ring, x - 40); Canvas.SetTop(ring, y - 40);
            var begin = TimeSpan.FromMilliseconds(220 * i);
            var grow = new DoubleAnimation(0.1, 1, TimeSpan.FromMilliseconds(800)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.Opacity = 0;
            ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin,
                KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)), new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))) },
            });
            group.Children.Add(ring);
        }
        var dot = new System.Windows.Shapes.Ellipse { Fill = brush, Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, Effect = new DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 } };
        Canvas.SetLeft(dot, x - 7); Canvas.SetTop(dot, y - 7);
        group.Children.Add(dot);
        var tag = Tag(name, color);
        Canvas.SetLeft(tag, x + 12); Canvas.SetTop(tag, y + 10);
        group.Children.Add(tag);
        _canvas.Children.Add(group);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(1100) };
        fade.Completed += (_, _) => _canvas.Children.Remove(group);
        group.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>A held button: a glowing dot with the name, following the viewer's mouse.</summary>
    private void Laser(string peerId, string name, Color color, double x, double y)
    {
        if (!_lasers.TryGetValue(peerId, out var l))
        {
            var g = new Canvas();
            var dot = new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(color), Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, Effect = new DropShadowEffect { Color = color, BlurRadius = 18, ShadowDepth = 0, Opacity = 1 } };
            Canvas.SetLeft(dot, -8); Canvas.SetTop(dot, -8);
            var tag = Tag(name, color);
            Canvas.SetLeft(tag, 12); Canvas.SetTop(tag, 10);
            g.Children.Add(dot); g.Children.Add(tag);
            _canvas.Children.Add(g);
            l = (g, DateTime.UtcNow);
        }
        Canvas.SetLeft(l.El, x); Canvas.SetTop(l.El, y);
        l.El.BeginAnimation(UIElement.OpacityProperty, null);
        l.El.Opacity = 1;
        _lasers[peerId] = (l.El, DateTime.UtcNow);
    }

    private void End(string peerId)
    {
        if (!_lasers.Remove(peerId, out var l)) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) => _canvas.Children.Remove(l.El);
        l.El.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void ClickThrough(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLongPtr(h, -20).ToInt64();
        SetWindowLongPtr(h, -20, new IntPtr(ex | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */ | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    public void Dispose() { _tidy.Stop(); _w?.Close(); _w = null; _lasers.Clear(); }
}
