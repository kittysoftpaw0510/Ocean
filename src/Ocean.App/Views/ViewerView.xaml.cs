using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ocean.App.ViewModels;
using Ocean.Core.Media;

namespace Ocean.App.Views;

/// <summary>
/// The viewer's view: a picture fitted to the window (the wheel zooms around the mouse, a drag moves it, a double
/// click switches between fitting and 100 %, ←/→ the next and previous picture), or a PDF's pages one under the
/// other, each drawn by Windows' PDF engine at the size it is shown and only when it comes near the view
/// (Ctrl+wheel or +/− zoom, Page Up/Down, Home/End).
/// </summary>
public partial class ViewerView : UserControl
{
    private ViewerViewModel? _vm;
    // a picture
    private bool _fit = true;
    private Point? _dragFrom;
    private Point _dragOffset;
    // a PDF
    private sealed class Slot { public int Index; public double W, H; public Border Frame = null!; public Image Image = null!; public uint Rendered; }
    private readonly List<Slot> _slots = new();
    private double _pdfZoom = 1;
    private bool _pdfFit = true;
    private bool _renderDue, _rendering;
    private int _pdfGeneration;

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
    }

    private void Attach(ViewerViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is not null) vm.PropertyChanged += OnVmChanged;
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
                KindGlyph.Text = _vm.IsPdf ? "" : "";
                break;
            case nameof(ViewerViewModel.Image):
                _fit = true;
                FitPicture();
                break;
            case nameof(ViewerViewModel.Pdf):
                BuildPdf();
                break;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
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

    private void Zoom(double by) { if (_vm?.IsPdf == true) ZoomPdf(_pdfZoom * by); else ZoomPicture(PictureZoom.ScaleX * by, null); }
    private void FitAll() { if (_vm?.IsPdf == true) { _pdfFit = true; FitPdf(); } else { _fit = true; FitPicture(); } }

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

    // ------------------------------------------------------------------ a PDF

    private void ClearPdf()
    {
        _pdfGeneration++;
        _slots.Clear();
        Pages.Children.Clear();
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
            slot.Frame = new Border
            {
                Background = Brushes.White, Margin = new Thickness(0, 0, 0, 14), Child = slot.Image,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 },
            };
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
        PdfScroll.ScrollToVerticalOffset(Math.Max(0, ratio * PdfScroll.ExtentHeight - PdfScroll.ViewportHeight / 2));
    }

    private void SetPdfZoom(double z)
    {
        _pdfZoom = z;
        foreach (var s in _slots) { s.Frame.Width = s.W * z; s.Frame.Height = s.H * z; }
        ZoomText.Content = $"{Math.Round(z * 100)} %";
        RenderSoon();
    }

    private void OnPdfWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm?.IsPdf != true || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        ZoomPdf(_pdfZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
    }

    private void UpdatePageText()
    {
        if (_vm is null || _slots.Count == 0) return;
        var middle = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight / 2;
        double y = 16; var page = 1;
        foreach (var s in _slots)
        {
            var h = s.H * _pdfZoom + 14;
            if (middle < y + h) { page = s.Index + 1; break; }
            y += h; page = s.Index + 1;
        }
        _vm.PageText = $"Page {page} of {_slots.Count}";
    }

    private void RenderSoon()
    {
        if (_renderDue) return;
        _renderDue = true;
        Dispatcher.BeginInvoke(async () => { _renderDue = false; await RenderVisibleAsync(); }, DispatcherPriority.Background);
    }

    /// <summary>The pages in view and next to it, drawn at the size shown (sharp on this screen); those far away let go.</summary>
    private async Task RenderVisibleAsync()
    {
        if (_rendering || _vm?.Pdf is not { } doc) { if (_rendering) _renderAgain = true; return; }
        _rendering = true;
        var generation = _pdfGeneration;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double top = PdfScroll.VerticalOffset - PdfScroll.ViewportHeight, bottom = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight * 2;
            double y = 16;
            var wanted = new List<(Slot S, uint Px)>();
            foreach (var s in _slots)
            {
                var h = s.H * _pdfZoom + 14;
                var near = y + h >= top && y <= bottom;
                if (near)
                {
                    var px = (uint)Math.Clamp(s.W * _pdfZoom * dpi, 64, 6000);
                    if (s.Rendered == 0 || Math.Abs((double)s.Rendered - px) / px > 0.1) wanted.Add((s, px));
                }
                else if (s.Rendered != 0 && (y + h < top - PdfScroll.ViewportHeight * 3 || y > bottom + PdfScroll.ViewportHeight * 3))
                {
                    s.Image.Source = null; s.Rendered = 0; // (far away: its memory back)
                }
                y += h;
            }
            // the one in the middle first
            var middle = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight / 2;
            foreach (var (s, px) in wanted.OrderBy(w => Math.Abs(Offset(w.S) - middle)))
            {
                using var page = doc.GetPage((uint)s.Index);
                var png = await PdfFiles.RenderAsync(page, px);
                if (generation != _pdfGeneration) return;
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
                bmp.Freeze();
                s.Image.Source = bmp; s.Rendered = px;
            }
        }
        catch (Exception ex) { Services.AppLog.Info("Viewer", "pdf page: " + ex.Message); }
        finally
        {
            _rendering = false;
            if (_renderAgain) { _renderAgain = false; RenderSoon(); }
        }
    }
    private bool _renderAgain;

    private double Offset(Slot s)
    {
        double y = 16;
        foreach (var x in _slots) { if (ReferenceEquals(x, s)) return y + s.H * _pdfZoom / 2; y += x.H * _pdfZoom + 14; }
        return y;
    }
}
