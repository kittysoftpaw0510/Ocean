using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Api;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Net;

/// <summary>
/// Learns about nodes we have no network path to: every few seconds each node we hear directly is asked which nodes
/// it can reach (<c>/p/neighbors</c>), and those we can't hear ourselves are listed as reached through it.
/// <para>Example: A and B share one network, B and C another. A hears B, B tells A about C, A lists C "via B".</para>
/// </summary>
public sealed class MeshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryOldNode = TimeSpan.FromMinutes(10);

    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly NodeIdentity _identity;
    private readonly ConfigStore _config;
    private readonly ILogger<MeshService> _log;
    private readonly ConcurrentDictionary<string, byte> _busy = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unsupported = new();

    public MeshService(PeerRegistry registry, PeerClient client, NodeIdentity identity, ConfigStore config, ILogger<MeshService> log)
    {
        _registry = registry; _client = client; _identity = identity; _config = config; _log = log;
        _registry.Changed += (p, change) =>
        {
            if (p.Via is not null && change is PeerChange.Added or PeerChange.Online)
                _log.LogInformation("peer {State}: {Name} ({User}@{Host}), reached through {Via} (no network path from here)",
                    change == PeerChange.Added ? "online" : "back online", p.Name, p.User, p.Host, p.ViaName);
        };
    }

    /// <summary>What we can offer <paramref name="callerId"/>: the nodes we hear directly (one PC in between at most).</summary>
    public NeighborsDto Neighbors(string callerId)
    {
        var dto = new NeighborsDto();
        foreach (var p in _registry.Snapshot())
        {
            if (!p.Online || p.Id == callerId || p.Via is not null || string.IsNullOrEmpty(p.PublicKey)) continue;
            dto.Nodes.Add(new NeighborDto
            {
                Id = p.Id, Pk = p.PublicKey, Name = p.Name, User = p.User, Host = p.Host,
                Cat = p.CatalogVersion, Shares = p.ShareCount, Hops = p.Hops,
            });
        }
        return dto;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var p in _registry.Snapshot())
            {
                if (!p.Online || p.Via is not null) continue; // ask only nodes we reach ourselves
                if (_unsupported.TryGetValue(p.Id, out var until) && now < until) continue;
                if (_busy.TryAdd(p.Id, 0)) _ = AskAsync(p, ct);
            }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task AskAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _client.SendAsync(_client.Build(peer, HttpMethod.Get, "/p/neighbors"), HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                _unsupported[peer.Id] = DateTimeOffset.UtcNow + RetryOldNode; // an older Ocean: no relaying through it
                return;
            }
            if (!resp.IsSuccessStatusCode) return;
            var dto = await resp.Content.ReadFromJsonAsync<NeighborsDto>(Json.Options, timeout.Token);
            if (dto is null) return;
            var realm = Realm.Tag(_config.Current.Secret);
            foreach (var n in dto.Nodes.Take(500))
            {
                if (n.Id == _identity.NodeId || n.Id == peer.Id || n.Hops != 0) continue; // one PC in between at most
                if (!SameKey(n)) continue;
                _registry.UpsertRelayed(n, peer, realm);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _log.LogDebug("neighbours of {Peer}: {Msg}", peer.Name, ex.Message);
        }
        finally { _busy.TryRemove(peer.Id, out _); }
    }

    /// <summary>The id must be the hash of the key it comes with (the key is what TLS and signatures are checked against).</summary>
    private static bool SameKey(NeighborDto n)
    {
        try { return n.Id.Length == 64 && NodeIdentity.ComputeNodeId(Convert.FromBase64String(n.Pk)) == n.Id; }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Relay side of <see cref="Via"/>: carries a request from a node that can't reach the target (but reaches us) to
/// the target, and streams the answer back. The request keeps its origin's names, grants and signature; we only
/// add our own proof of the shared secret on the way.
/// </summary>
public sealed class ViaForwarder
{
    private static readonly string[] RequestHeaders = { "Range", "If-Range", "If-None-Match", "If-Modified-Since" };
    private static readonly string[] ResponseHeaders = { "Content-Type", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Retry-After", "Cache-Control" };

    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly NodeIdentity _identity;
    private readonly ILogger<ViaForwarder> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _announced = new();

    public ViaForwarder(PeerRegistry registry, PeerClient client, NodeIdentity identity, ILogger<ViaForwarder> log)
    {
        _registry = registry; _client = client; _identity = identity; _log = log;
    }

    public static string RawTarget(HttpContext ctx)
        => ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw ? raw : ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString;

    public async Task ServeAsync(HttpContext ctx)
    {
        var res = ctx.Response;
        if (!Via.TryParse(RawTarget(ctx), out var targetId, out var inner) || !Via.IsRelayable(inner)) { res.StatusCode = 400; return; }
        _ = int.TryParse(ctx.Request.Headers[Via.HopsHeader], out var hops);
        var caller = PeerContext.Get(ctx);
        if (hops >= Via.MaxHops || targetId == _identity.NodeId) { res.StatusCode = StatusCodes.Status508LoopDetected; return; }
        // only to a PC we hear ourselves: never a second PC in between
        if (!_registry.TryGetOnline(targetId, out var target) || target.Via is not null)
        {
            res.StatusCode = 503;
            await res.WriteAsJsonAsync(new ErrorDto { Error = "unreachable", Message = "That PC can't be reached from here right now." }, Json.Options);
            return;
        }

        var req = _client.Build(target, new HttpMethod(ctx.Request.Method), inner);
        foreach (var (k, v) in ctx.Request.Headers)
        {
            var pass = (k.StartsWith("X-Ocean-", StringComparison.OrdinalIgnoreCase)
                        && !k.Equals("X-Ocean-Realm", StringComparison.OrdinalIgnoreCase) && !k.Equals("X-Ocean-OnBehalf", StringComparison.OrdinalIgnoreCase))
                       || RequestHeaders.Contains(k, StringComparer.OrdinalIgnoreCase);
            if (!pass) continue;
            req.Headers.Remove(k);
            req.Headers.TryAddWithoutValidation(k, v.ToArray());
        }
        req.Headers.Remove(Via.HopsHeader);
        req.Headers.TryAddWithoutValidation(Via.HopsHeader, (hops + 1).ToString());
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            req.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } type) req.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }

        var origin = ctx.Request.Headers[Via.OriginHeader].ToString();
        var key = origin + ">" + targetId;
        if (!_announced.TryGetValue(key, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(10))
        {
            _announced[key] = DateTimeOffset.UtcNow;
            _log.LogInformation("relaying for {From} to {To} ({Origin}: they have no network path to each other)",
                DisplayNames.Of(caller.User, caller.Name), target.Name, NodeIdentity.ShortId(origin));
        }

        HttpResponseMessage resp;
        try { resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogDebug("relay to {To} failed: {Msg}", target.Name, ex.Message);
            res.StatusCode = 502;
            return;
        }
        using (resp)
        {
            res.StatusCode = (int)resp.StatusCode;
            foreach (var h in ResponseHeaders)
                if (resp.Headers.TryGetValues(h, out var v1) || resp.Content.Headers.TryGetValues(h, out v1))
                    res.Headers[h] = v1.ToArray();
            if (resp.Content.Headers.ContentLength is { } len) res.ContentLength = len;
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            try
            {
                await using var body = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                await body.CopyToAsync(res.Body, 1 << 17, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // the far node went away mid-answer: end abruptly so the requester sees it wasn't complete
                if (!ctx.RequestAborted.IsCancellationRequested) ctx.Abort();
            }
        }
    }
}
