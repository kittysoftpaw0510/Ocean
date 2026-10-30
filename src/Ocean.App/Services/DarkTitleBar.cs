using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ocean.App.Services;

/// <summary>Asks DWM for a dark title bar so standard windows match the app theme (Windows 10 20H1+ / 11).</summary>
public static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        try
        {
            var h = new WindowInteropHelper(window).EnsureHandle();
            var on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            var corner = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33, ref corner, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
