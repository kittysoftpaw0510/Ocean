using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Net;

/// <summary>
/// Serving side of the reverse connection. When a node announces that it cannot reach us (our firewall blocks
/// incoming connections), we connect out to it, long-poll for its requests, run each one against our own peer API
/// (over loopback, on its behalf, with all the usual checks) and stream the answer back.
/// </summary>
public sealed class RelayWorker : IDisposable
{
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(3);

    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly NodeIdentity _identity;
    private readonly ConfigStore _config;
    private readonly ILogger<RelayWorker> _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _links = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWanted = new();
    private readonly HttpClient _self;

    public RelayWorker(PeerRegistry registry, PeerClient client, NodeIdentity identity, ConfigStore config, ILogger<RelayWorker> log)
    {
        _registry = registry; _client = client; _identity = identity; _config = config; _log = log;
        // Our own peer API over loopback: present our certificate and accept only our own certificate back.
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseProxy = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && NodeIdentity.ComputeNodeId(new X509Certificate2(cert)) == identity.NodeId,
            },
        };
        _self = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public int ActiveLinks => _links.Count;

    /// <summary>A node's beacon asks us to connect back: start (or keep) the link to it.</summary>
    public void Wanted(string peerId)
    {
        _lastWanted[peerId] = DateTimeOffset.UtcNow;
        if (_links.ContainsKey(peerId)) return;
        var cts = new CancellationTokenSource();
        if (!_links.TryAdd(peerId, cts)) return;
        _ = Task.Run(() => RunLink(peerId, cts.Token));
    }

    private async Task RunLink(string peerId, CancellationToken ct)
    {
        _log.LogInformation("reverse link to {Peer}: started (it cannot connect to us directly)", Name(peerId));
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow - _lastWanted.GetValueOrDefault(peerId) > IdleStop) break; // no longer asked for
                if (!_registry.TryGetOnline(peerId, out var peer)) break;
                try
                {
                    var req = _client.Build(peer, HttpMethod.Post, "/p/relay/poll");
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(40));
                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                    failures = 0;
                    if (resp.StatusCode == HttpStatusCode.NoContent) continue;
                    if (!resp.IsSuccessStatusCode) { await Task.Delay(2000, ct); continue; }
                    var job = await resp.Content.ReadFromJsonAsync<RelayJob>(Json.Options, ct);
                    if (job is not null) _ = Task.Run(() => Serve(peer, job, ct), CancellationToken.None);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    failures++;
                    _log.LogDebug("reverse link to {Peer}: poll failed ({Msg})", peer.Name, ex.Message);
                    if (failures >= 5) { _log.LogWarning("reverse link to {Peer}: cannot reach it either; giving up for now", peer.Name); break; }
                    await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _links.TryRemove(peerId, out _);
            _log.LogInformation("reverse link to {Peer}: stopped", Name(peerId));
        }
    }

    /// <summary>Runs one request on our own peer API on behalf of <paramref name="peer"/> and uploads the answer.</summary>
    private async Task Serve(Peer peer, RelayJob job, CancellationToken ct)
    {
        try
        {
            if (!job.Path.StartsWith("/p/", StringComparison.Ordinal) || job.Path.StartsWith("/p/relay", StringComparison.Ordinal)) return;
            var local = new HttpRequestMessage(new HttpMethod(job.Method), $"https://127.0.0.1:{_config.Current.PeerPort}{job.Path}");
            foreach (var (k, v) in job.Headers)
                if (k.StartsWith("X-Ocean-", StringComparison.OrdinalIgnoreCase) || k.Equals("Range", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase))
                    local.Headers.TryAddWithoutValidation(k, v);
            // Catalogs travel compressed over the reverse link too (the requesting side unpacks them).
            if (job.Path.StartsWith("/p/catalog", StringComparison.Ordinal)) local.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
            local.Headers.Remove("X-Ocean-OnBehalf");
            local.Headers.TryAddWithoutValidation("X-Ocean-OnBehalf", peer.Id); // honoured only for our own key over loopback
            if (job.Body is not null)
            {
                local.Content = new ByteArrayContent(Convert.FromBase64String(job.Body));
                if (job.ContentType is not null) local.Content.Headers.TryAddWithoutValidation("Content-Type", job.ContentType);
            }
            using var answer = await _self.SendAsync(local, HttpCompletionOption.ResponseHeadersRead, ct);

            var headers = new Dictionary<string, string>();
            foreach (var h in answer.Headers) headers[h.Key] = string.Join(",", h.Value);
            foreach (var h in answer.Content.Headers) headers[h.Key] = string.Join(",", h.Value);

            var upload = _client.Build(peer, HttpMethod.Post, "/p/relay/response/" + Uri.EscapeDataString(job.Id));
            upload.Headers.TryAddWithoutValidation("X-Relay-Status", ((int)answer.StatusCode).ToString());
            upload.Headers.TryAddWithoutValidation("X-Relay-Headers", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Json.Serialize(headers))));
            await using var body = await answer.Content.ReadAsStreamAsync(ct);
            upload.Content = new StreamContent(body, 1 << 17); // streamed (chunked) upload: video flows as it is read
            using var done = await _client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "reverse link to {Peer}: serving {Path} failed", peer.Name, job.Path);
        }
    }

    private string Name(string peerId) => _registry.TryGet(peerId, out var p) ? p.Name : peerId[..12];

    public void Dispose()
    {
        foreach (var c in _links.Values) c.Cancel();
        _self.Dispose();
    }
}
