using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Ocean.App.Services;

/// <summary>
/// Keeps one of our windows on every virtual desktop (Task View's "Show this window on all desktops"), used by the
/// picture-in-picture window while it is pinned.
///
/// Windows offers no public call for that switch, so this goes two ways:
/// 1. The shell's own pinning (the interfaces Task View uses; unchanged since Windows 10, and every call is guarded):
///    the window is simply on all desktops, switching shows no gap.
/// 2. If that is not there (a shell that changed it, or Explorer not running), the documented
///    IVirtualDesktopManager: while pinned, a light check notices a switch and moves the window to the desktop now shown.
/// Neither ever fails the window: at worst it stays on its desktop, as before.
/// </summary>
public sealed class AllDesktops : IDisposable
{
    private readonly Window _window;
    private DispatcherTimer? _follow;
    private bool _on;
    private bool _shellPinned;

    public AllDesktops(Window window) { _window = window; }

    /// <summary>On every desktop (true) or only on the one it is on (false). Safe to call before the window shows.</summary>
    public void Set(bool on)
    {
        _on = on;
        if (!_window.IsVisible)
        {
            // the shell knows a window only once it shows: apply then
            _window.IsVisibleChanged -= OnShown;
            if (on) _window.IsVisibleChanged += OnShown;
            return;
        }
        Apply(attempt: 0);
    }

    private void OnShown(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible) return;
        _window.IsVisibleChanged -= OnShown;
        // the shell creates its view of a new window a moment after it shows
        _window.Dispatcher.BeginInvoke(() => Apply(attempt: 0), DispatcherPriority.ApplicationIdle);
    }

    private void Apply(int attempt)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (_on)
        {
            if (ShellPins.TrySet(hwnd, true) is true) { _shellPinned = true; StopFollowing(); return; }
            // the shell may not know the window yet: one more go shortly, then follow instead
            if (attempt < 3)
            {
                var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                retry.Tick += (_, _) => { retry.Stop(); if (_on && _window.IsVisible) Apply(attempt + 1); };
                retry.Start();
                return;
            }
            StartFollowing();
        }
        else
        {
            StopFollowing();
            if (_shellPinned) { ShellPins.TrySet(hwnd, false); _shellPinned = false; }
        }
    }

    // ---- 2. follow the desktop shown (documented API) ----

    private void StartFollowing()
    {
        if (_follow is not null || !Follower.Available) return;
        _follow = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _follow.Tick += (_, _) => Follower.Follow(new WindowInteropHelper(_window).Handle);
        _follow.Start();
        AppLog.Info("PiP", "the shell's all-desktops pin is not available; following the desktop shown instead");
    }

    private void StopFollowing() { _follow?.Stop(); _follow = null; }

    public void Dispose()
    {
        _window.IsVisibleChanged -= OnShown;
        StopFollowing();
        if (_shellPinned)
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero) ShellPins.TrySet(hwnd, false);
            _shellPinned = false;
        }
        _on = false;
    }

    /// <summary>Is the window pinned to all desktops by the shell (null: cannot tell)? For checks.</summary>
    public static bool? IsShellPinned(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero ? null : ShellPins.IsPinned(hwnd);
    }

    // ---- 1. the shell's pinning ----

    private static class ShellPins
    {
        private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        private static readonly Guid PinnedAppsService = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
        private static bool _broken;
        private static IVirtualDesktopPinnedApps? _pins;
        private static IApplicationViewCollection? _views;

        private static bool Connect()
        {
            if (_broken) return false;
            if (_pins is not null && _views is not null) return true;
            try
            {
                var type = Type.GetTypeFromCLSID(ImmersiveShell, throwOnError: true)!;
                var shell = (IServiceProvider10)Activator.CreateInstance(type)!;
                var viewsId = typeof(IApplicationViewCollection).GUID;
                _views = (IApplicationViewCollection)shell.QueryService(ref viewsId, ref viewsId);
                var pinsService = PinnedAppsService;
                var pinsId = typeof(IVirtualDesktopPinnedApps).GUID;
                _pins = (IVirtualDesktopPinnedApps)shell.QueryService(ref pinsService, ref pinsId);
                return true;
            }
            catch (Exception ex)
            {
                // this Windows has it otherwise: don't ask again this session
                _broken = true; _pins = null; _views = null;
                AppLog.Info("PiP", $"no all-desktops pin from the shell ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        /// <summary>true: done; false: the shell does not know the window (yet); null: not available here.</summary>
        public static bool? TrySet(IntPtr hwnd, bool pinned)
        {
            if (!Connect()) return null;
            var view = IntPtr.Zero;
            try
            {
                if (_views!.GetViewForHwnd(hwnd, out view) < 0 || view == IntPtr.Zero) return false;
                var now = _pins!.IsViewPinned(view);
                if (now != pinned) { if (pinned) _pins.PinView(view); else _pins.UnpinView(view); }
                return true;
            }
            catch (COMException) { _pins = null; _views = null; return false; } // e.g. Explorer restarted: connect afresh next time
            catch (Exception ex) when (ex is InvalidCastException or NotImplementedException) { _broken = true; return null; }
            finally
            {
                if (view != IntPtr.Zero) Marshal.Release(view);
            }
        }

        public static bool? IsPinned(IntPtr hwnd)
        {
            if (!Connect()) return null;
            var view = IntPtr.Zero;
            try { return _views!.GetViewForHwnd(hwnd, out view) < 0 || view == IntPtr.Zero ? null : _pins!.IsViewPinned(view); }
            catch { return null; }
            finally { if (view != IntPtr.Zero) Marshal.Release(view); }
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider10
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        /// <summary>Only the first four methods are used; the order is the shell's.</summary>
        [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationViewCollection
        {
            [PreserveSig] int GetViews(out IntPtr array);
            [PreserveSig] int GetViewsByZOrder(out IntPtr array);
            [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
            [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
        }

        [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopPinnedApps
        {
            bool IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId);
            void PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            void UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            bool IsViewPinned(IntPtr view);
            void PinView(IntPtr view);
            void UnpinView(IntPtr view);
        }
    }

    // ---- 2. the documented desktop manager ----

    private static class Follower
    {
        private static IVirtualDesktopManager? _manager;
        private static bool _broken;

        public static bool Available => Manager() is not null;

        private static IVirtualDesktopManager? Manager()
        {
            if (_broken) return null;
            try { return _manager ??= (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
            catch { _broken = true; return null; }
        }

        /// <summary>If the desktop shown is not the window's, move it there (the desktop of the window in front).</summary>
        public static void Follow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || Manager() is not { } m) return;
            try
            {
                if (m.IsWindowOnCurrentVirtualDesktop(hwnd)) return;
                var front = GetForegroundWindow();
                if (front == IntPtr.Zero || front == hwnd) return;
                var desktop = m.GetWindowDesktopId(front);
                if (desktop == Guid.Empty) return; // e.g. the taskbar or the desktop itself: wait for a real window
                m.MoveWindowToDesktop(hwnd, ref desktop);
            }
            catch (COMException) { } // a window that went meanwhile, a desktop being switched: next tick
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
        private class VirtualDesktopManagerClass { }

        [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
            Guid GetWindowDesktopId(IntPtr topLevelWindow);
            void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }
    }
}
