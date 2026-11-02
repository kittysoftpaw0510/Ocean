using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ocean.Service.State;

/// <summary>This PC's Windows edition, for the other PCs' "Node info" window ("Windows 11 Pro 24H2 (build 26100)").</summary>
public static class SystemInfo
{
    private static readonly Lazy<string> WindowsName = new(Read);

    public static string Windows => WindowsName.Value;

    private static string Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var release = key?.GetValue("DisplayVersion") as string;
            var build = Environment.OSVersion.Version.Build;
            // Windows 11 still calls itself "Windows 10" in the registry; build 22000 and later is Windows 11.
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product}{(string.IsNullOrEmpty(release) ? "" : " " + release)} (build {build})";
        }
        catch { return RuntimeInformation.OSDescription; }
    }
}
