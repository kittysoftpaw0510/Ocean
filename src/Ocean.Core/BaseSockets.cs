using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ocean.Core;

/// <summary>
/// Creates sockets directly on Windows' own network provider (MSAFD), skipping network add-ons layered on top of it
/// (see <see cref="WinsockCatalog"/>). With an old-style add-on installed, a normal .NET socket goes through it and
/// the process dies within seconds; a socket on the base provider never touches it. Native programs such as IPmsg
/// are unaffected by those add-ons for a different reason (they don't use I/O completion ports); this gives Ocean the
/// same robustness.
/// <para>
/// Active only when such an add-on is present (or OCEAN_BYPASS_LSP=1 is set, for testing). On a normal PC the base
/// provider is what every socket gets anyway, so nothing changes there. Any failure falls back to a normal socket.
/// </para>
/// </summary>
public static class BaseSockets
{
    private static readonly Lazy<bool> _bypass = new(() =>
        Environment.GetEnvironmentVariable("OCEAN_BYPASS_LSP") == "1" || WinsockCatalog.IncompatibleProviders().Count > 0);

    private static readonly ConcurrentDictionary<(AddressFamily, SocketType, ProtocolType), WinsockCatalog.WSAPROTOCOL_INFOW?> _providers = new();
    private static int _started;

    /// <summary>True when sockets are created on the base provider (an incompatible add-on was found).</summary>
    public static bool Bypass => _bypass.Value;

    /// <summary>Why the last socket fell back to a normal one (null = every socket so far is on the base provider).</summary>
    public static string? FallbackReason { get; private set; }

    /// <summary>A socket on Windows' own provider when <see cref="Bypass"/> is on, otherwise a normal socket.</summary>
    public static Socket Create(AddressFamily family, SocketType type, ProtocolType protocol)
    {
        if (!Bypass) return new Socket(family, type, protocol);
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

    /// <summary>For <see cref="SocketsHttpHandler.ConnectCallback"/>: connects on a base-provider socket.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = Create(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
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
