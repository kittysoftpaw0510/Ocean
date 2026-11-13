using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Ocean.App.Services;

namespace Ocean.App.Views;

/// <summary>
/// 3, 2, 1 over what is about to be recorded: the area dims a little, a large number counts down in a ring that
/// sweeps once a second, so there is a moment to get ready and the video doesn't open on the Start click. Esc or a
/// click cancels. Kept out of every capture (people watching a shared screen don't see it either).
/// </summary>
public sealed class RecordCountdownWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly TextBlock _number = new() { FontSize = 96, FontWeight = FontWeights.Light, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI"), RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Ellipse _sweep = new() { Width = 196, Height = 196, Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)), StrokeThickness = 4, StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90) };
    private int _left;
    private System.Windows.Threading.DispatcherTimer? _timer;

    private RecordCountdownWindow(Int32Rect area, int seconds)
    {
        _left = seconds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Topmost = true; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x4D, 0x05, 0x08, 0x10));
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; Width = 10; Height = 10;
        Cursor = Cursors.Hand;
        var ring = new Ellipse { Width = 200, Height = 200, Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x0E, 0x16)), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.5 };
        var dial = new Grid { Width = 200, Height = 200 };
        dial.Children.Add(ring); dial.Children.Add(_sweep); dial.Children.Add(_number);
        var caption = new TextBlock { Text = "Recording starts", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 2) };
        var hint = new TextBlock { Text = "Esc or click to cancel", Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(dial); stack.Children.Add(caption); stack.Children.Add(hint);
        Content = new Grid { Children = { stack } };
        SourceInitialized += (_, _) => { ScreenSources.HideFromCapture(this); ScreenSources.MoveTo(this, area); };
        Loaded += (_, _) => { ScreenSources.MoveTo(this, area); Activate(); Focus(); Tick(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(false); };
        MouseLeftButtonUp += (_, _) => Finish(false);
        MouseRightButtonUp += (_, _) => Finish(false);
        Deactivated += (_, _) => { }; // (another window taking the focus doesn't cancel it)
    }

    private void Tick()
    {
        if (_left <= 0) { Finish(true); return; }
        _number.Text = _left.ToString();
        // the number comes in (a little larger, fading in); the ring sweeps the second away
        var grow = new ScaleTransform(1.35, 1.35);
        _number.RenderTransform = grow;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
        _number.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(300)));
        var circumference = Math.PI * (_sweep.Width - _sweep.StrokeThickness) / _sweep.StrokeThickness;
        _sweep.StrokeDashArray = new DoubleCollection { circumference, circumference };
        _sweep.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, circumference, TimeSpan.FromSeconds(1)));
        _left--;
        _timer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTimer; _timer.Tick += OnTimer;
        _timer.Stop(); _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e) => Tick();

    private void Finish(bool go)
    {
        _timer?.Stop();
        if (_result.TrySetResult(go)) Close();
    }

    /// <summary>Counts down over <paramref name="area"/> (real pixels). True: go; false: cancelled.</summary>
    public static Task<bool> RunAsync(Int32Rect area, int seconds = 3)
    {
        var w = new RecordCountdownWindow(area, seconds);
        w.Show();
        return w._result.Task;
    }
}
