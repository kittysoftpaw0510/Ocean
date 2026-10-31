using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ocean.Core;

/// <summary>
/// Creates sockets. Normally plain <see cref="Socket"/>s; with <c>OCEAN_BASE_PROVIDER=1</c> (an experiment, off by
/// default) directly on Windows' own network provider (MSAFD), skipping add-ons layered on top of it. That alone
/// did not stop Astrill's ASProxy from breaking asynchronous I/O (it hooks deeper than the catalog), which is why
/// Ocean relies on <see cref="NetworkCompat"/>'s classic mode instead; IPmsg uses ordinary sockets too.
/// </summary>
public static class BaseSockets
{
    private static readonly Lazy<bool> _useBase = new(() => Environment.GetEnvironmentVariable("OCEAN_BASE_PROVIDER") == "1");
    private static readonly ConcurrentDictionary<(AddressFamily, SocketType, ProtocolType), WinsockCatalog.WSAPROTOCOL_INFOW?> _providers = new();
    private static int _started;

    public static bool UseBaseProvider => _useBase.Value;

    /// <summary>Why the last socket fell back to a normal one (null = every socket so far is on the base provider).</summary>
    public static string? FallbackReason { get; private set; }

    public static Socket Create(AddressFamily family, SocketType type, ProtocolType protocol)
    {
        if (!UseBaseProvider) return new Socket(family, type, protocol);
        try
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) WinsockCatalog.WSAStartup(0x0202, new byte[512]); // kept for the process lifetime
            if (_providers.GetOrAdd((family, type, protocol), k => FindBase(k.Item1, k.Item2, k.Item3)) is { } info)
            {
                var handle = WSASocketW((int)family, (int)type, (int)protocol, ref info, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT);
                if (handle != InvalidSocket) return new Socket(new SafeSocketHandle(handle, ownsHandle: true));
                FallbackReason = $"WSASocket failed for {family}/{type}/{protocol} (error {Marshal.GetLastPInvokeError()})";
            }
            else FallbackReason = $"no base provider for {family}/{type}/{protocol}";
        }
        catch (Exception ex) { FallbackReason = $"{family}/{type}/{protocol}: {ex.Message}"; }
        return new Socket(family, type, protocol);
    }

    /// <summary>The non-layered, IFS entry (MSAFD) for this kind of socket, or null.</summary>
    private static WinsockCatalog.WSAPROTOCOL_INFOW? FindBase(AddressFamily family, SocketType type, ProtocolType protocol)
    {
        uint size = 0;
        WinsockCatalog.WSAEnumProtocolsW(IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var count = WinsockCatalog.WSAEnumProtocolsW(IntPtr.Zero, buffer, ref size);
            var itemSize = Marshal.SizeOf<WinsockCatalog.WSAPROTOCOL_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WinsockCatalog.WSAPROTOCOL_INFOW>(buffer + i * itemSize);
                if (info.ProtocolChain.ChainLen == 1 && (info.dwServiceFlags1 & WinsockCatalog.XP1_IFS_HANDLES) != 0
                    && info.iAddressFamily == (int)family && info.iSocketType == (int)type && info.iProtocol == (int)protocol)
                    return info;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint WSA_FLAG_OVERLAPPED = 0x01;
    private const uint WSA_FLAG_NO_HANDLE_INHERIT = 0x80;
    private static readonly IntPtr InvalidSocket = new(-1);

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WSASocketW(int af, int type, int protocol, ref WinsockCatalog.WSAPROTOCOL_INFOW info, uint group, uint flags);
}
