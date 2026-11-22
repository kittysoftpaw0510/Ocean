using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

/// <summary>
/// A sharing session's board. The drawing: ink strokes, shapes, texts and pictures from everybody, each in its
/// author's colour, on a surface <see cref="BoardLimits.Width"/> units wide that is scaled to the panel's width (so
/// it looks the same on every PC, whatever the panel's size) and grows downwards. Pen, shapes (rectangle, ellipse,
/// line, arrow), text (click where it goes; your own text can be changed; others see it as it is typed), eraser
/// (wipes away what it touches, anybody's), paste (a picture or text, also Ctrl+V), undo and redo (my own changes),
/// clear (for everybody; undo brings it back). The text page: one text everybody types in, with the others' carets.
/// </summary>
public partial class BoardView : UserControl
{
    private static readonly Guid IdKey = new("6f0c8a52-3b1e-4c55-9b77-0d1f2a6b8e41");
    private const double PenSize = 3, TextSize = 18;
    /// <summary>The shape drawn last (the next one is the same).</summary>
    private static string _shape = "rect";

    private BoardVm? _vm;
    private readonly Dictionary<string, Stroke> _strokes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _elements = new(StringComparer.Ordinal);
    private Point _insert = new(20, 20);
    private bool _hover;
    // a text on the drawing being typed: its id once it went out, what it was before, what went out last
    private TextBox? _editing;
    private string? _editingId;
    private BoardItemDto? _editBefore;
    private string? _editSent;
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // a shape being drawn
    private Point? _shapeFrom;
    private Stroke? _shapePreview;
    private Popup? _shapes;
    // one sweep of the eraser is one step to undo
    private readonly List<BoardChange> _erased = new();
    // the text page shows a change that isn't mine (not to be sent again)
    private bool _mirroring;
    private bool _caretsDue;
    private readonly Dictionary<string, (Rectangle Bar, Border Tag)> _caretMarks = new(StringComparer.Ordinal);
    private string _noticePage = BoardVm.DrawPage;

    /// <summary>The bar's drag area (a floating board moves its window with it).</summary>
    public FrameworkElement DragArea => Grip;

    public BoardView()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, _) => Rescale();
        Ink.StrokeCollected += OnStroke;
        Ink.StrokeErasing += OnErasing;
        Ink.PreviewMouseLeftButtonDown += OnCanvasDown;
        Ink.PreviewMouseMove += OnCanvasMove;
        Ink.PreviewMouseLeftButtonUp += OnCanvasUp;
        Ink.LostMouseCapture += (_, _) => { if (_shapeFrom is not null) EndShape(null); };
        PreviewKeyDown += OnKey;
        MouseEnter += (_, _) => { _hover = true; Fade(); };
        MouseLeave += (_, _) => { _hover = false; Fade(); };
        Ink.DefaultDrawingAttributes = new DrawingAttributes { Width = PenSize, Height = PenSize, FitToCurve = true, IgnorePressure = true, StylusTip = StylusTip.Ellipse };
        _live.Tick += (_, _) => { _live.Stop(); SendLive(); };
        ShapeIcon.Data = ShapeGeometry(_shape);

        PageBox.TextChanged += OnPageTyped;
        PageBox.SelectionChanged += (_, _) => { if (!_mirroring && _vm is not null) _vm.TextPage.MoveCaret(PageBox.CaretIndex); };
        PageBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Carets()));
        PageBox.SizeChanged += (_, _) => Carets();
        PageBox.MaxLength = BoardLimits.MaxPage;
        // a narrow board: Follow shows only its eye
        SizeChanged += (_, _) => FollowText.Visibility = ActualWidth >= 340 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Attach(BoardVm vm)
    {
        Detach();
        _vm = vm;
        DataContext = vm;
        vm.Reset += Rebuild;
        vm.Put += OnPut;
        vm.Removed += OnRemoved;
        vm.PropertyChanged += OnVmChanged;
        vm.FollowTo += OnFollowTo;
        vm.TextPage.Changed += OnPageChanged;
        vm.TextPage.Replaced += OnPageReplaced;
        vm.TextPage.CaretsMoved += Carets;
        vm.TextPage.PropertyChanged += OnTextVmChanged;
        var mine = (Color)ColorConverter.ConvertFromString(vm.MyColor);
        Ink.DefaultDrawingAttributes.Color = mine;
        MyDot.Fill = new SolidColorBrush(mine);
        PageBox.CaretBrush = new SolidColorBrush(mine);
        Rebuild();
        OnPageReplaced();
        Supported();
        ShowPage();
        Fade();
    }

    public void Detach()
    {
        if (_vm is not { } vm) return;
        EndEdit(commit: true);
        vm.Reset -= Rebuild; vm.Put -= OnPut; vm.Removed -= OnRemoved; vm.PropertyChanged -= OnVmChanged;
        vm.FollowTo -= OnFollowTo;
        vm.TextPage.Changed -= OnPageChanged; vm.TextPage.Replaced -= OnPageReplaced; vm.TextPage.CaretsMoved -= Carets; vm.TextPage.PropertyChanged -= OnTextVmChanged;
        if (_shapes is not null) _shapes.IsOpen = false;
        _vm = null;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardVm.Opacity)) Fade();
        else if (e.PropertyName == nameof(BoardVm.Page)) ShowPage();
        else if (e.PropertyName == nameof(BoardVm.Problem))
        {
            ProblemText.Text = _vm?.Problem ?? "";
            ProblemBar.Visibility = string.IsNullOrEmpty(_vm?.Problem) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnTextVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardTextVm.Supported)) Supported();
        else if (e.PropertyName == nameof(BoardTextVm.TypingColor) && _vm?.TextPage.TypingColor is { } c)
            Typing.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
    }

    /// <summary>The background as solid as the slider says; a little more see-through while the mouse is elsewhere.</summary>
    private void Fade()
    {
        var target = (_vm?.Opacity ?? 0.82) * (_hover ? 1 : 0.8);
        Back.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(_hover ? 120 : 600)));
        Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(_hover ? 1 : 0.75, TimeSpan.FromMilliseconds(_hover ? 120 : 600)));
    }

    // ------------------------------------------------------------------ the pages

    private bool OnText => _vm?.Page == BoardVm.TextPageName;

    private void Page_Checked(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Page = ReferenceEquals(sender, TextTab) ? BoardVm.TextPageName : BoardVm.DrawPage;
    }

    private void ShowPage()
    {
        var text = OnText;
        if (text) { EndEdit(commit: true); if (_shapes is not null) _shapes.IsOpen = false; }
        (text ? TextTab : DrawTab).IsChecked = true;
        DrawPagePanel.Visibility = DrawTools.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        TextPagePanel.Visibility = TextTools.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        if (text)
            Dispatcher.BeginInvoke(() => { if (OnText && IsVisible) { PageBox.Focus(); Carets(); } }, DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------ size

    private double Scale => Zoom.ScaleX > 0 ? Zoom.ScaleX : 1;

    private void Rescale()
    {
        // (room for a scroll bar always: the scale doesn't change when one comes or goes, so nothing jumps)
        var w = Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        if (w <= 0) return;
        var s = Math.Max(0.2, w / BoardLimits.Width);
        Zoom.ScaleX = Zoom.ScaleY = s;
        Grow();
    }

    /// <summary>The surface reaches a little below what is on it, and at least to the panel's bottom.</summary>
    private void Grow()
    {
        double bottom = 0;
        foreach (var st in _strokes.Values) bottom = Math.Max(bottom, st.GetBounds().Bottom);
        foreach (var el in _elements.Values) bottom = Math.Max(bottom, InkCanvas.GetTop(el) + (el.ActualHeight > 0 ? el.ActualHeight : el.Height is var h && !double.IsNaN(h) ? h : 60));
        // as high as the panel, or higher only when what is on it needs the room (then it scrolls)
        var visible = Math.Floor(Scroller.ActualHeight / Scale) - 1;
        Ink.Height = Math.Max(visible, bottom > visible - 60 ? bottom + 160 : 0);
        Empty.Visibility = _strokes.Count + _elements.Count == 0 && _editing is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ what is on the drawing

    private void Rebuild()
    {
        EndEdit(commit: false);
        Ink.Strokes.Clear();
        foreach (var el in _elements.Values) Ink.Children.Remove(el);
        _strokes.Clear(); _elements.Clear();
        if (_vm is not null) foreach (var it in _vm.Items.ToList()) OnPut(it);
        Grow();
    }

    private void OnPut(BoardItemDto it)
    {
        OnRemoved(it.Id);
        var color = ParseColor(it.Color, it.AuthorId);
        var size = it.Size > 0 ? it.Size : PenSize;
        switch (it.Kind)
        {
            case "ink" when it.Points is { Count: >= 2 } p:
            {
                var pts = new StylusPointCollection();
                for (var i = 0; i + 1 < p.Count; i += 2) pts.Add(new StylusPoint(p[i], p[i + 1]));
                AddStroke(it.Id, new Stroke(pts, new DrawingAttributes { Color = color, Width = size, Height = size, FitToCurve = true, IgnorePressure = true }));
                break;
            }
            case "shape" when it.Points is { Count: 4 } p:
                AddStroke(it.Id, ShapeStroke(it.Shape ?? "rect", new Point(p[0], p[1]), new Point(p[2], p[3]), color, size));
                break;
            case "text":
            {
                var tb = new TextBlock
                {
                    Text = it.Text ?? "", Foreground = new SolidColorBrush(color), FontSize = it.Size > 0 ? it.Size : TextSize, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = it.W > 20 ? it.W : BoardLimits.Width - it.X - 10, FontFamily = (FontFamily)FindResource("UiFont"),
                };
                var box = new Border { Child = tb, Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 3), ToolTip = it.AuthorName, Tag = it.Id };
                Place(box, it);
                // (my own text being typed: the box I type in stands in for it)
                if (it.Id == _editingId) box.Visibility = Visibility.Hidden;
                break;
            }
            case "image" when it.Image is { Length: > 0 } b64:
            {
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(Convert.FromBase64String(b64)))
                    {
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit();
                    }
                    bmp.Freeze();
                    var img = new Image { Source = bmp, Width = it.W > 0 ? it.W : 300, Height = it.H > 0 ? it.H : double.NaN, Stretch = Stretch.Uniform, ToolTip = it.AuthorName, Tag = it.Id };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Place(img, it);
                }
                catch { /* a picture that can't be read: left out */ }
                break;
            }
        }
        Grow();
    }

    private void AddStroke(string id, Stroke stroke)
    {
        stroke.AddPropertyData(IdKey, id);
        _strokes[id] = stroke;
        Ink.Strokes.Add(stroke);
    }

    private void Place(FrameworkElement el, BoardItemDto it)
    {
        InkCanvas.SetLeft(el, it.X); InkCanvas.SetTop(el, it.Y);
        el.PreviewMouseLeftButtonDown += OnElementDown;
        if (Tool == "eraser") el.Cursor = Cursors.Cross;
        _elements[it.Id] = el;
        Ink.Children.Add(el);
    }

    private void OnRemoved(string id)
    {
        if (_strokes.Remove(id, out var st)) Ink.Strokes.Remove(st);
        if (_elements.Remove(id, out var el)) Ink.Children.Remove(el);
        Grow();
    }

    private static Color ParseColor(string? c, string author)
    {
        try { if (c is { Length: 7 } && c[0] == '#') return (Color)ColorConverter.ConvertFromString(c); } catch { }
        return Controls.InitialsBrushConverter.ColorOf(author);
    }

    // ------------------------------------------------------------------ shapes

    private Geometry ShapeGeometry(string shape) => (Geometry)FindResource(shape switch { "ellipse" => "ShapeEllipse", "line" => "ShapeLine", "arrow" => "ShapeArrow", _ => "ShapeRect" });

    /// <summary>A shape as a stroke (so it looks like ink and the eraser takes it like ink): from one corner (or end) to the other.</summary>
    private static Stroke ShapeStroke(string shape, Point a, Point b, Color color, double size)
    {
        var pts = new StylusPointCollection();
        void Add(double x, double y) => pts.Add(new StylusPoint(x, y));
        switch (shape)
        {
            case "ellipse":
            {
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                const int n = 72;
                for (var i = 0; i <= n; i++) { var t = 2 * Math.PI * i / n; Add(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)); }
                break;
            }
            case "line":
                Add(a.X, a.Y); Add(b.X, b.Y);
                break;
            case "arrow":
            {
                Add(a.X, a.Y); Add(b.X, b.Y);
                var len = (b - a).Length;
                if (len > 0.5)
                {
                    var head = Math.Clamp(len * 0.35, 6, 9 + 2.5 * size);
                    var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    foreach (var side in new[] { -1, 1 })
                    {
                        var t = angle + Math.PI - side * 0.5;
                        Add(b.X + head * Math.Cos(t), b.Y + head * Math.Sin(t));
                        if (side < 0) Add(b.X, b.Y);
                    }
                }
                break;
            }
            default: // rect
                Add(a.X, a.Y); Add(b.X, a.Y); Add(b.X, b.Y); Add(a.X, b.Y); Add(a.X, a.Y);
                break;
        }
        return new Stroke(pts, new DrawingAttributes { Color = color, Width = size, Height = size, FitToCurve = false, IgnorePressure = true, StylusTip = StylusTip.Ellipse });
    }

    /// <summary>Where the shape ends: with Shift, a square or a circle, or a line at a multiple of 45°.</summary>
    private static Point ShapeEnd(string shape, Point a, Point b)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return b;
        var d = b - a;
        if (shape is "rect" or "ellipse")
        {
            var side = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            return new Point(a.X + Math.CopySign(side, d.X), a.Y + Math.CopySign(side, d.Y));
        }
        var angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(a.X + d.Length * Math.Cos(angle), a.Y + d.Length * Math.Sin(angle));
    }

    private void EndShape(Point? at)
    {
        if (_shapeFrom is not { } from) return;
        _shapeFrom = null;
        if (_shapePreview is not null) { Ink.Strokes.Remove(_shapePreview); _shapePreview = null; }
        if (Ink.IsMouseCaptured) Ink.ReleaseMouseCapture();
        if (at is not { } end || _vm is null) return;
        end = ShapeEnd(_shape, from, end);
        if ((end - from).Length < 4) return; // (a click, not a shape)
        var id = NewId();
        var pts = new List<double> { Math.Round(from.X, 1), Math.Round(from.Y, 1), Math.Round(end.X, 1), Math.Round(end.Y, 1) };
        var item = new BoardItemDto
        {
            Id = id, Kind = "shape", Shape = _shape, Points = pts, Size = PenSize, Color = _vm.MyColor, AuthorId = _vm.MyId, AuthorName = _vm.MyName,
            X = Math.Min(from.X, end.X), Y = Math.Min(from.Y, end.Y), W = Math.Abs(end.X - from.X), H = Math.Abs(end.Y - from.Y),
        };
        OnPut(item); // (shown at once; the board's answer replaces it)
        _vm.Record(new BoardChange(id, null, item));
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
    }

    private void ShapeTool_Click(object sender, RoutedEventArgs e) => OpenShapes();

    /// <summary>The choice of shapes, under the shape tool (kept out of the capture like the board).</summary>
    private void OpenShapes()
    {
        if (_shapes is null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (shape, name) in new[] { ("rect", "Rectangle (R)"), ("ellipse", "Ellipse (O)"), ("line", "Line (L)"), ("arrow", "Arrow (A)") })
            {
                var b = new RadioButton
                {
                    Style = (Style)FindResource("Tool"), GroupName = "BoardShape", ToolTip = name, Tag = shape,
                    Content = new Path { Style = (Style)FindResource("Icon"), Data = ShapeGeometry(shape) },
                };
                System.Windows.Automation.AutomationProperties.SetName(b, name);
                b.Click += (_, _) => { ChooseShape(shape); _shapes!.IsOpen = false; };
                row.Children.Add(b);
            }
            _shapes = new Popup
            {
                PlacementTarget = ShapeTool, Placement = PlacementMode.Bottom, VerticalOffset = 4, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
                Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0E, 0x15, 0x22)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = row },
            };
            _shapes.Opened += (_, _) => { if (PresentationSource.FromVisual(_shapes.Child) is HwndSource src) ScreenSources.HideFromCapture(src.Handle); };
        }
        foreach (RadioButton b in ((StackPanel)((Border)_shapes.Child).Child).Children) b.IsChecked = (string)b.Tag == _shape;
        _shapes.IsOpen = !_shapes.IsOpen;
    }

    private void ChooseShape(string shape)
    {
        _shape = shape;
        ShapeIcon.Data = ShapeGeometry(shape);
        if (ShapeTool.IsChecked != true) ShapeTool.IsChecked = true;
    }

    // ------------------------------------------------------------------ tools

    private string Tool => EraserTool.IsChecked == true ? "eraser" : TextTool.IsChecked == true ? "text" : ShapeTool.IsChecked == true ? "shape" : "pen";

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (Ink is null) return;
        EndEdit(commit: true);
        Ink.EditingMode = Tool switch { "pen" => InkCanvasEditingMode.Ink, "eraser" => InkCanvasEditingMode.EraseByStroke, _ => InkCanvasEditingMode.None };
        Ink.Cursor = Tool switch { "pen" => Cursors.Pen, "text" => Cursors.IBeam, _ => Cursors.Cross };
        // (with the eraser, texts and pictures are wiped by a click on them)
        foreach (var el in _elements.Values) el.Cursor = Tool == "eraser" ? Cursors.Cross : null;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var undo = ctrl && e.Key == Key.Z;
        var redo = (ctrl && e.Key == Key.Y) || (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z);
        if (OnText)
        {
            // the page types; only undo and redo are the board's (mine, not the others' typing)
            if (undo) { UndoText(); e.Handled = true; }
            else if (redo) { RedoText(); e.Handled = true; }
            return;
        }
        if (_editing is not null)
        {
            if (e.Key == Key.Escape) { EndEdit(commit: false); e.Handled = true; }
            else if (e.Key == Key.Enter && ctrl) { EndEdit(commit: true); e.Handled = true; }
            return;
        }
        if (undo) { _ = _vm?.UndoAsync(); e.Handled = true; return; }
        if (redo) { _ = _vm?.RedoAsync(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _shapeFrom is not null) { EndShape(null); e.Handled = true; return; }
        if (e.Key == Key.V && ctrl) { Paste(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.P: PenTool.IsChecked = true; break;
            case Key.T: TextTool.IsChecked = true; break;
            case Key.E: EraserTool.IsChecked = true; break;
            case Key.R: ChooseShape("rect"); break;
            case Key.O: ChooseShape("ellipse"); break;
            case Key.L: ChooseShape("line"); break;
            case Key.A: ChooseShape("arrow"); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnStroke(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_vm is null) return;
        var id = NewId();
        e.Stroke.AddPropertyData(IdKey, id);
        _strokes[id] = e.Stroke;
        var pts = new List<double>(e.Stroke.StylusPoints.Count * 2);
        foreach (var p in e.Stroke.StylusPoints) { pts.Add(Math.Round(p.X, 1)); pts.Add(Math.Round(p.Y, 1)); }
        if (pts.Count > BoardLimits.MaxPoints) pts = pts.Take(BoardLimits.MaxPoints).ToList();
        var b = e.Stroke.GetBounds();
        var item = new BoardItemDto { Id = id, Kind = "ink", Points = pts, Size = PenSize, Color = _vm.MyColor, X = b.X, Y = b.Y, W = b.Width, H = b.Height, AuthorId = _vm.MyId, AuthorName = _vm.MyName };
        _vm.Record(new BoardChange(id, null, item));
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
        Grow();
    }

    private void OnErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        if (_vm is null) return;
        if (e.Stroke.ContainsPropertyData(IdKey) && e.Stroke.GetPropertyData(IdKey) is string id)
        {
            _strokes.Remove(id);
            if (_vm.Find(id) is { } was) _erased.Add(new BoardChange(id, was, null));
            _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = id });
        }
    }

    /// <summary>A click on a text or a picture: the eraser wipes it; the text tool edits my own text.</summary>
    private void OnElementDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: string id } el) return;
        if (Tool == "eraser")
        {
            e.Handled = true;
            if (_vm.Find(id) is { } was) _vm.Record(new BoardChange(id, was, null));
            OnRemoved(id);
            _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = id });
        }
        else if (Tool == "text" && _vm.Find(id) is { Kind: "text" } it && it.AuthorId == _vm.MyId)
        {
            e.Handled = true;
            StartEdit(new Point(it.X, it.Y), it);
        }
    }

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_shapes is { IsOpen: true }) _shapes.IsOpen = false;
        var p = e.GetPosition(Ink);
        if (Tool == "shape")
        {
            e.Handled = true;
            _insert = p;
            _shapeFrom = p;
            Ink.CaptureMouse();
            return;
        }
        if (Tool != "text") { _insert = p; return; }
        if (e.OriginalSource is DependencyObject d && _editing is not null && IsInside(d, _editing)) return;
        e.Handled = true;
        if (_editing is not null) { EndEdit(commit: true); return; } // (a click elsewhere finishes the text)
        _insert = p;
        StartEdit(p, null);
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (_shapeFrom is not { } from || _vm is null) return;
        var end = ShapeEnd(_shape, from, e.GetPosition(Ink));
        if (_shapePreview is not null) Ink.Strokes.Remove(_shapePreview);
        _shapePreview = (end - from).Length < 1 ? null : ShapeStroke(_shape, from, end, (Color)ColorConverter.ConvertFromString(_vm.MyColor), PenSize);
        if (_shapePreview is not null) Ink.Strokes.Add(_shapePreview);
    }

    private void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (_shapeFrom is not null) { EndShape(e.GetPosition(Ink)); e.Handled = true; }
        // one sweep of the eraser: one step to undo
        if (_erased.Count > 0 && _vm is not null) { _vm.Record(_erased.ToList()); _erased.Clear(); }
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x)) if (ReferenceEquals(x, parent)) return true;
        return false;
    }

    // ------------------------------------------------------------------ a text on the drawing

    private void StartEdit(Point at, BoardItemDto? existing)
    {
        EndEdit(commit: true);
        if (_vm is null) return;
        var color = (Color)ColorConverter.ConvertFromString(_vm.MyColor);
        _editingId = existing?.Id;
        _editBefore = existing;
        _editSent = existing?.Text;
        if (existing is not null && _elements.TryGetValue(existing.Id, out var shown)) shown.Visibility = Visibility.Hidden;
        _editing = new TextBox
        {
            Text = existing?.Text ?? "", FontSize = existing?.Size > 0 ? existing.Size : TextSize, Foreground = new SolidColorBrush(color), CaretBrush = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 120, MaxWidth = Math.Max(140, BoardLimits.Width - at.X - 10), Padding = new Thickness(4, 2, 4, 2),
            FontFamily = (FontFamily)FindResource("UiFont"), MaxLength = BoardLimits.MaxText,
        };
        _editing.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (_editing is not null && !_editing.IsKeyboardFocusWithin) EndEdit(commit: true); });
        // others see it as it is typed
        _editing.TextChanged += (_, _) => { _live.Stop(); _live.Start(); };
        InkCanvas.SetLeft(_editing, at.X); InkCanvas.SetTop(_editing, at.Y);
        Ink.Children.Add(_editing);
        Empty.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => { _editing?.Focus(); _editing?.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>The text being typed as it is now (null: empty).</summary>
    private BoardItemDto? EditedItem()
    {
        if (_editing is not { } box || _vm is null) return null;
        var text = box.Text.TrimEnd();
        if (text.Length == 0) return null;
        var at = new Point(InkCanvas.GetLeft(box), InkCanvas.GetTop(box));
        var width = Math.Max(box.ActualWidth, 120);
        _editingId ??= NewId();
        return new BoardItemDto { Id = _editingId, Kind = "text", X = at.X, Y = at.Y, W = Math.Min(width, BoardLimits.Width - at.X), Size = box.FontSize, Color = _vm.MyColor, Text = text, AuthorId = _vm.MyId, AuthorName = _vm.MyName };
    }

    /// <summary>What is typed so far, to everybody (a moment after the last key).</summary>
    private void SendLive()
    {
        if (_vm is null || EditedItem() is not { } item || item.Text == _editSent) return;
        _editSent = item.Text;
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
    }

    private void EndEdit(bool commit)
    {
        if (_editing is not { } box) return;
        _live.Stop();
        var item = commit ? EditedItem() : null;
        var id = item?.Id ?? _editingId;
        var before = _editBefore;
        var sent = _editSent;
        _editing = null; _editingId = null; _editBefore = null; _editSent = null;
        Ink.Children.Remove(box);
        if (id is not null && _elements.TryGetValue(id, out var shown)) shown.Visibility = Visibility.Visible;
        if (_vm is null) { Grow(); return; }
        if (!commit)
        {
            // given up: as it was before (others may have seen some of it)
            if (id is not null && sent != before?.Text)
            {
                if (before is null) { OnRemoved(id); _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = id }); }
                else { OnPut(before); _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = before }); }
            }
            Grow();
            return;
        }
        if (item is null)
        {
            // emptied: it goes
            if (id is not null && (before is not null || sent is not null))
            {
                OnRemoved(id);
                _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = id });
                if (before is not null) _vm.Record(new BoardChange(id, before, null));
            }
            Grow();
            return;
        }
        if (before?.Text != item.Text) _vm.Record(new BoardChange(item.Id, before, item));
        if (sent != item.Text || before is null && sent is null) { OnPut(item); _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item }); }
        Grow();
    }

    // ------------------------------------------------------------------ paste, undo, clear, close

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();

    /// <summary>A picture (a screenshot, a copied image, an image file) or text from the clipboard, where I last clicked.</summary>
    private void Paste()
    {
        if (_vm is null) return;
        try
        {
            BitmapSource? image = null;
            if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
            else if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));
                if (file is not null) { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(file); b.EndInit(); image = b; }
            }
            var at = PasteSpot();
            BoardItemDto? item = null;
            if (image is not null)
            {
                var (data, w, h) = Encode(image);
                if (data.Length > BoardLimits.MaxImage) { _vm.Problem = "That picture is too large for the board"; return; }
                // as wide as the board allows from here, never wider than half its pixels (a small picture stays small)
                var boardW = Math.Min(BoardLimits.Width - at.X - 10, Math.Max(80, w * 0.5));
                var boardH = boardW * h / Math.Max(1, w);
                item = new BoardItemDto { Id = NewId(), Kind = "image", X = at.X, Y = at.Y, W = boardW, H = boardH, Image = Convert.ToBase64String(data), AuthorId = _vm.MyId, AuthorName = _vm.MyName };
                _insert = new Point(at.X, at.Y + boardH + 12);
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                if (text.Length > BoardLimits.MaxText) text = text[..BoardLimits.MaxText];
                item = new BoardItemDto { Id = NewId(), Kind = "text", X = at.X, Y = at.Y, W = Math.Min(420, BoardLimits.Width - at.X - 10), Size = TextSize, Color = _vm.MyColor, Text = text, AuthorId = _vm.MyId, AuthorName = _vm.MyName };
            }
            if (item is null) { _vm.Problem = "Nothing to paste: copy a picture or text first"; return; }
            OnPut(item);
            _vm.Record(new BoardChange(item.Id, null, item));
            _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
        }
        catch (Exception ex) { _vm.Problem = "It couldn't be pasted (" + ex.Message + ")"; }
    }

    /// <summary>Where I last clicked, if it is in view; else the top left of what is in view.</summary>
    private Point PasteSpot()
    {
        var top = Scroller.VerticalOffset / Scale;
        var bottom = top + Scroller.ViewportHeight / Scale;
        if (_insert.Y >= top && _insert.Y < bottom - 40) return new Point(Math.Clamp(_insert.X, 0, BoardLimits.Width - 80), _insert.Y);
        return new Point(16, top + 16);
    }

    /// <summary>At most 1600 pixels on its longest side; PNG (sharp text) unless JPEG is much smaller (a photo).</summary>
    private static (byte[] Data, int W, int H) Encode(BitmapSource src)
    {
        var scale = Math.Min(1.0, 1600.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource bmp = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        // (a copied screenshot may have an empty alpha channel: drawn without it)
        bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
        byte[] Save(BitmapEncoder enc) { enc.Frames.Add(BitmapFrame.Create(bmp)); using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray(); }
        var png = Save(new PngBitmapEncoder());
        if (png.Length < 400_000) return (png, bmp.PixelWidth, bmp.PixelHeight);
        var jpg = Save(new JpegBitmapEncoder { QualityLevel = 85 });
        return (jpg.Length < png.Length * 0.6 ? jpg : png, bmp.PixelWidth, bmp.PixelHeight);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (OnText) { UndoText(); return; }
        EndEdit(commit: true);
        _ = _vm?.UndoAsync();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (OnText) { RedoText(); return; }
        EndEdit(commit: true);
        _ = _vm?.RedoAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (OnText)
        {
            if (_vm.ClearText() is { } op) { Mirror(op, caretToChange: true); _noticePage = BoardVm.TextPageName; }
            PageBox.Focus();
            return;
        }
        EndEdit(commit: true);
        _noticePage = BoardVm.DrawPage;
        _ = _vm.ClearAsync();
    }

    private void NoticeUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Unsay();
        if (_noticePage == BoardVm.TextPageName) { _vm.Page = BoardVm.TextPageName; UndoText(); }
        else { _vm.Page = BoardVm.DrawPage; _ = _vm.UndoAsync(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (_vm is not null) _vm.Visible = false; }

    // ------------------------------------------------------------------ follow me

    /// <summary>Everybody else goes to what this panel shows: its page, and where it is scrolled to.</summary>
    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        double top;
        if (OnText) top = Math.Max(0, PageBox.GetCharacterIndexFromPoint(new Point(12, PageBox.Padding.Top + 4), true));
        else { EndEdit(commit: true); top = Scroller.VerticalOffset / Scale; }
        _ = _vm.FollowMeAsync(top);
    }

    /// <summary>Somebody said "follow me" (the page is switched already): to their place, once the page is laid out.</summary>
    private void OnFollowTo(string page, double top)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (page == BoardVm.TextPageName)
            {
                var at = (int)Math.Clamp(top, 0, PageBox.Text.Length);
                var r = PageBox.GetRectFromCharacterIndex(at);
                if (!r.IsEmpty) PageBox.ScrollToVerticalOffset(Math.Max(0, PageBox.VerticalOffset + r.Top - PageBox.Padding.Top));
                Carets();
            }
            else
            {
                EndEdit(commit: true);
                Grow();
                Scroller.ScrollToVerticalOffset(Math.Max(0, top * Scale));
            }
        }, DispatcherPriority.Loaded);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    // ------------------------------------------------------------------ the text page

    private void Supported()
    {
        var ok = _vm?.TextPage.Supported != false;
        PageOld.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        PageBox.IsReadOnly = !ok;
    }

    /// <summary>I typed: the change goes to everybody.</summary>
    private void OnPageTyped(object sender, TextChangedEventArgs e)
    {
        if (_mirroring || _vm is null) return;
        var before = _vm.TextPage.Text;
        var after = PageBox.Text;
        PageEmpty.Visibility = after.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (before == after) return;
        TextOp? op = null;
        if (e.Changes.Count == 1 && e.Changes.First() is var c && c.Offset >= 0 && c.Offset + c.RemovedLength <= before.Length
            && before.Length - c.RemovedLength + c.AddedLength == after.Length)
        {
            var guess = TextOp.Splice(before.Length, c.Offset, c.RemovedLength, after.Substring(c.Offset, c.AddedLength));
            if (guess.Apply(before) == after) op = guess;
        }
        op ??= TextOp.Diff(before, after);
        _vm.TextPage.Local(op, PageBox.CaretIndex);
    }

    /// <summary>Somebody else's change (or my undo): into the page, my caret and selection staying where they were in the text.</summary>
    private void OnPageChanged(TextOp op) => Mirror(op, caretToChange: false);

    private void Mirror(TextOp op, bool caretToChange)
    {
        if (_vm is null) return;
        _mirroring = true;
        try
        {
            int selStart = PageBox.SelectionStart, selEnd = selStart + PageBox.SelectionLength;
            var scroll = PageBox.VerticalOffset;
            var at = 0;
            PageBox.BeginChange();
            try
            {
                foreach (var p in op.Parts)
                {
                    if (p.IsKeep) at += p.N;
                    else if (p.IsInsert) { PageBox.Select(at, 0); PageBox.SelectedText = p.S; at += p.S!.Length; }
                    else { PageBox.Select(at, -p.N); PageBox.SelectedText = ""; }
                }
            }
            finally { PageBox.EndChange(); }
            if (PageBox.Text != _vm.TextPage.Text) PageBox.Text = _vm.TextPage.Text; // (never: but the page must be the text)
            if (caretToChange)
            {
                PageBox.Select(Math.Min(BoardTextVm.EndOf(op), PageBox.Text.Length), 0);
                var r = PageBox.GetRectFromCharacterIndex(PageBox.CaretIndex);
                if (!r.IsEmpty && (r.Top < 0 || r.Bottom > PageBox.ActualHeight)) PageBox.ScrollToVerticalOffset(Math.Max(0, scroll + r.Top - PageBox.ActualHeight / 3));
            }
            else
            {
                int s = Math.Min(op.TransformIndex(selStart), PageBox.Text.Length), en = Math.Min(op.TransformIndex(selEnd), PageBox.Text.Length);
                PageBox.Select(s, Math.Max(0, en - s));
                PageBox.ScrollToVerticalOffset(scroll);
            }
        }
        finally { _mirroring = false; }
        PageEmpty.Visibility = PageBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Carets();
    }

    private void OnPageReplaced()
    {
        if (_vm is null) return;
        _mirroring = true;
        try
        {
            var caret = PageBox.CaretIndex;
            PageBox.Text = _vm.TextPage.Text;
            PageBox.CaretIndex = Math.Min(caret, PageBox.Text.Length);
        }
        finally { _mirroring = false; }
        PageEmpty.Visibility = PageBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Carets();
    }

    private void UndoText() { if (_vm?.TextPage.Undo() is { } op) Mirror(op, caretToChange: true); }
    private void RedoText() { if (_vm?.TextPage.Redo() is { } op) Mirror(op, caretToChange: true); }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.TextPage.Text.Length == 0) return;
        try { Clipboard.SetText(_vm.TextPage.Text); _vm.Say("The text is copied"); }
        catch (Exception ex) { _vm.Problem = "It couldn't be copied (" + ex.Message + ")"; }
    }

    /// <summary>The others' carets, drawn once the page has laid out what changed.</summary>
    private void Carets()
    {
        if (_caretsDue) return;
        _caretsDue = true;
        Dispatcher.BeginInvoke(DrawCarets, DispatcherPriority.Background);
    }

    private void DrawCarets()
    {
        _caretsDue = false;
        if (_vm is null || !TextPagePanel.IsVisible) return;
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in _vm.TextPage.Carets)
        {
            seen.Add(c.Id);
            if (!_caretMarks.TryGetValue(c.Id, out var mark))
            {
                var brush = new SolidColorBrush(Controls.InitialsBrushConverter.ColorOf(c.Id));
                mark = (new Rectangle { Width = 2, Fill = brush },
                        new Border { Background = brush, CornerRadius = new CornerRadius(3, 3, 3, 0), Padding = new Thickness(4, 0, 4, 1),
                                     Child = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White } });
                CaretLayer.Children.Add(mark.Bar); CaretLayer.Children.Add(mark.Tag);
                _caretMarks[c.Id] = mark;
            }
            ((TextBlock)mark.Tag.Child).Text = c.Name;
            var r = PageBox.GetRectFromCharacterIndex(Math.Min(c.Index, PageBox.Text.Length));
            if (r.IsEmpty || r.Bottom < 0 || r.Top > PageBox.ActualHeight) { mark.Bar.Visibility = mark.Tag.Visibility = Visibility.Collapsed; continue; }
            var p = PageBox.TranslatePoint(r.TopLeft, CaretLayer);
            mark.Bar.Visibility = Visibility.Visible;
            mark.Bar.Height = Math.Max(10, r.Height);
            mark.Bar.Opacity = now - c.LastSeen < TimeSpan.FromSeconds(60) ? 1 : 0.45;
            Canvas.SetLeft(mark.Bar, p.X - 1); Canvas.SetTop(mark.Bar, p.Y);
            // their name a moment after they type (and while the mouse is near: the tooltip of a caret)
            var named = now - c.LastEdit < TimeSpan.FromSeconds(2.5);
            mark.Tag.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
            if (named)
            {
                mark.Tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(mark.Tag, Math.Min(p.X - 1, Math.Max(0, CaretLayer.ActualWidth - mark.Tag.DesiredSize.Width)));
                Canvas.SetTop(mark.Tag, p.Y >= mark.Tag.DesiredSize.Height ? p.Y - mark.Tag.DesiredSize.Height : p.Y + Math.Max(10, r.Height));
            }
        }
        foreach (var gone in _caretMarks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var (bar, tag) = _caretMarks[gone];
            CaretLayer.Children.Remove(bar); CaretLayer.Children.Remove(tag);
            _caretMarks.Remove(gone);
        }
    }
}
