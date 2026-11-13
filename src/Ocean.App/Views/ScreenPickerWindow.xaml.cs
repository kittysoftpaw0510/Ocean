using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.App.Services;

namespace Ocean.App.Views;

/// <summary>One thing that can be shared, as a card in the picker.</summary>
public sealed partial class ScreenSourceCard : ObservableObject
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "";
    public MonitorInfo? Monitor { get; init; }
    public WindowInfo? Window { get; init; }
    /// <summary>A part of <see cref="Monitor"/> (real pixels, relative to the monitor).</summary>
    public Int32Rect? Region { get; init; }
    [ObservableProperty] private BitmapSource? _preview;
}

/// <summary>
/// Lets the user pick what to share: a whole screen, a part of one (marked with the mouse) or one window. Small
/// pictures show what each is right now. Nothing is captured for sharing before "Share".
/// </summary>
public partial class ScreenPickerWindow : Window
{
    private readonly ObservableCollection<ScreenSourceCard> _screens = new();
    private readonly ObservableCollection<ScreenSourceCard> _windows = new();
    private ScreenSourceCard? _chosen;
    private bool _selecting;
    /// <summary>While a part of a screen is being marked (the picker is minimized meanwhile): its window list is not refreshed.</summary>
    private bool _marking;
    /// <summary>Marks a part of a monitor with the mouse (replaceable for checks, which can't drag a mouse).</summary>
    internal static Func<MonitorInfo, Int32Rect?> MarkArea = RegionSelectWindow.Select;

    public ScreenPick? Result { get; private set; }

    /// <summary>Recording (null: sharing): its options show instead of sharing's, and sizes follow its quality.</summary>
    private readonly ViewModels.RecordViewModel? _record;

    public ScreenPickerWindow(string heading, string detail, ViewModels.RecordViewModel? record = null)
    {
        _record = record;
        InitializeComponent();
        Heading.Text = heading; Detail.Text = detail;
        if (record is not null)
        {
            Title = "Record your screen";
            RecordOptions.DataContext = record;
            RecordOptions.Visibility = Visibility.Visible;
            ShareOptions.Visibility = Visibility.Collapsed;
            ShareButton.Content = "Start recording";
            record.PropertyChanged += OnRecordChanged;
            Closed += (_, _) => record.PropertyChanged -= OnRecordChanged;
        }
        try { Icon = IconFactory.WindowIcon(); } catch { }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Screens.ItemsSource = _screens; Windows.ItemsSource = _windows;
        // the lists sit in one scrolling page: the wheel scrolls the page, not a list
        foreach (var list in new[] { Screens, Windows })
            list.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                (list.Parent as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = s });
            };
        // screens are pictured before this window shows, so it is not in its own pictures
        foreach (var m in ScreenSources.Monitors())
            _screens.Add(new ScreenSourceCard { Name = m.Name, Detail = m.Detail, Monitor = m, Preview = ScreenSources.Snapshot(m.Bounds) });
        Loaded += (_, _) => LoadWindows();
        Activated += (_, _) => { if (IsLoaded && !_selecting && !_marking) RefreshWindows(); };
        if (_screens.Count > 0) Screens.SelectedIndex = 0;
    }

    private void LoadWindows()
    {
        var mine = new WindowInteropHelper(this).Handle;
        var own = Application.Current.Windows.OfType<Window>().Select(w => new WindowInteropHelper(w).Handle).Where(h => h != IntPtr.Zero).Append(mine).ToHashSet();
        var found = ScreenSources.Windows(own);
        _windows.Clear();
        foreach (var w in found)
            _windows.Add(new ScreenSourceCard { Name = w.Title, Detail = w.Detail, Glyph = "", Window = w });
        NoWindows.Visibility = _windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // their pictures come in the background, one after the other
        var cards = _windows.ToList();
        _ = Task.Run(() =>
        {
            foreach (var card in cards)
            {
                var img = ScreenSources.Snapshot(card.Window!);
                if (img is not null) Dispatcher.BeginInvoke(() => card.Preview = img);
            }
        });
    }

    /// <summary>Coming back to the picker (after opening or restoring a window to share): the list is up to date again.</summary>
    private void RefreshWindows()
    {
        var chosen = _chosen?.Window?.Handle;
        LoadWindows();
        if (chosen is { } h && _windows.FirstOrDefault(c => c.Window!.Handle == h) is { } again) Windows.SelectedItem = again;
    }

    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || sender is not ListBox list || list.SelectedItem is not ScreenSourceCard card) return;
        _selecting = true;
        (ReferenceEquals(list, Screens) ? Windows : Screens).SelectedItem = null; // one choice across both lists
        _selecting = false;
        Choose(card);
    }

    private void Choose(ScreenSourceCard? card)
    {
        _chosen = card;
        ShareButton.IsEnabled = card is not null;
        if (card is null) { Chosen.Text = ""; return; }
        var pick = ToPick(card);
        var (w, h) = _record is null ? pick.SendSize() : Services.RecordingQuality.Fit(Services.RecordingQuality.Of(_record.Quality), pick.SourceWidth, pick.SourceHeight);
        var verb = _record is null ? "Shared" : "Recorded";
        Chosen.Text = w == pick.SourceWidth && h == pick.SourceHeight ? $"{verb} at {w} × {h}" : $"{verb} at {w} × {h} (scaled down from {pick.SourceWidth} × {pick.SourceHeight})";
    }

    private void Source_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement((ListBox)sender, d) is ListBoxItem) Share_Click(sender, e);
    }

    private void Region_Click(object sender, RoutedEventArgs e)
    {
        // on the selected screen (a part of one already picked: that screen again), else the main one
        var monitor = _chosen?.Monitor ?? _screens.FirstOrDefault()?.Monitor;
        if (monitor is null) return;
        // out of the way while marking (minimized, not hidden: hiding would end this dialog)
        _marking = true;
        WindowState = WindowState.Minimized;
        Int32Rect? area;
        try { area = MarkArea(monitor); }
        finally { WindowState = WindowState.Normal; Activate(); Dispatcher.BeginInvoke(() => _marking = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
        if (area is not { } r) return;
        foreach (var old in _screens.Where(c => c.Region is not null).ToList()) _screens.Remove(old);
        var card = new ScreenSourceCard
        {
            Name = $"Part of {monitor.Name.ToLowerInvariant()}", Detail = $"{r.Width} × {r.Height}", Glyph = "", Monitor = monitor, Region = r,
            Preview = ScreenSources.Snapshot(new Int32Rect(monitor.Bounds.X + r.X, monitor.Bounds.Y + r.Y, r.Width, r.Height)),
        };
        _screens.Add(card);
        // chosen outright (not left to the list's selection event): Share must share this part, nothing else
        _selecting = true;
        Windows.SelectedItem = null;
        Screens.SelectedItem = card;
        _selecting = false;
        Choose(card);
    }

    internal ScreenPick? CurrentPick => _chosen is null ? null : ToPick(_chosen);

    private ScreenPick ToPick(ScreenSourceCard card)
    {
        if (card.Window is { } w)
        {
            // the window may have been resized since it was listed
            var b = ScreenSources.WindowBounds(w.Handle) ?? w.Bounds;
            return new ScreenPick { Kind = ScreenPick.Kinds.Window, Window = w.Handle, SourceWidth = b.Width, SourceHeight = b.Height, Title = w.Title.Length > 80 ? w.Title[..80] + "…" : w.Title, Smooth = Smooth.IsChecked == true };
        }
        var m = card.Monitor!;
        if (card.Region is { } r)
            return new ScreenPick
            {
                Kind = ScreenPick.Kinds.Region, Monitor = m.Handle, SourceWidth = r.Width, SourceHeight = r.Height, Title = card.Name, Smooth = Smooth.IsChecked == true,
                CropLeft = r.X, CropTop = r.Y, CropRight = m.Bounds.Width - r.X - r.Width, CropBottom = m.Bounds.Height - r.Y - r.Height,
            };
        return new ScreenPick { Kind = ScreenPick.Kinds.Monitor, Monitor = m.Handle, SourceWidth = m.Bounds.Width, SourceHeight = m.Bounds.Height, Title = m.Name, Smooth = Smooth.IsChecked == true };
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;
        if (_chosen.Window is { } w && !ScreenSources.Exists(w.Handle))
        {
            MessageBox.Show(this, "That window was closed meanwhile. Pick another one.", "Share your screen", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshWindows(); Choose(null);
            return;
        }
        Result = ToPick(_chosen);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Another quality: another recorded size.</summary>
    private void OnRecordChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.RecordViewModel.Quality) && _chosen is not null) Choose(_chosen);
    }
    private void SmoothLabel_Click(object sender, MouseButtonEventArgs e) => Smooth.IsChecked = Smooth.IsChecked != true;
}

/// <summary>
/// Marks a part of a monitor with the mouse. The monitor dims; then:
/// - drag a rectangle: its edges snap to the edges of windows and of the screen (within a few pixels), Shift keeps
///   it 16:9, its size shows while dragging;
/// - or point at a window (it lights up) and click: exactly that window's area;
/// - or pick a size above (1280 × 720, 1920 × 1080, 1080 × 1080): a frame of that size follows the mouse, a click
///   puts it there. Common sizes make tutorial videos come out a standard size.
/// Esc or a right click cancels. The result is in real pixels, relative to the monitor.
/// </summary>
public sealed class RegionSelectWindow : Window
{
    private const int Snap = 10; // real pixels
    private static readonly (string Label, int W, int H)[] Presets = { ("Free", 0, 0), ("1280 × 720", 1280, 720), ("1920 × 1080", 1920, 1080), ("1080 × 1080", 1080, 1080) };

    private readonly MonitorInfo _monitor;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _box = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Rectangle _hover = new() { Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x2D, 0xD4, 0xBF)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0x2D, 0xD4, 0xBF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _sizeTag = new() { Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x11, 0x20)), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 3), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
    private readonly Border _toolbar;
    private readonly List<Int32Rect> _windows = new();   // visible windows on this monitor, top first (real pixels, relative)
    private readonly int[] _xs, _ys;                      // the edges to snap to
    private Point? _startPx;                              // where a drag began (real pixels)
    private bool _dragged;
    private (int W, int H) _preset;
    private Int32Rect? _result;
    private DpiScale _dpi = new(1, 1);

    private RegionSelectWindow(MonitorInfo monitor, IEnumerable<IntPtr> skip)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Topmost = true; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 200; Height = 200;

        // windows on this monitor (for snapping and for "click a window")
        var b = monitor.Bounds;
        foreach (var w in ScreenSources.Windows(skip))
        {
            var r = w.Bounds;
            int x0 = Math.Max(r.X, b.X), y0 = Math.Max(r.Y, b.Y), x1 = Math.Min(r.X + r.Width, b.X + b.Width), y1 = Math.Min(r.Y + r.Height, b.Y + b.Height);
            if (x1 - x0 >= 64 && y1 - y0 >= 64) _windows.Add(new Int32Rect(x0 - b.X, y0 - b.Y, x1 - x0, y1 - y0));
        }
        _xs = _windows.SelectMany(r => new[] { r.X, r.X + r.Width }).Append(0).Append(b.Width).Distinct().ToArray();
        _ys = _windows.SelectMany(r => new[] { r.Y, r.Y + r.Height }).Append(0).Append(b.Height).Distinct().ToArray();

        // the toolbar: sizes and what to do
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in Presets)
        {
            var fits = p.W <= b.Width && p.H <= b.Height;
            var chip = new RadioButton
            {
                Content = p.Label, GroupName = "size", IsChecked = p.W == 0, IsEnabled = fits, Margin = new Thickness(0, 0, 14, 0), Foreground = Brushes.White, Cursor = Cursors.Hand,
                ToolTip = p.W == 0 ? "Drag any rectangle (Shift: 16:9), or click a window" : fits ? $"A {p.Label} frame follows the mouse; click to put it there" : "Larger than this screen",
            };
            var preset = (p.W, p.H);
            chip.Checked += (_, _) => { _preset = preset; _box.Visibility = Visibility.Collapsed; _hover.Visibility = Visibility.Collapsed; _sizeTag.Visibility = Visibility.Collapsed; Follow(Mouse.GetPosition(_canvas)); };
            row.Children.Add(chip);
        }
        var hint = new TextBlock { Text = "Drag a rectangle, or click a window.  Esc cancels.", Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), FontSize = 12, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(hint);
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x11, 0x20)), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 9, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0), Child = row, Cursor = Cursors.Arrow,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        _sizeTag.Child = _sizeText;
        _canvas.Children.Add(_hover); _canvas.Children.Add(_box); _canvas.Children.Add(_sizeTag);
        var root = new Grid();
        root.Children.Add(_canvas); root.Children.Add(_toolbar);
        Content = root;
        SourceInitialized += (_, _) => { ScreenSources.HideFromCapture(this); ScreenSources.MoveTo(this, monitor.Bounds); };
        Loaded += (_, _) => { ScreenSources.MoveTo(this, monitor.Bounds); _dpi = VisualTreeHelper.GetDpi(this); Activate(); Focus(); Follow(Mouse.GetPosition(_canvas)); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key is Key.LeftShift or Key.RightShift && _startPx is { } s) Draw(s, ToPx(Mouse.GetPosition(_canvas))); };
        MouseRightButtonDown += (_, _) => Close();
        MouseLeftButtonDown += OnDown;
        MouseMove += (_, e) => { if (_startPx is { } s) { _dragged |= Distance(s, ToPx(e.GetPosition(_canvas))) > 4; Draw(s, ToPx(e.GetPosition(_canvas))); } else Follow(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += OnUp;
    }

    private bool OnToolbar(MouseEventArgs e) => _toolbar.IsMouseOver;
    private Point ToPx(Point dip) => new(dip.X * _dpi.DpiScaleX, dip.Y * _dpi.DpiScaleY);
    private Rect ToDip(Int32Rect px) => new(px.X / _dpi.DpiScaleX, px.Y / _dpi.DpiScaleY, px.Width / _dpi.DpiScaleX, px.Height / _dpi.DpiScaleY);
    private static double Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    /// <summary>Not dragging: a preset frame follows the mouse, or the window under it lights up.</summary>
    private void Follow(Point dip)
    {
        if (_toolbar.IsMouseOver) return;
        var p = ToPx(dip);
        if (_preset.W > 0)
        {
            var r = PresetAt(p);
            Show(_box, r); Tag(r, _preset.W == _preset.H ? "square" : null);
            return;
        }
        if (WindowAt(p) is { } w) { Show(_hover, w); Tag(w, "click: this window"); }
        else { _hover.Visibility = Visibility.Collapsed; _sizeTag.Visibility = Visibility.Collapsed; }
    }

    private Int32Rect? WindowAt(Point px) => _windows.Cast<Int32Rect?>().FirstOrDefault(r => px.X >= r!.Value.X && px.X < r.Value.X + r.Value.Width && px.Y >= r.Value.Y && px.Y < r.Value.Y + r.Value.Height);

    private Int32Rect PresetAt(Point px)
    {
        var b = _monitor.Bounds;
        var x = (int)Math.Clamp(px.X - _preset.W / 2.0, 0, b.Width - _preset.W);
        var y = (int)Math.Clamp(px.Y - _preset.H / 2.0, 0, b.Height - _preset.H);
        return new Int32Rect(x, y, _preset.W, _preset.H);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (OnToolbar(e)) return;
        var p = ToPx(e.GetPosition(_canvas));
        if (_preset.W > 0) { _result = PresetAt(p); Close(); return; }
        _startPx = new Point(SnapTo(p.X, _xs), SnapTo(p.Y, _ys));
        _dragged = false;
        CaptureMouse();
        _hover.Visibility = Visibility.Collapsed;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPx is not { } s) return;
        ReleaseMouseCapture();
        _startPx = null;
        var p = ToPx(e.GetPosition(_canvas));
        // a click (no drag) on a window: exactly its area
        if (!_dragged) { if (WindowAt(p) is { } w) { _result = Even(w); Close(); } return; }
        _result = Rect(s, p);
        if (_result is null) { _box.Visibility = Visibility.Collapsed; _sizeTag.Visibility = Visibility.Collapsed; return; } // too small to be meant
        Close();
    }

    private static int SnapTo(double v, int[] edges)
    {
        var best = (int)Math.Round(v); var d = Snap + 1.0;
        foreach (var e in edges) if (Math.Abs(e - v) < d) { d = Math.Abs(e - v); best = e; }
        return best;
    }

    /// <summary>The rectangle from where the drag began to the mouse: edges snapped, Shift keeps 16:9, even sizes, at least 64 × 64.</summary>
    private Int32Rect? Rect(Point a, Point bPt)
    {
        var m = _monitor.Bounds;
        double x2 = SnapTo(Math.Clamp(bPt.X, 0, m.Width), _xs), y2 = SnapTo(Math.Clamp(bPt.Y, 0, m.Height), _ys);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var w = Math.Abs(x2 - a.X); var h = w * 9 / 16;
            y2 = a.Y + Math.Sign(y2 - a.Y == 0 ? 1 : y2 - a.Y) * h;
        }
        int x = (int)Math.Min(a.X, x2), y = (int)Math.Min(a.Y, y2), wd = (int)Math.Abs(a.X - x2), ht = (int)Math.Abs(a.Y - y2);
        x = Math.Clamp(x, 0, m.Width - 1); y = Math.Clamp(y, 0, m.Height - 1);
        wd = Math.Min(wd, m.Width - x); ht = Math.Min(ht, m.Height - y);
        var r = Even(new Int32Rect(x, y, wd, ht));
        return r.Width < 64 || r.Height < 64 ? null : r;
    }

    private static Int32Rect Even(Int32Rect r) => new(r.X, r.Y, r.Width & ~1, r.Height & ~1);

    private void Draw(Point a, Point bPt)
    {
        if (Rect(a, bPt) is { } r) { Show(_box, r); Tag(r, null); }
        else { _box.Visibility = Visibility.Collapsed; _sizeTag.Visibility = Visibility.Collapsed; }
    }

    private void Show(Rectangle shape, Int32Rect px)
    {
        var d = ToDip(px);
        Canvas.SetLeft(shape, d.X); Canvas.SetTop(shape, d.Y); shape.Width = d.Width; shape.Height = d.Height;
        shape.Visibility = Visibility.Visible;
    }

    private void Tag(Int32Rect px, string? note)
    {
        _sizeText.Text = $"{px.Width} × {px.Height}" + (note is null ? "" : "  ·  " + note);
        var d = ToDip(px);
        Canvas.SetLeft(_sizeTag, d.X + 6); Canvas.SetTop(_sizeTag, d.Y > 30 ? d.Y - 26 : d.Y + 6);
        _sizeTag.Visibility = Visibility.Visible;
    }

    public static Int32Rect? Select(MonitorInfo monitor)
    {
        var own = Application.Current?.Windows.OfType<Window>().Select(w => new System.Windows.Interop.WindowInteropHelper(w).Handle).Where(h => h != IntPtr.Zero).ToList() ?? new();
        var w = new RegionSelectWindow(monitor, own);
        w.ShowDialog();
        return w._result;
    }
}
