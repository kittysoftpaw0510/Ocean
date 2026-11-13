using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>
/// The boundary of what is being recorded: a fine line around it with rounded corner accents (a viewfinder) and a
/// soft glow that breathes slowly while recording (red); still and finely dashed while paused (amber). A small dark
/// tag at its top left says REC (with a breathing dot) or PAUSED.
///
/// Four thin strips (top, bottom, left, right), each its own click-through window: nothing under them stops
/// working, and a large see-through window (which Windows redraws in software, the whole area at every frame of the
/// pulse) is avoided. They are kept out of every capture, so the boundary is never in the recording; where Windows
/// can't do that (before Windows 10 2004), it is drawn only outside the recorded area, never on it.
/// A part of a screen and a window get the line just outside them; a whole screen gets it along its edges. A
/// recorded window is followed when it moves or changes size, and the boundary hides while it is minimized or on
/// another desktop.
/// </summary>
public sealed class RecordBoundary : IDisposable
{
    private const int Thick = 8;    // strip thickness, real pixels: the line and its soft glow
    private const int TagHeight = 22;

    private readonly ScreenPick _pick;
    private readonly RecordViewModel _vm;
    private readonly Strip[] _strips;
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private Int32Rect _area;
    private bool _shown, _inside, _disposed;

    public RecordBoundary(ScreenPick pick, RecordViewModel vm)
    {
        _pick = pick; _vm = vm;
        _inside = pick.Kind == ScreenPick.Kinds.Monitor;
        _strips = new[] { new Strip(Side.Top, _inside), new Strip(Side.Bottom, _inside), new Strip(Side.Left, _inside), new Strip(Side.Right, _inside) };
        foreach (var s in _strips)
        {
            s.SourceInitialized += (_, _) =>
            {
                ClickThrough(s);
                // on a whole screen the line is on the recorded area: only where it stays out of the capture
                if (!ScreenSources.HideFromCapture(s) && _inside) s.Unsafe = true;
            };
        }
        vm.PropertyChanged += OnVmChanged;
        _follow.Tick += (_, _) => Place();
        Place();
        _follow.Start();
        ApplyState();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordViewModel.Paused) or nameof(RecordViewModel.Saving)) ApplyState();
    }

    private void ApplyState()
    {
        var paused = _vm.Paused || _vm.Saving;
        foreach (var s in _strips) s.SetPaused(paused, _vm.Saving ? "SAVING" : paused ? "PAUSED" : "REC");
    }

    /// <summary>Where the recorded thing is now (real pixels); null while it can't be seen.</summary>
    private Int32Rect? Target()
    {
        switch (_pick.Kind)
        {
            case ScreenPick.Kinds.Window:
                if (!ScreenSources.IsOnScreen(_pick.Window)) return null;
                return ScreenSources.WindowBounds(_pick.Window);
            default:
                var m = ScreenSources.Monitors().FirstOrDefault(x => x.Handle == _pick.Monitor);
                if (m is null) return null;
                if (_pick.Kind == ScreenPick.Kinds.Region)
                    return new Int32Rect(m.Bounds.X + _pick.CropLeft, m.Bounds.Y + _pick.CropTop, _pick.SourceWidth, _pick.SourceHeight);
                return m.Bounds;
        }
    }

    private void Place()
    {
        if (_disposed) return;
        var t = Target();
        if (t is not { } a || a.Width < 8 || a.Height < 8)
        {
            if (_shown) { foreach (var s in _strips) s.Hide(); _shown = false; }
            return;
        }
        if (_shown && a.Equals(_area)) return;
        _area = a;
        int x = a.X, y = a.Y, w = a.Width, h = a.Height, k = Thick, g = 3; // g: a small gap between the line and the area
        foreach (var s in _strips)
        {
            Int32Rect r = _inside
                ? s.Side switch
                {
                    Side.Top => new(x, y, w, k + TagHeight),
                    Side.Bottom => new(x, y + h - k, w, k),
                    Side.Left => new(x, y, k, h),
                    _ => new(x + w - k, y, k, h),
                }
                : s.Side switch
                {
                    Side.Top => new(x - k - g, y - k - g - TagHeight, w + 2 * (k + g), k + TagHeight),
                    Side.Bottom => new(x - k - g, y + h + g, w + 2 * (k + g), k),
                    Side.Left => new(x - k - g, y - g, k, h + 2 * g),
                    _ => new(x + w + g, y - g, k, h + 2 * g),
                };
            if (s.Unsafe) continue;
            if (!s.IsVisible) s.Show();
            ScreenSources.MoveTo(s, r);
            s.Layout();
        }
        _shown = true;
    }

    private static void ClickThrough(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLongPtr(h, -20).ToInt64();
        // transparent to the mouse, never activated, not in Alt+Tab
        SetWindowLongPtr(h, -20, new IntPtr(ex | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */ | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _follow.Stop();
        _vm.PropertyChanged -= OnVmChanged;
        foreach (var s in _strips) s.Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    private enum Side { Top, Bottom, Left, Right }

    /// <summary>
    /// One edge: a fine line along the side that faces the recorded area, short rounded corner accents at both ends
    /// (together the four strips draw a viewfinder), and a soft glow that breathes slowly while recording.
    /// </summary>
    private sealed class Strip : Window
    {
        private static readonly Color Rec = Color.FromRgb(0xF4, 0x3F, 0x5E), Pause = Color.FromRgb(0xF5, 0xB0, 0x2E);
        private const double LineWidth = 1.5, CornerWidth = 3, CornerLength = 18;
        public Side Side { get; }
        /// <summary>The line would be in the recording (no capture exclusion here): this strip stays hidden.</summary>
        public bool Unsafe;
        private readonly bool _inside;
        private readonly Canvas _canvas = new();
        private readonly Line _line = new() { StrokeThickness = LineWidth, Opacity = 0.9 };
        private readonly Line _cornerA = new() { StrokeThickness = CornerWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly Line _cornerB = new() { StrokeThickness = CornerWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.5 };
        // the tag: a small dark pill, a dot and a word
        private readonly Border _tag = new()
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 8, 1), Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x10, 0x18)),
            BorderThickness = new Thickness(1),
        };
        private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _tagText = new() { FontSize = 10, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Storyboard _pulse = new();

        public Strip(Side side, bool inside)
        {
            Side = side; _inside = inside;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false;
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; Width = 10; Height = 10;
            var glowLayer = new Canvas { Effect = _glow };
            glowLayer.Children.Add(_line); glowLayer.Children.Add(_cornerA); glowLayer.Children.Add(_cornerB);
            _canvas.Children.Add(glowLayer);
            if (side == Side.Top)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(_dot); row.Children.Add(_tagText);
                _tag.Child = row;
                _canvas.Children.Add(_tag);
            }
            Content = _canvas;
            // a slow breath of the glow and of the tag's dot (the line itself stays crisp); 30 steps a second is plenty
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var period = TimeSpan.FromSeconds(1.6);
            var blur = new DoubleAnimation(2, 8, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var fade = new DoubleAnimation(0.25, 0.75, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var dot = new DoubleAnimation(0.35, 1, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            foreach (var a in new Timeline[] { blur, fade, dot }) Timeline.SetDesiredFrameRate(a, 30);
            Storyboard.SetTarget(blur, glowLayer); Storyboard.SetTargetProperty(blur, new PropertyPath("Effect.BlurRadius"));
            Storyboard.SetTarget(fade, glowLayer); Storyboard.SetTargetProperty(fade, new PropertyPath("Effect.Opacity"));
            Storyboard.SetTarget(dot, _dot); Storyboard.SetTargetProperty(dot, new PropertyPath(OpacityProperty));
            _pulse.Children.Add(blur); _pulse.Children.Add(fade);
            if (side == Side.Top) _pulse.Children.Add(dot);
            SizeChanged += (_, _) => Layout();
        }

        public void SetPaused(bool paused, string tag)
        {
            var c = paused ? Pause : Rec;
            var brush = new SolidColorBrush(c); brush.Freeze();
            _line.Stroke = brush; _cornerA.Stroke = brush; _cornerB.Stroke = brush; _dot.Fill = brush;
            _line.StrokeDashArray = paused ? new DoubleCollection { 4, 3 } : null;
            _glow.Color = c;
            _tag.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B));
            _tagText.Foreground = brush;
            _tagText.Text = tag;
            _dot.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            if (paused) { _pulse.Stop(); _glow.BlurRadius = 3; _glow.Opacity = 0.35; _tagText.Text = "❚❚  " + tag; }
            else _pulse.Begin();
        }

        /// <summary>Draws the line along the side facing the recorded area (just outside it; on a whole screen, along its edge).</summary>
        public void Layout()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double k = Thick / dpi.DpiScaleY, kx = Thick / dpi.DpiScaleX, tag = TagHeight / dpi.DpiScaleY, c = CornerLength, e = CornerWidth / 2;
            switch (Side)
            {
                case Side.Top:
                {
                    // outside the area: the line at the bottom of the strip; on a whole screen, at its top
                    var y = _inside ? e : h - e;
                    double x0 = _inside ? e : kx - e, x1 = _inside ? w - e : w - kx + e;
                    Set(_line, x0, y, x1, y); Set(_cornerA, x0, y, x0 + c, y); Set(_cornerB, x1 - c, y, x1, y);
                    _tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(_tag, x0 + c + 6);
                    Canvas.SetTop(_tag, _inside ? y + 5 : Math.Max(0, y - _tag.DesiredSize.Height - 4));
                    break;
                }
                case Side.Bottom:
                {
                    var y = _inside ? h - e : e;
                    double x0 = _inside ? e : kx - e, x1 = _inside ? w - e : w - kx + e;
                    Set(_line, x0, y, x1, y); Set(_cornerA, x0, y, x0 + c, y); Set(_cornerB, x1 - c, y, x1, y);
                    break;
                }
                case Side.Left:
                {
                    var x = _inside ? e : w - e;
                    Set(_line, x, 0, x, h); Set(_cornerA, x, 0, x, Math.Min(h, c)); Set(_cornerB, x, Math.Max(0, h - c), x, h);
                    break;
                }
                default:
                {
                    var x = _inside ? w - e : e;
                    Set(_line, x, 0, x, h); Set(_cornerA, x, 0, x, Math.Min(h, c)); Set(_cornerB, x, Math.Max(0, h - c), x, h);
                    break;
                }
            }
        }

        private static void Set(Line l, double x1, double y1, double x2, double y2) { l.X1 = x1; l.Y1 = y1; l.X2 = x2; l.Y2 = y2; }
    }
}
