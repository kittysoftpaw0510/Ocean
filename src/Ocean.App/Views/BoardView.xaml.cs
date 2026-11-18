using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ocean.App.ViewModels;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

/// <summary>
/// A sharing session's board, drawn: ink strokes, texts and pictures from everybody, each in its author's colour,
/// on a surface <see cref="BoardLimits.Width"/> units wide that is scaled to the panel's width (so it looks the
/// same on every PC, whatever the panel's size) and grows downwards. Pen, text (click where it goes; your own text
/// can be changed), eraser (wipes away what it touches, anybody's), paste (a picture or text, also Ctrl+V).
/// </summary>
public partial class BoardView : UserControl
{
    private static readonly Guid IdKey = new("6f0c8a52-3b1e-4c55-9b77-0d1f2a6b8e41");
    private const double PenSize = 3, TextSize = 18;

    private BoardVm? _vm;
    private readonly Dictionary<string, Stroke> _strokes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _elements = new(StringComparer.Ordinal);
    private Point _insert = new(20, 20);
    private TextBox? _editing;
    private string? _editingId;
    private bool _hover;

    /// <summary>The bar's drag area (a floating board moves its window with it).</summary>
    public FrameworkElement DragArea => Grip;

    public BoardView()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, _) => Rescale();
        Ink.StrokeCollected += OnStroke;
        Ink.StrokeErasing += OnErasing;
        Ink.PreviewMouseLeftButtonDown += OnCanvasDown;
        PreviewKeyDown += OnKey;
        MouseEnter += (_, _) => { _hover = true; Fade(); };
        MouseLeave += (_, _) => { _hover = false; Fade(); };
        Ink.DefaultDrawingAttributes = new DrawingAttributes { Width = PenSize, Height = PenSize, FitToCurve = true, IgnorePressure = true, StylusTip = StylusTip.Ellipse };
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
        var mine = (Color)ColorConverter.ConvertFromString(vm.MyColor);
        Ink.DefaultDrawingAttributes.Color = mine;
        MyDot.Fill = new SolidColorBrush(mine);
        ClearButton.Visibility = vm.IsHost ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
        Fade();
    }

    public void Detach()
    {
        if (_vm is not { } vm) return;
        vm.Reset -= Rebuild; vm.Put -= OnPut; vm.Removed -= OnRemoved; vm.PropertyChanged -= OnVmChanged;
        _vm = null;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardVm.Opacity)) Fade();
        else if (e.PropertyName == nameof(BoardVm.Problem))
        {
            ProblemText.Text = _vm?.Problem ?? "";
            ProblemBar.Visibility = string.IsNullOrEmpty(_vm?.Problem) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The background as solid as the slider says; a little more see-through while the mouse is elsewhere.</summary>
    private void Fade()
    {
        var target = (_vm?.Opacity ?? 0.82) * (_hover ? 1 : 0.8);
        Back.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(_hover ? 120 : 600)));
        Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(_hover ? 1 : 0.75, TimeSpan.FromMilliseconds(_hover ? 120 : 600)));
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

    // ------------------------------------------------------------------ what is on it

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
        switch (it.Kind)
        {
            case "ink" when it.Points is { Count: >= 2 } p:
            {
                var pts = new StylusPointCollection();
                for (var i = 0; i + 1 < p.Count; i += 2) pts.Add(new StylusPoint(p[i], p[i + 1]));
                var size = it.Size > 0 ? it.Size : PenSize;
                var stroke = new Stroke(pts, new DrawingAttributes { Color = color, Width = size, Height = size, FitToCurve = true, IgnorePressure = true });
                stroke.AddPropertyData(IdKey, it.Id);
                _strokes[it.Id] = stroke;
                Ink.Strokes.Add(stroke);
                break;
            }
            case "text":
            {
                var tb = new TextBlock
                {
                    Text = it.Text ?? "", Foreground = new SolidColorBrush(color), FontSize = it.Size > 0 ? it.Size : TextSize, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = it.W > 20 ? it.W : BoardLimits.Width - it.X - 10, FontFamily = (FontFamily)FindResource("UiFont"),
                };
                var box = new Border { Child = tb, Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 3), ToolTip = it.AuthorName, Tag = it.Id };
                Place(box, it);
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

    private void Place(FrameworkElement el, BoardItemDto it)
    {
        InkCanvas.SetLeft(el, it.X); InkCanvas.SetTop(el, it.Y);
        el.PreviewMouseLeftButtonDown += OnElementDown;
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

    // ------------------------------------------------------------------ tools

    private string Tool => EraserTool.IsChecked == true ? "eraser" : TextTool.IsChecked == true ? "text" : "pen";

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
        if (_editing is not null)
        {
            if (e.Key == Key.Escape) { EndEdit(commit: false); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { EndEdit(commit: true); e.Handled = true; }
            return;
        }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { Paste(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.P) PenTool.IsChecked = true;
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.T) TextTool.IsChecked = true;
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.E) EraserTool.IsChecked = true;
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
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = new BoardItemDto { Id = id, Kind = "ink", Points = pts, Size = PenSize, Color = _vm.MyColor, X = b.X, Y = b.Y, W = b.Width, H = b.Height } });
        Grow();
    }

    private void OnErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        if (_vm is null) return;
        if (e.Stroke.ContainsPropertyData(IdKey) && e.Stroke.GetPropertyData(IdKey) is string id)
        {
            _strokes.Remove(id);
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
        var p = e.GetPosition(Ink);
        if (Tool != "text") { _insert = p; return; }
        if (e.OriginalSource is DependencyObject d && _editing is not null && IsInside(d, _editing)) return;
        e.Handled = true;
        if (_editing is not null) { EndEdit(commit: true); return; } // (a click elsewhere finishes the text)
        _insert = p;
        StartEdit(p, null);
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x)) if (ReferenceEquals(x, parent)) return true;
        return false;
    }

    // ------------------------------------------------------------------ text

    private void StartEdit(Point at, BoardItemDto? existing)
    {
        EndEdit(commit: true);
        if (_vm is null) return;
        var color = (Color)ColorConverter.ConvertFromString(_vm.MyColor);
        _editingId = existing?.Id;
        if (existing is not null && _elements.TryGetValue(existing.Id, out var shown)) shown.Visibility = Visibility.Hidden;
        _editing = new TextBox
        {
            Text = existing?.Text ?? "", FontSize = existing?.Size > 0 ? existing.Size : TextSize, Foreground = new SolidColorBrush(color), CaretBrush = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 120, MaxWidth = Math.Max(140, BoardLimits.Width - at.X - 10), Padding = new Thickness(4, 2, 4, 2),
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        _editing.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (_editing is not null && !_editing.IsKeyboardFocusWithin) EndEdit(commit: true); });
        InkCanvas.SetLeft(_editing, at.X); InkCanvas.SetTop(_editing, at.Y);
        Ink.Children.Add(_editing);
        Empty.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => { _editing?.Focus(); _editing?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndEdit(bool commit)
    {
        if (_editing is not { } box) return;
        _editing = null;
        var id = _editingId; _editingId = null;
        var text = box.Text.TrimEnd();
        var at = new Point(InkCanvas.GetLeft(box), InkCanvas.GetTop(box));
        var width = Math.Max(box.ActualWidth, 120);
        Ink.Children.Remove(box);
        if (id is not null && _elements.TryGetValue(id, out var shown)) shown.Visibility = Visibility.Visible;
        if (_vm is null || !commit) { Grow(); return; }
        if (text.Length == 0)
        {
            if (id is not null) { OnRemoved(id); _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Delete, Id = id }); }
            Grow();
            return;
        }
        if (id is not null && _vm.Find(id)?.Text == text) { Grow(); return; }
        var item = new BoardItemDto { Id = id ?? NewId(), Kind = "text", X = at.X, Y = at.Y, W = Math.Min(width, BoardLimits.Width - at.X), Size = box.FontSize, Color = _vm.MyColor, Text = text, AuthorId = _vm.MyId, AuthorName = _vm.MyName };
        OnPut(item); // (shown at once; the board's answer replaces it)
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
    }

    // ------------------------------------------------------------------ paste, clear, hide

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
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (file is not null) { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(file); b.EndInit(); image = b; }
            }
            var at = PasteSpot();
            if (image is not null)
            {
                var (data, w, h) = Encode(image);
                if (data.Length > BoardLimits.MaxImage) { _vm.Problem = "That picture is too large for the board"; return; }
                // as wide as the board allows from here, never wider than half its pixels (a small picture stays small)
                var boardW = Math.Min(BoardLimits.Width - at.X - 10, Math.Max(80, w * 0.5));
                var boardH = boardW * h / Math.Max(1, w);
                var item = new BoardItemDto { Id = NewId(), Kind = "image", X = at.X, Y = at.Y, W = boardW, H = boardH, Image = Convert.ToBase64String(data), AuthorId = _vm.MyId, AuthorName = _vm.MyName };
                OnPut(item);
                _insert = new Point(at.X, at.Y + boardH + 12);
                _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
                return;
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                if (text.Length > BoardLimits.MaxText) text = text[..BoardLimits.MaxText];
                var item = new BoardItemDto { Id = NewId(), Kind = "text", X = at.X, Y = at.Y, W = Math.Min(420, BoardLimits.Width - at.X - 10), Size = TextSize, Color = _vm.MyColor, Text = text, AuthorId = _vm.MyId, AuthorName = _vm.MyName };
                OnPut(item);
                _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Put, Item = item });
                return;
            }
            _vm.Problem = "Nothing to paste: copy a picture or text first";
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

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not { IsHost: true }) return;
        var ask = new ConfirmWindow("Clear the board?", "Everything on it goes, for everybody in this sharing.", "Clear", "Cancel", danger: true) { Owner = Window.GetWindow(this), Topmost = true };
        if (ask.ShowDialog() != true) return;
        _ = _vm.SendAsync(new BoardOpDto { Op = BoardOpDto.Clear });
    }

    private void Hide_Click(object sender, RoutedEventArgs e) { if (_vm is not null) _vm.Visible = false; }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
