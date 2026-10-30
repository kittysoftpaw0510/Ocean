using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Service.Discovery;
using Ocean.Service.State;

namespace Ocean.Service.Peers;

/// <summary>
/// HTTPS client for talking to other nodes.
/// - Mutual TLS: we present our node certificate; the server must present the key its node id was derived from
///   (one connection pool per peer, so the pin is simply "this peer's id").
/// - Multi-homed peers: a PC is often heard on several adapters (Ethernet, Wi-Fi, VPN/virtual switches). Requests
///   go to the last address that worked and fail over to the others with a short connect timeout.
/// - Adds the shared-secret proof and the caller's grant tokens to every request.
/// </summary>
public sealed class PeerClient : IDisposable
{
    private static readonly HttpRequestOptionsKey<Peer> PeerKey = new("ocean.peer");
    private static readonly HttpRequestOptionsKey<string> PathKey = new("ocean.path");

    private readonly NodeIdentity _identity;
    private readonly ConfigStore _config;
    private readonly ILogger<PeerClient> _log;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly Net.RelayHub _relay;

    public PeerClient(NodeIdentity identity, ConfigStore config, Net.RelayHub relay, ILogger<PeerClient> log)
    {
        _identity = identity; _config = config; _relay = relay; _log = log;
    }

    private HttpClient ClientFor(string peerId) => _clients.GetOrAdd(peerId, id =>
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            // catalogs come compressed (videos never are)
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Brotli,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { _identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) => Pinned(cert, id),
            },
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ocean/" + OceanDefaults.AppVersion);
        return client;
    });

    /// <summary>Key pinning: the server must prove possession of the key its node id was derived from.</summary>
    private bool Pinned(X509Certificate? cert, string expectedId)
    {
        if (cert is null) return false;
        using var c2 = new X509Certificate2(cert);
        var actual = NodeIdentity.ComputeNodeId(c2);
        var ok = string.Equals(actual, expectedId, StringComparison.Ordinal);
        if (!ok) _log.LogWarning("tls: key mismatch: expected {Expected}, got {Actual}", NodeIdentity.ShortId(expectedId), actual is null ? "?" : NodeIdentity.ShortId(actual));
        return ok;
    }

    public HttpRequestMessage Build(Peer peer, HttpMethod method, string path, IReadOnlyDictionary<string, string>? grants = null)
    {
        var cfg = _config.Current;
        var req = new HttpRequestMessage(method, peer.BaseUrl + path);
        req.Options.Set(PeerKey, peer);
        req.Options.Set(PathKey, path);
        req.Headers.TryAddWithoutValidation("X-Ocean-Realm", Realm.Proof(cfg.Secret, _identity.NodeId, peer.Id));
        req.Headers.TryAddWithoutValidation("X-Ocean-Name", Ascii(cfg.NodeName));
        req.Headers.TryAddWithoutValidation("X-Ocean-User", Ascii(cfg.UserName));
        req.Headers.TryAddWithoutValidation("X-Ocean-Host", Ascii(Environment.MachineName));
        if (grants is { Count: > 0 })
            req.Headers.TryAddWithoutValidation("X-Ocean-Grants", string.Join(';', grants.Select(kv => kv.Key + "=" + kv.Value)));
        return req;
    }

    /// <summary>Headers must be ASCII; non-ASCII names are sent percent-encoded (UTF-8).</summary>
    public static string Ascii(string s) => Uri.EscapeDataString(s);

    public static string FromAscii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    /// <summary>
    /// Sends the request: directly (trying each known address), or — when the node cannot be reached directly,
    /// typically because its firewall blocks incoming connections — through a reverse connection it opens to us.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
    {
        if (!req.Options.TryGetValue(PeerKey, out var peer) || !req.Options.TryGetValue(PathKey, out var path))
            throw new InvalidOperationException("Request was not built with PeerClient.Build");
        var relayable = !path.StartsWith("/p/relay", StringComparison.Ordinal);

        // Already known to need the reverse connection: use it, and only retry the direct route now and then.
        if (relayable && peer.PreferRelay && DateTimeOffset.UtcNow < peer.DirectRetryAt)
            return await _relay.SendAsync(peer.Id, req, ct);

        try
        {
            var resp = await SendDirectAsync(peer, req, path, option, ct);
            if (peer.PreferRelay) { peer.PreferRelay = false; _log.LogInformation("{Peer} is reachable directly again", peer.Name); }
            return resp;
        }
        catch (Exception ex) when (relayable && !ct.IsCancellationRequested
                                   && (ex is TaskCanceledException || (ex is HttpRequestException h && IsUnreachable(h))))
        {
            if (!peer.PreferRelay)
                _log.LogInformation("{Peer} can't be reached directly ({Msg}); using a reverse connection", peer.Name, ex.InnerException?.Message ?? ex.Message);
            peer.PreferRelay = true;
            peer.DirectRetryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            return await _relay.SendAsync(peer.Id, req, ct);
        }
    }

    private async Task<HttpResponseMessage> SendDirectAsync(Peer peer, HttpRequestMessage req, string path, HttpCompletionOption option, CancellationToken ct)
    {
        var client = ClientFor(peer.Id);
        var candidates = peer.Candidates();
        Exception? last = null;
        for (var i = 0; i < Math.Max(1, candidates.Count); i++)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = Clone(req, (candidates.Count > 0 ? peer.UrlFor(candidates[i]) : peer.BaseUrl) + path);
            try
            {
                var resp = await client.SendAsync(attempt, option, ct);
                if (candidates.Count > 0) peer.Preferred = candidates[i];
                return resp;
            }
            catch (HttpRequestException ex) when (IsUnreachable(ex))
            {
                last = ex;
                _log.LogDebug("peer {Peer} not reachable at {Addr} ({Msg})", peer.Name, i < candidates.Count ? candidates[i] : null, ex.InnerException?.Message ?? ex.Message);
                if (i < candidates.Count && Equals(peer.Preferred, candidates[i])) peer.Preferred = null;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex; // connect timeout
                if (i < candidates.Count && Equals(peer.Preferred, candidates[i])) peer.Preferred = null;
            }
        }
        throw last ?? new HttpRequestException("No address for peer");
    }

    private static bool IsUnreachable(HttpRequestException ex)
        => ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
           || ex.InnerException is SocketException or AuthenticationException;

    private static HttpRequestMessage Clone(HttpRequestMessage src, string url)
    {
        var copy = new HttpRequestMessage(src.Method, url) { Content = src.Content, Version = src.Version };
        foreach (var h in src.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in src.Options) ((IDictionary<string, object?>)copy.Options)[o.Key] = o.Value;
        return copy;
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
    }
}
