using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ocean.Service.Net;

/// <summary>
/// This PC's IPv4 networks, refreshed whenever an address changes. Used to rank a peer's addresses when it is heard
/// on several adapters: an address on the same subnet as one of our physical (Ethernet / Wi-Fi) adapters is the real
/// LAN; virtual switches (Hyper-V, WSL, VirtualBox, VMware) and VPN/TUN adapters rank lower.
/// </summary>
public static class LocalNetworks
{
    private sealed record Net(uint Network, uint Mask, bool Physical);

    // Declared before _nets: static initializers run in order, and Read() needs this list.
    private static readonly string[] VirtualHints =
        { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "wsl", "docker", "tap-", "wintun", "wireguard",
          "vpn", "zerotier", "tailscale", "hamachi", "loopback", "pseudo", "teredo", "bluetooth" };
    private static volatile List<Net> _nets = Read();

    /// <summary>Raised after the PC's addresses changed (Wi-Fi switched, VPN connected, cable plugged...).</summary>
    public static event Action? Changed;

    static LocalNetworks()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => { _nets = Read(); Changed?.Invoke(); };
    }

    /// <summary>2 = same subnet as a physical adapter, 1 = same subnet as another local adapter, 0 = not local.</summary>
    public static int Rank(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return 0;
        var ip = ToUInt(a);
        var best = 0;
        foreach (var n in _nets)
            if ((ip & n.Mask) == n.Network) best = Math.Max(best, n.Physical ? 2 : 1);
        return best;
    }

    public static string Describe() => string.Join(", ", _nets.Select(n => $"{ToIp(n.Network)}/{MaskBits(n.Mask)}{(n.Physical ? "" : " (virtual)")}"));

    private static List<Net> Read()
    {
        var list = new List<Net>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var physical = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                                   or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                               && !VirtualHints.Any(text.Contains);
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var mask = ToUInt(ua.IPv4Mask);
                    if (mask == 0) continue;
                    list.Add(new Net(ToUInt(ua.Address) & mask, mask, physical));
                }
            }
        }
        catch { }
        return list;
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static string ToIp(uint v) => new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }).ToString();
    private static int MaskBits(uint m) { var n = 0; while (m != 0) { n += (int)(m & 1); m >>= 1; } return n; }
}
