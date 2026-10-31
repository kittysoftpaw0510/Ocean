using System.Runtime.InteropServices;
using System.Text;

namespace Ocean.Core;

/// <summary>
/// Finds old-style network add-ons ("non-IFS Layered Service Providers") in the Windows networking stack.
/// <para>
/// Some VPN clients, "network accelerators", proxy tools and security suites install one. It reports finished network
/// operations a second time, which modern .NET cannot survive: any program using its networking (Ocean's service
/// included) is terminated within seconds with "'overlapped' has already been freed" or "NativeOverlapped cannot be
/// reused". Ocean can't work around it, so it names the add-on instead, so the user knows what to remove.
/// </para>
/// </summary>
public static class WinsockCatalog
{
    public sealed record Provider(string Name, string? Path);

    /// <summary>Layered providers whose sockets are not real kernel handles (the ones that break .NET). Empty when fine.</summary>
    public static List<Provider> IncompatibleProviders()
    {
        var result = new List<Provider>();
        try
        {
            var started = WSAStartup(0x0202, new byte[512]) == 0;
            try
            {
                uint size = 0;
                WSAEnumProtocolsW(IntPtr.Zero, IntPtr.Zero, ref size);
                if (size == 0) return result;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    var count = WSAEnumProtocolsW(IntPtr.Zero, buffer, ref size);
                    var itemSize = Marshal.SizeOf<WSAPROTOCOL_INFOW>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<WSAPROTOCOL_INFOW>(buffer + i * itemSize);
                        var layered = info.ProtocolChain.ChainLen != 1;          // 0 = the add-on itself, >1 = a chain through it
                        var ifs = (info.dwServiceFlags1 & XP1_IFS_HANDLES) != 0;
                        if (!layered || ifs) continue;
                        var name = string.IsNullOrWhiteSpace(info.szProtocol) ? info.ProviderId.ToString() : info.szProtocol.Trim();
                        var path = ProviderPath(info.ProviderId);
                        if (seen.Add(path ?? name)) result.Add(new Provider(name, path));
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { if (started) WSACleanup(); }
        }
        catch { /* detection is best effort */ }
        return result;
    }

    /// <summary>One line for logs and messages, e.g. "Some VPN [C:\...\lsp.dll]".</summary>
    public static string Describe(IEnumerable<Provider> providers)
        => string.Join("; ", providers.Select(p => p.Path is null ? p.Name : $"{p.Name} [{p.Path}]"));

    private static string? ProviderPath(Guid id)
    {
        try
        {
            var sb = new StringBuilder(260);
            var len = sb.Capacity;
            if (WSCGetProviderPath(ref id, sb, ref len, out _) != 0) return null;
            var raw = sb.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : Environment.ExpandEnvironmentVariables(raw);
        }
        catch { return null; }
    }

    internal const uint XP1_IFS_HANDLES = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WSAPROTOCOLCHAIN
    {
        public int ChainLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] ChainEntries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WSAPROTOCOL_INFOW
    {
        public uint dwServiceFlags1, dwServiceFlags2, dwServiceFlags3, dwServiceFlags4, dwProviderFlags;
        public Guid ProviderId;
        public uint dwCatalogEntryId;
        public WSAPROTOCOLCHAIN ProtocolChain;
        public int iVersion, iAddressFamily, iMaxSockAddr, iMinSockAddr, iSocketType, iProtocol, iProtocolMaxOffset,
                   iNetworkByteOrder, iSecurityScheme;
        public uint dwMessageSize, dwProviderReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szProtocol;
    }

    [DllImport("ws2_32.dll")] internal static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll")] private static extern int WSACleanup();
    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)] internal static extern int WSAEnumProtocolsW(IntPtr protocols, IntPtr buffer, ref uint length);
    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)] private static extern int WSCGetProviderPath(ref Guid providerId, StringBuilder path, ref int length, out int error);
}
