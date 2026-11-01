using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Ocean.Core;

namespace Ocean.Service.Net;

/// <summary>
/// Ocean talks to other PCs on this PC's own local networks and nowhere else: never over the internet, never through
/// a VPN (Astrill, WireGuard, Tailscale, a company VPN...). The rule (<see cref="LocalNetworks.IsLan"/>) is applied
/// wherever Ocean meets another PC: beacons heard and sent (discovery), connections accepted (the peer port, checked
/// before TLS) and made (every peer request, bridges and reverse links included), and multicast packets received.
/// The update check to GitHub is the one connection to the internet, and it never carries anything about peers.
/// </summary>
public static class LanOnly
{
    public static ILogger? Log { get; set; }

    private static readonly ConcurrentDictionary<string, long> LastLogged = new(StringComparer.Ordinal);

    /// <summary>Records a refused address (at most once per address and kind every 10 minutes).</summary>
    public static void Refused(string what, IPAddress? address)
    {
        var key = what + "|" + address;
        var now = Environment.TickCount64;
        if (LastLogged.TryGetValue(key, out var last) && now - last < 600_000) return;
        LastLogged[key] = now;
        if (LastLogged.Count > 4096) LastLogged.Clear();
        Log?.LogInformation("local network only: ignored {What} {Address} (not on this PC's local network)", what, address?.ToString() ?? "?");
    }

    /// <summary>
    /// ConnectCallback for every client that talks to peers: refuses an address outside the local networks before a
    /// single packet leaves, then connects as usual (blocking sockets in classic mode, see <see cref="NetworkCompat"/>).
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var ip) || !LocalNetworks.IsLan(ip))
        {
            Refused("connection to", ip);
            throw new HttpRequestException(HttpRequestError.ConnectionError,
                $"{host} is not on this PC's local network; Ocean only connects to PCs on the local network.");
        }
        if (NetworkCompat.Classic) return await NetworkCompat.ConnectAsync(context, ct).ConfigureAwait(false);
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
