using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Ocean.App.Services;

/// <summary>What the user picked to share. All sizes are in real screen pixels.</summary>
/// <summary>Sharing choices remembered for next time (per Windows user): %LocalAppData%\Ocean\screen.json.</summary>
public sealed class ScreenShareSettings
{
    /// <summary>Share the PC's sound with the picture.</summary>
    public bool Sound { get; set; } = true;
    /// <summary>The board panel's background: how solid (0.25 to 1).</summary>
    public double BoardOpacity { get; set; } = 0.82;
    /// <summary>The board's width in the viewer window (device-independent pixels).</summary>
    public double BoardWidth { get; set; } = 380;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocean", "screen.json");

    public static ScreenShareSettings Load()
    {
        try { if (File.Exists(FilePath) && System.Text.Json.JsonSerializer.Deserialize<ScreenShareSettings>(File.ReadAllText(FilePath)) is { } s) return s; }
        catch (Exception ex) { AppLog.Warn("Screen", "screen.json can't be read", ex); }
        return new ScreenShareSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Warn("Screen", "screen.json can't be written", ex); }
    }
}

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
    /// <summary>Sharing: what the PC plays goes with the picture (can be switched while sharing).</summary>
    public bool Sound { get; set; }

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
        try { return HideFromCapture(new WindowInteropHelper(window).EnsureHandle()); }
        catch { return false; }
    }

    /// <summary>
    /// Tooltips and right-click menus are windows of their own: one that opens for a window kept out of the capture
    /// (the board, the sharing and recording bars, the shapes' choice) is kept out too, else viewers see a tooltip
    /// floating where the sharing PC's mouse is. It is done when it opens, before it is first drawn (these windows
    /// are see-through and show nothing until then), so not even one picture of it is captured. Once, at start.
    /// </summary>
    public static void GuardPopups()
    {
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent,
            new RoutedEventHandler((s, _) => { if (s is System.Windows.Controls.ToolTip t) Follow(t, t.PlacementTarget); }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((s, _) => { if (s is System.Windows.Controls.ContextMenu m) Follow(m, m.PlacementTarget); }));
    }

    /// <summary>The popup's window is kept out of the capture when its owner's window is.</summary>
    private static void Follow(System.Windows.Media.Visual popup, UIElement? owner)
    {
        try
        {
            if (owner is null || PresentationSource.FromVisual(owner) is not HwndSource from || !IsHiddenFromCapture(from.Handle)) return;
            if (PresentationSource.FromVisual(popup) is HwndSource src) HideFromCapture(src.Handle);
        }
        catch { }
    }

    /// <summary>Is this window kept out of every capture?</summary>
    public static bool IsHiddenFromCapture(IntPtr h) => GetWindowDisplayAffinity(h, out var affinity) && affinity == 0x11;

    /// <summary>The same for a window of ours that isn't a Window (a popup's).</summary>
    public static bool HideFromCapture(IntPtr h)
    {
        try
        {
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

    /// <summary>
    /// The work area (the monitor without its taskbar), in pixels, of what is picked: its monitor (a whole one or a
    /// part of one), or the monitor a window is mostly on right now; the main monitor when there is nothing picked.
    /// </summary>
    public static Int32Rect WorkAreaOf(ScreenPick? pick)
    {
        var mon = pick switch
        {
            { Kind: ScreenPick.Kinds.Window } when IsWindow(pick.Window) => MonitorFromWindow(pick.Window, 2 /* NEAREST */),
            not null => pick.Monitor,
            _ => IntPtr.Zero,
        };
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi))
        {
            mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromPoint(0, 1 /* PRIMARY */), ref mi)) return new Int32Rect(0, 0, 1280, 720);
        }
        var w = mi.rcWork;
        return new Int32Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
    }

    /// <summary>A window of ours, in pixels (null before it has a handle).</summary>
    public static Int32Rect? PixelBounds(Window window)
    {
        var h = new WindowInteropHelper(window).Handle;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    /// <summary>
    /// Puts a window of ours on a work area (pixels): first into its middle, so the window takes that monitor's
    /// scale (monitors may differ), then where <paramref name="place"/> says, given the window's size in pixels
    /// and that monitor's scale (1 = 96 dpi); it returns the top left, or a whole rectangle to resize it too.
    /// </summary>
    public static void PlaceIn(Window window, Int32Rect area, Func<int, int, double, Int32Rect> place)
    {
        var h = new WindowInteropHelper(window).EnsureHandle();
        const uint flags = 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */;
        SetWindowPos(h, IntPtr.Zero, area.X + area.Width / 2, area.Y + area.Height / 2, 0, 0, flags);
        if (!GetWindowRect(h, out var r)) return;
        var scale = GetDpiForWindow(h) is var dpi and > 0 ? dpi / 96.0 : 1.0;
        var to = place(r.Right - r.Left, r.Bottom - r.Top, scale);
        // never outside the work area
        var w = to.Width > 0 ? Math.Min(to.Width, area.Width) : r.Right - r.Left;
        var hh = to.Height > 0 ? Math.Min(to.Height, area.Height) : r.Bottom - r.Top;
        var x = Math.Clamp(to.X, area.X, Math.Max(area.X, area.X + area.Width - w));
        var y = Math.Clamp(to.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - hh));
        SetWindowPos(h, IntPtr.Zero, x, y, w, hh, to.Width > 0 ? flags & ~0x0001u : flags);
    }

    /// <summary>Whether the middle of a rectangle (pixels) lies in an area.</summary>
    public static bool Inside(Int32Rect r, Int32Rect area)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return cx >= area.X && cx < area.X + area.Width && cy >= area.Y && cy < area.Y + area.Height;
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
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
