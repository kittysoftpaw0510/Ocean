using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// Marks a part of a monitor with the mouse: the monitor dims, a rectangle is dragged, its size shows while
/// dragging. Esc or a click without dragging cancels. The result is in real pixels, relative to the monitor.
/// </summary>
public sealed class RegionSelectWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Rectangle _box = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _size = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed };
    private Point? _start;
    private Int32Rect? _result;

    private RegionSelectWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Topmost = true; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0; Width = 200; Height = 200;
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x11, 0x20)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 9, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock { Text = "Drag to mark the part to share.  Esc cancels.", Foreground = Brushes.White, FontSize = 14 },
        };
        _canvas.Children.Add(_box); _canvas.Children.Add(_size);
        var root = new Grid();
        root.Children.Add(_canvas); root.Children.Add(hint);
        Content = root;
        SourceInitialized += (_, _) => ScreenSources.MoveTo(this, monitor.Bounds);
        Loaded += (_, _) => { ScreenSources.MoveTo(this, monitor.Bounds); Activate(); Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); CaptureMouse(); hint.Visibility = Visibility.Collapsed; };
        MouseMove += (_, e) => { if (_start is { } s) Draw(s, e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } s) return;
            ReleaseMouseCapture();
            _result = ToPixels(s, e.GetPosition(_canvas));
            Close();
        };
        MouseRightButtonDown += (_, _) => Close();
    }

    private void Draw(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        Canvas.SetLeft(_box, x); Canvas.SetTop(_box, y); _box.Width = w; _box.Height = h; _box.Visibility = Visibility.Visible;
        if (ToPixels(a, b) is { } px) { _size.Text = $"{px.Width} × {px.Height}"; _size.Visibility = Visibility.Visible; }
        else _size.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(_size, x + 6); Canvas.SetTop(_size, y > 26 ? y - 22 : y + 6);
    }

    /// <summary>Null when too small to be meant (a click).</summary>
    private Int32Rect? ToPixels(Point a, Point b)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int x = (int)Math.Round(Math.Min(a.X, b.X) * dpi.DpiScaleX), y = (int)Math.Round(Math.Min(a.Y, b.Y) * dpi.DpiScaleY);
        int w = (int)Math.Round(Math.Abs(a.X - b.X) * dpi.DpiScaleX), h = (int)Math.Round(Math.Abs(a.Y - b.Y) * dpi.DpiScaleY);
        x = Math.Clamp(x, 0, _monitor.Bounds.Width - 1); y = Math.Clamp(y, 0, _monitor.Bounds.Height - 1);
        w = Math.Min(w, _monitor.Bounds.Width - x) & ~1; h = Math.Min(h, _monitor.Bounds.Height - y) & ~1;
        return w < 64 || h < 64 ? null : new Int32Rect(x, y, w, h);
    }

    public static Int32Rect? Select(MonitorInfo monitor)
    {
        var w = new RegionSelectWindow(monitor);
        w.ShowDialog();
        return w._result;
    }
}
