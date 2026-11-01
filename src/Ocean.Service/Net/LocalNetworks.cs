using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ocean.Service.Net;

/// <summary>
/// This PC's IPv4 networks, refreshed whenever an address changes. Used to rank a peer's addresses when it is heard
/// on several adapters: an address on the same subnet as one of our physical (Ethernet / Wi-Fi) adapters is the real
/// LAN; virtual switches (Hyper-V, WSL, VirtualBox, VMware) and VPN/TUN adapters rank lower. Also the rule of what
/// counts as the local network at all (<see cref="IsLan"/>): VPN adapters never do.
/// </summary>
public static class LocalNetworks
{
    private sealed record Net(uint Network, uint Mask, bool Physical, bool Vpn, IPAddress Address, string Adapter);

    // Declared before _nets: static initializers run in order, and Read() needs these lists.
    private static readonly string[] VirtualHints =
        { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "wsl", "docker", "tap-", "wintun", "wireguard",
          "vpn", "zerotier", "tailscale", "hamachi", "loopback", "pseudo", "teredo", "bluetooth" };
    /// <summary>
    /// Adapters that carry traffic to somewhere else, over the internet: the other end of a VPN is not this PC's
    /// local network, whatever addresses it hands out (many VPN services put their customers on one shared subnet).
    /// Matched against the adapter's name and description, lower case.
    /// </summary>
    private static readonly string[] VpnHints =
        { "vpn", "astrill", "tap-", "tap adapter", "wintun", "wireguard", "tunnel", " tun", "zerotier", "tailscale", "hamachi",
          "radmin", "openvpn", "softether", "nordlynx", "expressvpn", "anyconnect", "fortinet", "forticlient", "pangp",
          "globalprotect", "juniper", "pulse secure", "netextender", "sonicwall", "check point", "checkpoint", "sstp", "l2tp",
          "ikev2", "pptp", "wan miniport", "teredo", "ppp" };
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

    /// <summary>
    /// Whether <paramref name="a"/> is on this PC's local network, the only place Ocean talks to: this PC itself
    /// (loopback), or a private address (10/8, 172.16/12, 192.168/16, 169.254/16) on the subnet of one of this PC's
    /// adapters that is not a VPN. Internet addresses, VPN subnets (Astrill, WireGuard, Tailscale's 100.64/10...)
    /// and IPv6 (Ocean finds PCs over IPv4 only) never are.
    /// </summary>
    public static bool IsLan(IPAddress? a)
    {
        if (a is null) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var ip = ToUInt(a);
        if (!IsPrivate(ip)) return false;
        foreach (var n in _nets)
            if (!n.Vpn && (ip & n.Mask) == n.Network) return true;
        return false;
    }

    /// <summary>
    /// Where discovery broadcasts go: the broadcast address of each local network (private addresses, not a VPN),
    /// so a beacon never leaves through a VPN adapter or onto a network with internet addresses.
    /// </summary>
    public static List<(IPAddress Broadcast, string Adapter)> LanBroadcasts()
        => _nets.Where(n => !n.Vpn && IsPrivate(ToUInt(n.Address)) && n.Mask < 0xFFFFFFFE) // a /31 or /32 has no one else
                .Select(n => (IPAddress.Parse(ToIp(n.Network | ~n.Mask)), n.Adapter))
                .GroupBy(x => x.Item1).Select(g => g.First()).ToList();

    private static bool IsPrivate(uint ip)
        => ip >> 24 == 10 || ip >> 20 == 0xAC1 || ip >> 16 == 0xC0A8 || ip >> 16 == 0xA9FE;

    /// <summary>
    /// The local addresses to multicast on: physical adapters (never a VPN or virtual switch, which would carry the
    /// packets somewhere else) on the same subnet as the given peers, i.e. the adapter Ocean reaches them through;
    /// all physical adapters when no peer address matches. Each with its adapter's name, for the log.
    /// </summary>
    public static List<(IPAddress Address, string Adapter)> MulticastAddresses(IEnumerable<IPAddress> peers)
    {
        var physical = _nets.Where(n => n.Physical).ToList();
        var peerIps = peers.Where(p => p.AddressFamily == AddressFamily.InterNetwork).Select(ToUInt).ToList();
        var onPeerLan = physical.Where(n => peerIps.Any(p => (p & n.Mask) == n.Network)).ToList();
        return (onPeerLan.Count > 0 ? onPeerLan : physical)
            .GroupBy(n => n.Address).Select(g => (g.Key, g.First().Adapter)).ToList();
    }

    public static string Describe() => string.Join(", ", _nets.Select(n => $"{ToIp(n.Network)}/{MaskBits(n.Mask)}{(n.Vpn ? " (VPN, ignored)" : n.Physical ? "" : " (virtual)")}"));

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
                var vpn = nic.NetworkInterfaceType is NetworkInterfaceType.Ppp || VpnHints.Any(text.Contains);
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var mask = ToUInt(ua.IPv4Mask);
                    if (mask == 0) continue;
                    list.Add(new Net(ToUInt(ua.Address) & mask, mask, physical && !vpn, vpn, ua.Address, nic.Name));
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
