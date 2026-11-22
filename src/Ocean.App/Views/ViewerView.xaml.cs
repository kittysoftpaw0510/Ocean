using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.Core.Media;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Ocean.App.Views;

/// <summary>
/// The viewer's view: a picture fitted to the window (the wheel zooms around the mouse, a drag moves it, a double
/// click switches between fitting and 100 %, ←/→ the next and previous picture); a PDF's pages one under the
/// other, each drawn by Windows' PDF engine at exactly the pixels it takes on this screen and only when it comes
/// near the view (Ctrl+wheel or +/− zoom, Page Up/Down, Home/End); or a text (zoom, wrap). Ctrl+F finds words in a
/// PDF (read by Windows' text recognition, page by page, the matches marked on the pages) or a text.
/// </summary>
public partial class ViewerView : UserControl
{
    private ViewerViewModel? _vm;
    // a picture
    private bool _fit = true;
    private Point? _dragFrom;
    private Point _dragOffset;
    // a PDF
    private sealed class Slot
    {
        public int Index;
        public double W, H;          // the page's size (DIPs at 100 %)
        public int PxW, PxH;         // the pixels it takes on the screen at this zoom
        public Grid Frame = null!;
        public Image Image = null!;
        public Canvas Marks = null!;
        public int Rendered;         // the pixel width it was drawn at (0: not drawn)
    }
    private readonly List<Slot> _slots = new();
    private double _pdfZoom = 1;
    private bool _pdfFit = true;
    private bool _renderDue, _rendering, _renderAgain;
    private int _pdfGeneration;
    // a text
    private double _textZoom = 1;
    private const double TextSize = 13.5;
    // finding words
    private sealed class Match { public int Page; public List<Rect> Rects = new(); public int Start, Length; }
    private List<Match> _matches = new();
    private Match? _current;
    private CancellationTokenSource? _findCts;
    private readonly Dictionary<int, Task<PdfText.Page>> _pageText = new();
    private readonly DispatcherTimer _findDelay;

    public ViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ViewerViewModel);
        PreviewKeyDown += OnKey;
        ImageScroll.SizeChanged += (_, _) => { if (_fit) FitPicture(); };
        ImageScroll.PreviewMouseWheel += OnPictureWheel;
        Picture.MouseLeftButtonDown += OnPictureDown;
        Picture.MouseMove += OnPictureMove;
        Picture.MouseLeftButtonUp += (_, _) => { _dragFrom = null; Picture.ReleaseMouseCapture(); };
        PdfScroll.ScrollChanged += (_, _) => { RenderSoon(); UpdatePageText(); };
        PdfScroll.SizeChanged += (_, e) => { if (_pdfFit && e.WidthChanged) FitPdf(); };
        PdfScroll.PreviewMouseWheel += OnPdfWheel;
        TextView.PreviewMouseWheel += OnTextWheel;
        TextView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => DrawTextMarksSoon()));
        TextView.SizeChanged += (_, _) => DrawTextMarksSoon();
        SearchBox.PreviewKeyDown += OnSearchKey;
        _findDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _findDelay.Tick += (_, _) => { _findDelay.Stop(); Find(); };
    }

    private void Attach(ViewerViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is not null) { vm.PropertyChanged += OnVmChanged; ApplyWrap(); }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.IsOpen):
                if (_vm.IsOpen) Dispatcher.BeginInvoke(() => Focus(), DispatcherPriority.Input);
                else ClearPdf();
                break;
            case nameof(ViewerViewModel.Kind):
                KindGlyph.Text = _vm.IsPdf ? "" : _vm.IsText ? "" : "";
                if (_vm.IsText) SetTextZoom(_textZoom);
                break;
            case nameof(ViewerViewModel.Image):
                _fit = true;
                FitPicture();
                break;
            case nameof(ViewerViewModel.Pdf):
                BuildPdf();
                break;
            case nameof(ViewerViewModel.TextContent):
                ClearFind();
                TextView.ScrollToHome();
                break;
            case nameof(ViewerViewModel.Wrap):
                ApplyWrap();
                break;
            case nameof(ViewerViewModel.IsSearchOpen):
                if (_vm.IsSearchOpen) Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
                else { ClearFind(); Focus(); }
                break;
            case nameof(ViewerViewModel.SearchQuery):
                _findDelay.Stop(); _findDelay.Start();
                break;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.F && _vm.CanSearch) { OpenFind(); e.Handled = true; return; }
        if (e.Key == Key.F3 && _vm.IsSearchOpen) { Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; return; }
        // typing in the find box, or moving through the text: the keys are theirs
        if (e.OriginalSource is TextBox && e.Key != Key.Escape) return;
        switch (e.Key)
        {
            case Key.Escape when _vm.IsSearchOpen: _vm.CloseSearchCommand.Execute(null); break;
            case Key.Escape: _vm.CloseCommand.Execute(null); break;
            case Key.Right when _vm.IsImage: _vm.NextCommand.Execute(null); break;
            case Key.Left when _vm.IsImage: _vm.PreviousCommand.Execute(null); break;
            case Key.OemPlus or Key.Add: Zoom(1.25); break;
            case Key.OemMinus or Key.Subtract: Zoom(0.8); break;
            case Key.D0 or Key.NumPad0: FitAll(); break;
            case Key.PageDown when _vm.IsPdf: PdfScroll.PageDown(); break;
            case Key.PageUp when _vm.IsPdf: PdfScroll.PageUp(); break;
            case Key.Home when _vm.IsPdf: PdfScroll.ScrollToTop(); break;
            case Key.End when _vm.IsPdf: PdfScroll.ScrollToBottom(); break;
            case Key.Down when _vm.IsPdf: PdfScroll.LineDown(); break;
            case Key.Up when _vm.IsPdf: PdfScroll.LineUp(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { Zoom(1.25); Focus(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { Zoom(0.8); Focus(); }
    private void Fit_Click(object sender, RoutedEventArgs e) { FitAll(); Focus(); }

    private void Zoom(double by)
    {
        if (_vm?.IsPdf == true) ZoomPdf(_pdfZoom * by);
        else if (_vm?.IsText == true) SetTextZoom(_textZoom * by);
        else ZoomPicture(PictureZoom.ScaleX * by, null);
    }

    private void FitAll()
    {
        if (_vm?.IsPdf == true) { _pdfFit = true; FitPdf(); }
        else if (_vm?.IsText == true) SetTextZoom(1);
        else { _fit = true; FitPicture(); }
    }

    // ------------------------------------------------------------------ a picture

    /// <summary>One picture pixel on one screen pixel, in the image's own units (its DPI) and this screen's.</summary>
    private double PixelExact()
    {
        if (_vm?.Image is not { } img || img.Width <= 0) return 1;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return img.PixelWidth / img.Width / dpi;
    }

    private void FitPicture()
    {
        if (_vm?.Image is not { } img || img.Width <= 0 || ImageScroll.ActualWidth <= 0) return;
        // the whole picture in view, never larger than its own pixels (a small picture stays sharp)
        var fit = Math.Min((ImageScroll.ActualWidth - 32) / img.Width, (ImageScroll.ActualHeight - 32) / img.Height);
        SetPictureZoom(Math.Max(0.01, Math.Min(fit, PixelExact())));
    }

    private void SetPictureZoom(double z)
    {
        PictureZoom.ScaleX = PictureZoom.ScaleY = z;
        ZoomText.Content = $"{Math.Round(z / PixelExact() * 100)} %";
        Picture.Cursor = _fit ? Cursors.Hand : Cursors.SizeAll;
    }

    /// <summary>Zooms keeping the point under the mouse (or the middle) where it is.</summary>
    private void ZoomPicture(double z, Point? at)
    {
        if (_vm?.Image is null) return;
        z = Math.Clamp(z, 0.02, PixelExact() * 16);
        var old = PictureZoom.ScaleX;
        var anchor = at ?? new Point(ImageScroll.ViewportWidth / 2, ImageScroll.ViewportHeight / 2);
        var contentX = (ImageScroll.HorizontalOffset + anchor.X) / old;
        var contentY = (ImageScroll.VerticalOffset + anchor.Y) / old;
        _fit = false;
        SetPictureZoom(z);
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHorizontalOffset(contentX * z - anchor.X);
        ImageScroll.ScrollToVerticalOffset(contentY * z - anchor.Y);
    }

    private void OnPictureWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm?.IsImage != true) return;
        e.Handled = true;
        ZoomPicture(PictureZoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(ImageScroll));
    }

    private void OnPictureDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // fitting ↔ 100 % (around where it was clicked)
            if (_fit) ZoomPicture(PixelExact(), e.GetPosition(ImageScroll));
            else { _fit = true; FitPicture(); }
            e.Handled = true;
            return;
        }
        if (_fit) return;
        _dragFrom = e.GetPosition(ImageScroll);
        _dragOffset = new Point(ImageScroll.HorizontalOffset, ImageScroll.VerticalOffset);
        Picture.CaptureMouse();
    }

    private void OnPictureMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(ImageScroll);
        ImageScroll.ScrollToHorizontalOffset(_dragOffset.X - (p.X - from.X));
        ImageScroll.ScrollToVerticalOffset(_dragOffset.Y - (p.Y - from.Y));
    }

    // ------------------------------------------------------------------ a text

    private void ApplyWrap()
    {
        if (_vm is null) return;
        TextView.TextWrapping = _vm.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        WrapButton.ToolTip = _vm.Wrap ? "Long lines wrap: click to keep each line on one line" : "Wrap long lines";
        WrapButton.Foreground = _vm.Wrap ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE0));
    }

    private void SetTextZoom(double z)
    {
        _textZoom = Math.Clamp(z, 0.5, 4);
        TextView.FontSize = Math.Round(TextSize * _textZoom * 2) / 2;
        ZoomText.Content = $"{Math.Round(_textZoom * 100)} %";
    }

    private void OnTextWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetTextZoom(_textZoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    // ------------------------------------------------------------------ a PDF

    private void ClearPdf()
    {
        _pdfGeneration++;
        _slots.Clear();
        Pages.Children.Clear();
        _pageText.Clear();
        ClearFind();
    }

    private void BuildPdf()
    {
        ClearPdf();
        if (_vm?.Pdf is not { } doc) return;
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            var slot = new Slot { Index = (int)i, W = Math.Max(1, page.Size.Width), H = Math.Max(1, page.Size.Height) };
            slot.Image = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(slot.Image, BitmapScalingMode.HighQuality);
            slot.Marks = new Canvas { IsHitTestVisible = false };
            // the shadow is a sheet of its own behind the page: an effect on the page itself would redraw its picture blurred
            var sheet = new Border { Background = Brushes.White, Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 } };
            slot.Frame = new Grid { Margin = new Thickness(0, 0, 0, 14), Children = { sheet, slot.Image, slot.Marks } };
            _slots.Add(slot);
            Pages.Children.Add(slot.Frame);
        }
        _pdfFit = true;
        Dispatcher.BeginInvoke(() => { FitPdf(); PdfScroll.ScrollToTop(); }, DispatcherPriority.Loaded);
    }

    private void FitPdf()
    {
        if (_slots.Count == 0 || PdfScroll.ActualWidth <= 0) return;
        var widest = _slots.Max(s => s.W);
        SetPdfZoom(Math.Clamp((PdfScroll.ActualWidth - 48 - SystemParameters.VerticalScrollBarWidth) / widest, 0.1, 4));
    }

    private void ZoomPdf(double z)
    {
        if (_slots.Count == 0) return;
        _pdfFit = false;
        // the same place stays in view
        var ratio = PdfScroll.ExtentHeight > 0 ? (PdfScroll.VerticalOffset + PdfScroll.ViewportHeight / 2) / PdfScroll.ExtentHeight : 0;
        SetPdfZoom(Math.Clamp(z, 0.1, 6));
        PdfScroll.UpdateLayout();
        PdfScroll.ScrollToVerticalOffset(Math.Max(0, Math.Round(ratio * PdfScroll.ExtentHeight - PdfScroll.ViewportHeight / 2)));
    }

    /// <summary>Each page takes whole screen pixels (its picture is then drawn for exactly those: nothing is stretched).</summary>
    private void SetPdfZoom(double z)
    {
        _pdfZoom = z;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var s in _slots)
        {
            s.PxW = Math.Max(1, (int)Math.Round(s.W * z * dpi));
            s.PxH = Math.Max(1, (int)Math.Round(s.H * z * dpi));
            s.Frame.Width = s.PxW / dpi; s.Frame.Height = s.PxH / dpi;
            DrawMarks(s);
        }
        ZoomText.Content = $"{Math.Round(z * 100)} %";
        RenderSoon();
    }

    private void OnPdfWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm?.IsPdf != true || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        ZoomPdf(_pdfZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
    }

    /// <summary>The page in the middle of the view (0-based).</summary>
    private int MiddlePage()
    {
        var middle = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight / 2;
        double y = 16;
        foreach (var s in _slots)
        {
            y += s.Frame.Height + 14;
            if (middle < y) return s.Index;
        }
        return Math.Max(0, _slots.Count - 1);
    }

    private void UpdatePageText()
    {
        if (_vm is null || _slots.Count == 0) return;
        _vm.PageText = $"Page {MiddlePage() + 1} of {_slots.Count}";
    }

    private void RenderSoon()
    {
        if (_renderDue) return;
        _renderDue = true;
        Dispatcher.BeginInvoke(async () => { _renderDue = false; await RenderVisibleAsync(); }, DispatcherPriority.Background);
    }

    /// <summary>The pages in view and next to it, drawn for the pixels they take (sharp at any zoom); those far away let go.</summary>
    private async Task RenderVisibleAsync()
    {
        if (_rendering || _vm?.Pdf is not { } doc) { if (_rendering) _renderAgain = true; return; }
        _rendering = true;
        var generation = _pdfGeneration;
        try
        {
            double top = PdfScroll.VerticalOffset - PdfScroll.ViewportHeight, bottom = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight * 2;
            double y = 16;
            var wanted = new List<Slot>();
            foreach (var s in _slots)
            {
                var h = s.Frame.Height + 14;
                var near = y + h >= top && y <= bottom;
                if (near) { if (s.Rendered != s.PxW) wanted.Add(s); }
                else if (s.Rendered != 0 && (y + h < top - PdfScroll.ViewportHeight * 3 || y > bottom + PdfScroll.ViewportHeight * 3))
                {
                    s.Image.Source = null; s.Rendered = 0; // (far away: its memory back)
                }
                y += h;
            }
            // the one in the middle first
            var middle = MiddlePage();
            foreach (var s in wanted.OrderBy(w => Math.Abs(w.Index - middle)))
            {
                if (s.Rendered == s.PxW) continue;
                int pw = s.PxW, ph = s.PxH;
                // (beyond 6000 pixels wide a page is drawn at 6000 and enlarged: more would take too much memory)
                var cap = Math.Min(1.0, 6000.0 / pw);
                using var page = doc.GetPage((uint)s.Index);
                var png = await PdfFiles.RenderAsync(page, (uint)Math.Max(1, pw * cap), (uint)Math.Max(1, ph * cap));
                if (generation != _pdfGeneration) return;
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
                bmp.Freeze();
                s.Image.Source = bmp; s.Rendered = pw;
            }
        }
        catch (Exception ex) { AppLog.Info("Viewer", "pdf page: " + ex.Message); }
        finally
        {
            _rendering = false;
            if (_renderAgain) { _renderAgain = false; RenderSoon(); }
        }
    }

    // ------------------------------------------------------------------ finding words

    private void Find_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void FindNext_Click(object sender, RoutedEventArgs e) => Step(1);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void FindClose_Click(object sender, RoutedEventArgs e) => _vm?.CloseSearchCommand.Execute(null);

    private void OpenFind()
    {
        if (_vm is null) return;
        if (_vm.IsSearchOpen) { SearchBox.Focus(); SearchBox.SelectAll(); }
        else _vm.OpenSearchCommand.Execute(null);
        // a PDF's pages start being read at once, from the one in view (the words are ready sooner)
        if (_vm.IsPdf) _ = ReadAheadAsync(_pdfGeneration);
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: _findDelay.Stop(); if (_matches.Count == 0 && _findCts is null) Find(); else Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); break;
            case Key.Escape: _vm?.CloseSearchCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ClearFind()
    {
        _findCts?.Cancel(); _findCts = null;
        _matches = new(); _current = null;
        foreach (var s in _slots) DrawMarks(s);
        TextMarks.Children.Clear();
        if (_vm is not null) _vm.SearchStatus = "";
    }

    private void Find()
    {
        if (_vm is null) return;
        var query = _vm.SearchQuery.Trim();
        ClearFind();
        if (query.Length == 0) return;
        if (_vm.IsText) FindInText(query);
        else if (_vm.IsPdf) { _findCts = new CancellationTokenSource(); _ = FindInPdfAsync(query, _pdfGeneration, _findCts.Token); }
    }

    private void Step(int by)
    {
        if (_matches.Count == 0) return;
        var i = _current is null ? (by > 0 ? 0 : _matches.Count - 1) : (_matches.IndexOf(_current) + by + _matches.Count) % _matches.Count;
        Show(_matches[i]);
    }

    private void Show(Match m)
    {
        var old = _current;
        _current = m;
        if (_vm is not null) _vm.SearchStatus = $"{_matches.IndexOf(m) + 1} of {_matches.Count}{(_findCts is null ? "" : "+")}";
        if (_vm?.IsText == true)
        {
            TextView.Select(m.Start, m.Length);
            var line = TextView.GetLineIndexFromCharacterIndex(m.Start);
            if (line >= 0) TextView.ScrollToLine(Math.Max(0, line - 3));
            TextView.UpdateLayout();
            var r = TextView.GetRectFromCharacterIndex(m.Start);
            if (!r.IsEmpty && (r.X < 0 || r.X > TextView.ActualWidth - 60)) TextView.ScrollToHorizontalOffset(Math.Max(0, TextView.HorizontalOffset + r.X - 40));
            DrawTextMarksSoon();
            return;
        }
        if (old is not null && old.Page != m.Page) DrawMarks(_slots[old.Page]);
        var slot = _slots[m.Page];
        DrawMarks(slot);
        // the match in the middle of the view
        PdfScroll.UpdateLayout();
        var box = m.Rects.Aggregate(Rect.Union);
        var at = slot.Frame.TranslatePoint(new Point((box.X + box.Width / 2) * slot.Frame.Width, (box.Y + box.Height / 2) * slot.Frame.Height), PdfScroll);
        PdfScroll.ScrollToVerticalOffset(Math.Max(0, Math.Round(PdfScroll.VerticalOffset + at.Y - PdfScroll.ViewportHeight / 2)));
        if (at.X < 40 || at.X > PdfScroll.ViewportWidth - 40)
            PdfScroll.ScrollToHorizontalOffset(Math.Max(0, Math.Round(PdfScroll.HorizontalOffset + at.X - PdfScroll.ViewportWidth / 2)));
    }

    private void FindInText(string query)
    {
        var text = _vm?.TextContent ?? "";
        for (var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0 && _matches.Count < 50_000;
             at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            _matches.Add(new Match { Start = at, Length = query.Length });
        if (_matches.Count == 0) { _vm!.SearchStatus = "No matches"; return; }
        // the first one from where the text is shown
        var from = TextView.GetCharacterIndexFromLineIndex(Math.Max(0, TextView.GetFirstVisibleLineIndex()));
        Show(_matches.FirstOrDefault(m => m.Start >= from) ?? _matches[0]);
    }

    /// <summary>A page's words (read once; the same reading serves the read-ahead and every search).</summary>
    private Task<PdfText.Page> PageText(int index, Windows.Data.Pdf.PdfDocument doc)
    {
        if (_pageText.TryGetValue(index, out var t) && !t.IsFaulted) return t;
        t = ReadPage(doc, index);
        _pageText[index] = t;
        return t;

        static async Task<PdfText.Page> ReadPage(Windows.Data.Pdf.PdfDocument d, int i)
        {
            using var page = d.GetPage((uint)i);
            return await PdfText.ReadAsync(page);
        }
    }

    /// <summary>The pages in the order they are read: from the one in view to the end, then from the start.</summary>
    private List<int> ReadingOrder()
    {
        var from = MiddlePage();
        return Enumerable.Range(from, _slots.Count - from).Concat(Enumerable.Range(0, from)).ToList();
    }

    private async Task ReadAheadAsync(int generation)
    {
        if (_vm?.Pdf is not { } doc || PdfText.Engine is null) return;
        foreach (var i in ReadingOrder())
        {
            if (generation != _pdfGeneration || _vm?.IsSearchOpen != true) return;
            try { await PageText(i, doc); } catch { }
        }
    }

    private async Task FindInPdfAsync(string query, int generation, CancellationToken ct)
    {
        if (_vm?.Pdf is not { } doc) return;
        if (PdfText.Engine is null)
        {
            _vm.SearchStatus = "Can't read";
            SearchBox.ToolTip = "Finding words in a PDF uses Windows' text recognition, which needs a language with it installed (Settings › Time & language › Language & region).";
            _findCts = null;
            return;
        }
        var order = ReadingOrder();
        var read = 0;
        try
        {
            foreach (var i in order)
            {
                if (!_pageText.TryGetValue(i, out var ready) || !ready.IsCompleted) _vm.SearchStatus = $"Reading {read + 1}/{order.Count}…";
                PdfText.Page text;
                try { text = await PageText(i, doc); }
                catch (Exception ex) { AppLog.Info("Viewer", $"pdf text p{i + 1}: {ex.Message}"); read++; continue; }
                if (ct.IsCancellationRequested || generation != _pdfGeneration) return;
                read++;
                var found = PdfText.Find(text, query);
                if (found.Count == 0) continue;
                // in page order, whatever order they were read in
                var at = _matches.FindIndex(m => m.Page > i);
                var add = found.Select(r => new Match { Page = i, Rects = r }).ToList();
                if (at < 0) _matches.AddRange(add); else _matches.InsertRange(at, add);
                DrawMarks(_slots[i]);
                if (_current is null) Show(add[0]);
                else _vm.SearchStatus = $"{_matches.IndexOf(_current) + 1} of {_matches.Count}+";
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested && generation == _pdfGeneration)
            {
                _findCts = null;
                _vm.SearchStatus = _matches.Count == 0
                    ? (_pageText.Values.All(t => t.IsCompletedSuccessfully && t.Result.Words.Count == 0) ? "No text found" : "No matches")
                    : $"{_matches.IndexOf(_current!) + 1} of {_matches.Count}";
            }
        }
    }

    private bool _textMarksDue;

    private void DrawTextMarksSoon()
    {
        if (_textMarksDue) return;
        _textMarksDue = true;
        Dispatcher.BeginInvoke(() => { _textMarksDue = false; DrawTextMarks(); }, DispatcherPriority.Render);
    }

    /// <summary>The words found in the part of the text in view, marked over it (the current one stronger).</summary>
    private void DrawTextMarks()
    {
        TextMarks.Children.Clear();
        if (_vm?.IsText != true || _matches.Count == 0 || TextView.Text.Length == 0) return;
        int firstLine = TextView.GetFirstVisibleLineIndex(), lastLine = TextView.GetLastVisibleLineIndex();
        if (firstLine < 0 || lastLine < 0) return;
        int from = TextView.GetCharacterIndexFromLineIndex(firstLine);
        int to = TextView.GetCharacterIndexFromLineIndex(lastLine) + TextView.GetLineLength(lastLine);
        // (the matches are in text order: start at the first in view)
        int lo = 0, hi = _matches.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (_matches[mid].Start + _matches[mid].Length <= from) lo = mid + 1; else hi = mid; }
        for (var i = lo; i < _matches.Count && _matches[i].Start <= to && TextMarks.Children.Count < 400; i++)
        {
            var m = _matches[i];
            var current = ReferenceEquals(m, _current);
            // one mark per line the match is on (a wrapped line can split it)
            Rect? run = null;
            for (var c = m.Start; c < m.Start + m.Length && c < TextView.Text.Length; c++)
            {
                var a = TextView.GetRectFromCharacterIndex(c);
                var b = TextView.GetRectFromCharacterIndex(c, true);
                if (a.IsEmpty || b.IsEmpty) continue;
                var r = new Rect(a.X, a.Y, Math.Max(0, b.X - a.X), a.Height);
                if (run is { } prev && Math.Abs(prev.Y - r.Y) < 1) run = Rect.Union(prev, r);
                else { if (run is { } done) AddTextMark(done, current); run = r; }
            }
            if (run is { } last) AddTextMark(last, current);
        }
    }

    private void AddTextMark(Rect r, bool current)
    {
        var mark = new Rectangle
        {
            Width = r.Width + 2, Height = r.Height, RadiusX = 2, RadiusY = 2,
            Fill = current ? CurrentBrush : MarkBrush, Stroke = current ? CurrentEdge : null, StrokeThickness = current ? 1.2 : 0,
        };
        Canvas.SetLeft(mark, r.X - 1); Canvas.SetTop(mark, r.Y);
        TextMarks.Children.Add(mark);
    }

    private static readonly Brush MarkBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xD5, 0x4F));
    private static readonly Brush CurrentBrush = Frozen(Color.FromArgb(0x88, 0xFF, 0x8C, 0x1A));
    private static readonly Brush CurrentEdge = Frozen(Color.FromRgb(0xE8, 0x6A, 0x00));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>The page's matches marked over it (the current one stronger).</summary>
    private void DrawMarks(Slot s)
    {
        s.Marks.Children.Clear();
        double w = s.Frame.Width, h = s.Frame.Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;
        foreach (var m in _matches)
        {
            if (m.Page != s.Index) continue;
            var current = ReferenceEquals(m, _current);
            foreach (var r in m.Rects)
            {
                var pad = Math.Max(1, r.Height * h * 0.12);
                var mark = new Rectangle
                {
                    Width = r.Width * w + pad * 2, Height = r.Height * h + pad * 2, RadiusX = 2, RadiusY = 2,
                    Fill = current ? CurrentBrush : MarkBrush, Stroke = current ? CurrentEdge : null, StrokeThickness = current ? 1.2 : 0,
                };
                Canvas.SetLeft(mark, r.X * w - pad); Canvas.SetTop(mark, r.Y * h - pad);
                s.Marks.Children.Add(mark);
            }
        }
    }
}
