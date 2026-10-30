<#
.SYNOPSIS
  Renders the Ocean artwork and writes it to assets\:
    ocean.ico            multi-resolution app icon (16..256 px; DIB frames + PNG for 128/256)
    ocean-256.png, ocean-512.png
    wizard-*.bmp         installer wizard images (large side banner + small header logo, several DPI sizes)

  The design: a rounded "app tile" with an ocean gradient, a white play symbol (video), and a wave that
  doubles as a network line with node dots on it (computers on a LAN). Small sizes drop details so
  the shape stays crisp at 16 px.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "assets"
New-Item -ItemType Directory -Force $assets | Out-Null

Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework, System.Xaml
$refs = @(
    [System.Windows.Media.Visual].Assembly.Location,      # PresentationCore
    [System.Windows.DependencyObject].Assembly.Location,   # WindowsBase
    [System.Xaml.XamlReader].Assembly.Location,           # System.Xaml
    "System.dll", "System.Core.dll"
)

$src = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class OceanArt
{
    static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

    static double WaveY(double t, double y0, double amp) { return y0 + amp * Math.Sin(t * Math.PI * 2.0 * 1.25 + 0.6); }

    // Draws the tile into the given square.
    public static void DrawTile(DrawingContext dc, Rect rect, int px)
    {
        double w = rect.Width;
        double r = w * 0.235;

        var bg = new LinearGradientBrush();
        bg.StartPoint = new Point(0, 0); bg.EndPoint = new Point(1, 1);
        bg.GradientStops.Add(new GradientStop(C("#2DD4BF"), 0.0));
        bg.GradientStops.Add(new GradientStop(C("#1E7FE0"), 0.45));
        bg.GradientStops.Add(new GradientStop(C("#1E3A8A"), 0.80));
        bg.GradientStops.Add(new GradientStop(C("#0F1D4A"), 1.0));
        dc.DrawRoundedRectangle(bg, null, rect, r, r);

        dc.PushClip(new RectangleGeometry(rect, r, r));

        // soft light from the top-left
        var gloss = new RadialGradientBrush(Color.FromArgb(px >= 32 ? (byte)90 : (byte)50, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        gloss.Center = new Point(0.25, 0.1); gloss.GradientOrigin = new Point(0.25, 0.1);
        gloss.RadiusX = 0.85; gloss.RadiusY = 0.65;
        dc.DrawRectangle(gloss, null, rect);

        // deep water at the bottom
        if (px >= 24)
        {
            var deep = new LinearGradientBrush(Color.FromArgb(0, 8, 20, 60), Color.FromArgb(120, 8, 20, 60), 90);
            dc.DrawRectangle(deep, null, new Rect(rect.Left, rect.Top + w * 0.62, w, w * 0.38));
        }

        // waves (second, fainter wave only when there is room)
        double x0 = rect.Left - w * 0.05, x1 = rect.Right + w * 0.05;
        double y1 = rect.Top + w * 0.80, amp1 = w * 0.045;
        double thick1 = Math.Max(1.25, w * (px <= 24 ? 0.085 : 0.05));
        DrawWave(dc, x0, x1, y1, amp1, new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), thick1));
        if (px >= 32)
        {
            DrawWave(dc, x0, x1, rect.Top + w * 0.915, w * 0.035,
                new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), Math.Max(1.0, w * 0.035)));
        }
        dc.Pop();

        // nodes on the wave = computers on the network (too noisy below 48 px)
        if (px >= 48)
        {
            double dot = w * 0.052;
            var fill = new SolidColorBrush(C("#0F1D4A"));
            var ring = new Pen(Brushes.White, Math.Max(1.0, w * 0.03));
            foreach (double t in new double[] { 0.22, 0.5, 0.78 })
            {
                double x = rect.Left + w * t;
                double y = WaveY((x - x0) / (x1 - x0), y1, amp1);
                dc.DrawEllipse(fill, ring, new Point(x, y), dot, dot);
            }
        }

        // play symbol with rounded corners and a soft shadow
        double cx = rect.Left + w * 0.535, cy = rect.Top + w * (px <= 24 ? 0.43 : 0.415);
        double h = w * (px <= 24 ? 0.46 : 0.40);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(cx - h * 0.40, cy - h * 0.5), true, true);
            g.LineTo(new Point(cx + h * 0.50, cy), true, true);
            g.LineTo(new Point(cx - h * 0.40, cy + h * 0.5), true, true);
        }
        tri.Freeze();
        var roundPen = new Pen(Brushes.White, w * 0.075); roundPen.LineJoin = PenLineJoin.Round;
        if (px >= 32)
        {
            dc.PushTransform(new TranslateTransform(0, w * 0.025));
            var sh = new SolidColorBrush(Color.FromArgb(55, 0, 10, 40));
            var shPen = new Pen(sh, w * 0.075); shPen.LineJoin = PenLineJoin.Round;
            dc.DrawGeometry(sh, shPen, tri);
            dc.Pop();
        }
        dc.DrawGeometry(Brushes.White, roundPen, tri);
    }

    static void DrawWave(DrawingContext dc, double x0, double x1, double y0, double amp, Pen pen)
    {
        pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round; pen.LineJoin = PenLineJoin.Round;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            const int n = 64;
            g.BeginFigure(new Point(x0, WaveY(0, y0, amp)), false, false);
            var pts = new List<Point>();
            for (int i = 1; i <= n; i++)
            {
                double t = i / (double)n;
                pts.Add(new Point(x0 + (x1 - x0) * t, WaveY(t, y0, amp)));
            }
            g.PolyLineTo(pts, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    public static BitmapSource RenderIcon(int px)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double pad = px <= 24 ? 0 : px * 0.03;
            DrawTile(dc, new Rect(pad, pad, px - 2 * pad, px - 2 * pad), px);
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
    }

    // Large installer banner (welcome / finish pages).
    public static BitmapSource RenderBanner(int width, int height)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(C("#0F1D4A"), C("#0A1120"), 90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            var glow = new RadialGradientBrush(Color.FromArgb(90, 45, 212, 191), Color.FromArgb(0, 45, 212, 191));
            glow.Center = new Point(0.5, 0.33); glow.GradientOrigin = new Point(0.5, 0.33); glow.RadiusX = 0.9; glow.RadiusY = 0.45;
            dc.DrawRectangle(glow, null, new Rect(0, 0, width, height));
            double s = width * 0.56;
            DrawTile(dc, new Rect((width - s) / 2, height * 0.20, s, s), 256);
            // gentle waves at the bottom of the banner
            for (int i = 0; i < 3; i++)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(70 - i * 18), 255, 255, 255)), Math.Max(1, width * 0.012));
                DrawWave(dc, -width * 0.1, width * 1.1, height * (0.80 + i * 0.06), height * 0.015, pen);
            }
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText("Ocean", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, width * 0.17, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((width - ft.Width) / 2, height * 0.20 + s + height * 0.04));
            var tf2 = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var ft2 = new FormattedText("LAN video sharing", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf2, width * 0.075,
                new SolidColorBrush(C("#8CA0C0")), 1.0);
            dc.DrawText(ft2, new Point((width - ft2.Width) / 2, height * 0.20 + s + height * 0.04 + ft.Height));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    // Small header logo on a white background (Inno's inner pages are light).
    public static BitmapSource RenderSmall(int px)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, px, px));
            DrawTile(dc, new Rect(px * 0.06, px * 0.06, px * 0.88, px * 0.88), px);
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
    }

    static void SaveBmp24(BitmapSource src, string path)
    {
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0)));
        using (var fs = File.Create(path)) enc.Save(fs);
    }

    // Classic 32-bpp DIB icon frame (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask).
    static byte[] Dib(BitmapSource bgra, int px)
    {
        int stride = px * 4;
        var pixels = new byte[stride * px];
        bgra.CopyPixels(pixels, stride, 0);
        int maskStride = ((px + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(px); bw.Write(px * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(stride * px + maskStride * px); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = px - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
            bw.Write(new byte[maskStride * px]);
            return ms.ToArray();
        }
    }

    public static void WriteAll(string dir)
    {
        Exception error = null;
        var t = new Thread(() =>
        {
            try
            {
                int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
                var frames = new List<KeyValuePair<int, byte[]>>();
                foreach (int s in sizes)
                {
                    var bmp = RenderIcon(s);
                    frames.Add(new KeyValuePair<int, byte[]>(s, s >= 128 ? Png(bmp) : Dib(bmp, s)));
                }
                using (var fs = File.Create(Path.Combine(dir, "ocean.ico")))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((short)0); bw.Write((short)1); bw.Write((short)frames.Count);
                    int offset = 6 + 16 * frames.Count;
                    foreach (var f in frames)
                    {
                        bw.Write((byte)(f.Key >= 256 ? 0 : f.Key)); bw.Write((byte)(f.Key >= 256 ? 0 : f.Key));
                        bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                        bw.Write(f.Value.Length); bw.Write(offset);
                        offset += f.Value.Length;
                    }
                    foreach (var f in frames) bw.Write(f.Value);
                }
                File.WriteAllBytes(Path.Combine(dir, "ocean-256.png"), Png(RenderIcon(256)));
                File.WriteAllBytes(Path.Combine(dir, "ocean-512.png"), Png(RenderIcon(512)));
                foreach (int scale in new[] { 100, 125, 150, 200 })
                {
                    SaveBmp24(RenderBanner(164 * scale / 100, 314 * scale / 100), Path.Combine(dir, "wizard-large-" + scale + ".bmp"));
                    SaveBmp24(RenderSmall(55 * scale / 100), Path.Combine(dir, "wizard-small-" + scale + ".bmp"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies $refs
[OceanArt]::WriteAll($assets)
Get-ChildItem $assets | Select-Object Name, Length | Format-Table -AutoSize
