using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Ocean.App.Services;

/// <summary>What the user picked to share. All sizes are in real screen pixels.</summary>
public sealed class ScreenPick
{
    public enum Kinds { Monitor, Window, Region }
    public Kinds Kind { get; init; }
    public IntPtr Monitor { get; init; }
    public IntPtr Window { get; init; }
    /// <summary>Region: what is cut away from the monitor's left, top, right and bottom.</summary>
    public int CropLeft { get; init; }
    public int CropTop { get; init; }
    public int CropRight { get; init; }
    public int CropBottom { get; init; }
    /// <summary>The size of what is captured (the monitor, the window, the region).</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    /// <summary>For people: "Monitor 1", a window's title, "Part of monitor 2".</summary>
    public string Title { get; init; } = "";
    /// <summary>Smooth: 30 pictures a second for video and motion; otherwise 15, which is plenty for work on a desktop.</summary>
    public bool Smooth { get; set; }

    public int Fps => Smooth ? 30 : 15;

    /// <summary>The size it is sent at: never larger than 1920 × 1080, never enlarged, even numbers (the encoder needs them).</summary>
    public (int Width, int Height) SendSize()
    {
        var scale = Math.Min(1.0, Math.Min(1920.0 / Math.Max(1, SourceWidth), 1080.0 / Math.Max(1, SourceHeight)));
        var w = Math.Max(64, (int)Math.Round(SourceWidth * scale) & ~1);
        var h = Math.Max(64, (int)Math.Round(SourceHeight * scale) & ~1);
        return (w, h);
    }
}

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public int Number { get; init; }
    public Int32Rect Bounds { get; init; }
    public bool Primary { get; init; }
    public string Name => $"Monitor {Number}";
    public string Detail => $"{Bounds.Width} × {Bounds.Height}" + (Primary ? "  ·  main" : "");
    public BitmapSource? Preview { get; set; }
}

public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string App { get; init; } = "";
    public Int32Rect Bounds { get; init; }
    public BitmapSource? Preview { get; set; }
    public string Detail => string.IsNullOrEmpty(App) ? $"{Bounds.Width} × {Bounds.Height}" : $"{App}  ·  {Bounds.Width} × {Bounds.Height}";
}

/// <summary>The monitors and windows that can be shared, with small previews for the picker.</summary>
public static class ScreenSources
{
    public static List<MonitorInfo> Monitors()
    {
        var found = new List<(IntPtr Handle, RECT Rect, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi)) found.Add((h, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // the main one first, then left to right, top to bottom: the numbers people expect
        var ordered = found.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).ToList();
        return ordered.Select((m, i) => new MonitorInfo
        {
            Handle = m.Handle, Number = i + 1, Primary = m.Primary,
            Bounds = new Int32Rect(m.Rect.Left, m.Rect.Top, m.Rect.Right - m.Rect.Left, m.Rect.Bottom - m.Rect.Top),
        }).ToList();
    }

    /// <summary>Windows a person would call a window: visible, with a title, not minimized, not hidden on another desktop, not Ocean's own helpers.</summary>
    public static List<WindowInfo> Windows(IEnumerable<IntPtr>? skip = null)
    {
        var skipSet = new HashSet<IntPtr>(skip ?? Array.Empty<IntPtr>());
        var list = new List<WindowInfo>();
        var shell = GetShellWindow();
        EnumWindows((h, _) =>
        {
            try
            {
                if (h == shell || skipSet.Contains(h) || !IsWindowVisible(h) || IsIconic(h)) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                var ex = GetWindowLongPtr(h, -20).ToInt64();
                if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var len = GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString().Trim();
                if (title.Length == 0) return true;
                if (!Frame(h, out var r) || r.Right - r.Left < 80 || r.Bottom - r.Top < 60) return true;
                var app = "";
                GetWindowThreadProcessId(h, out var pid);
                try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; } catch { }
                list.Add(new WindowInfo { Handle = h, Title = title, App = app, Bounds = new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window's visible frame (without the invisible resize border and the shadow).</summary>
    private static bool Frame(IntPtr h, out RECT r)
        => DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf<RECT>()) == 0 || GetWindowRect(h, out r);

    public static bool Exists(IntPtr window) => IsWindow(window);

    public static Int32Rect? WindowBounds(IntPtr h) => Frame(h, out var r) ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    /// <summary>A small picture of a part of the desktop (what is on screen there right now).</summary>
    public static BitmapSource? Snapshot(Int32Rect area, int maxWidth = 320)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        try
        {
            var scale = Math.Min(1.0, (double)maxWidth / area.Width);
            int w = Math.Max(1, (int)(area.Width * scale)), h = Math.Max(1, (int)(area.Height * scale));
            using var full = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
                g.CopyFromScreen(area.X, area.Y, 0, 0, new System.Drawing.Size(area.Width, area.Height));
            return Shrink(full, w, h);
        }
        catch { return null; }
    }

    /// <summary>A small picture of a window, also when other windows cover it.</summary>
    public static BitmapSource? Snapshot(WindowInfo window, int maxWidth = 320)
    {
        try
        {
            if (!GetWindowRect(window.Handle, out var r)) return null;
            int fw = r.Right - r.Left, fh = r.Bottom - r.Top;
            if (fw <= 0 || fh <= 0) return null;
            using var full = new System.Drawing.Bitmap(fw, fh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(window.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok) return Snapshot(window.Bounds, maxWidth);
            }
            // cut to the visible frame
            var b = window.Bounds;
            var crop = new System.Drawing.Rectangle(Math.Max(0, b.X - r.Left), Math.Max(0, b.Y - r.Top), Math.Min(b.Width, fw), Math.Min(b.Height, fh));
            crop.Intersect(new System.Drawing.Rectangle(0, 0, fw, fh));
            if (crop.Width <= 0 || crop.Height <= 0) return null;
            using var cut = full.Clone(crop, full.PixelFormat);
            var scale = Math.Min(1.0, (double)maxWidth / cut.Width);
            return Shrink(cut, Math.Max(1, (int)(cut.Width * scale)), Math.Max(1, (int)(cut.Height * scale)));
        }
        catch { return null; }
    }

    private static BitmapSource Shrink(System.Drawing.Bitmap source, int w, int h)
    {
        using var small = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, 0, 0, w, h);
        }
        var hbmp = small.GetHbitmap();
        try
        {
            var img = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally { DeleteObject(hbmp); }
    }

    /// <summary>
    /// Keeps a window of ours out of every capture (the "you are sharing" bar, the recording boundary): viewers and
    /// recordings never show it. False where Windows can't (before Windows 10 2004): the window is then captured.
    /// </summary>
    public static bool HideFromCapture(Window window)
    {
        try
        {
            var h = new WindowInteropHelper(window).EnsureHandle();
            if (SetWindowDisplayAffinity(h, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) return true;
            SetWindowDisplayAffinity(h, 0);
        }
        catch { }
        return false;
    }

    /// <summary>A window that can be seen right now: there, shown, not minimized, on this desktop.</summary>
    public static bool IsOnScreen(IntPtr h)
        => IsWindow(h) && IsWindowVisible(h) && !IsIconic(h)
           && !(DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0);

    public static Int32Rect VirtualScreen()
        => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    /// <summary>Places a window by real pixels (WPF's own units differ per monitor).</summary>
    public static void MoveTo(Window window, Int32Rect px)
    {
        var h = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(h, IntPtr.Zero, px.X, px.Y, px.Width, px.Height, 0x0014 /* NOZORDER | NOACTIVATE */);
    }

    // ---- native ----

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
