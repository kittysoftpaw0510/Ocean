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
/// it hears (<c>/p/neighbors</c>), and those we can't hear ourselves are listed as reached through it. Every node
/// that offers one is kept as a bridge to it, so requests can spread over them and move when one fails.
/// <para>
/// The answer carries a revision: when nothing changed the node answers 304 with only its current load, so the
/// regular exchange costs a few hundred bytes.
/// </para>
/// <para>Example: A and B share one network, B and C another. A hears B, B tells A about C, A lists C "via B".</para>
/// </summary>
public sealed class MeshService : BackgroundService
{
    private static readonly TimeSpan RetryOldNode = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryOff = TimeSpan.FromMinutes(1);

    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly NodeIdentity _identity;
    private readonly ConfigStore _config;
    private readonly ViaForwarder _forwarder;
    private readonly ILogger<MeshService> _log;
    private readonly ConcurrentDictionary<string, byte> _busy = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _skip = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAsk = new();
    private readonly ConcurrentDictionary<string, string> _lastRev = new();
    private readonly ConcurrentDictionary<string, List<NeighborDto>> _lastNodes = new();

    public MeshService(PeerRegistry registry, PeerClient client, NodeIdentity identity, ConfigStore config, ViaForwarder forwarder, ILogger<MeshService> log)
    {
        _registry = registry; _client = client; _identity = identity; _config = config; _forwarder = forwarder; _log = log;
        _registry.Changed += (p, change) =>
        {
            if (change is PeerChange.Offline or PeerChange.Removed) { _lastRev.TryRemove(p.Id, out _); _lastNodes.TryRemove(p.Id, out _); }
            if (p.Bridged && change is PeerChange.Added or PeerChange.Online)
                _log.LogInformation("peer {State}: {Name} ({User}@{Host}), reached through {Via} (no network path from here)",
                    change == PeerChange.Added ? "online" : "back online", p.Name, p.User, p.Host, p.Via);
        };
    }

    /// <summary>Answers /p/neighbors: the nodes we hear directly (one PC in between at most), or 304 when unchanged.</summary>
    public IResult Serve(HttpContext ctx)
    {
        var dto = Neighbors(PeerContext.Get(ctx).ClientId);
        ctx.Response.Headers[Via.LoadHeader] = dto.Load.ToString();
        ctx.Response.Headers[Via.RevHeader] = dto.Rev;
        if (ctx.Request.Headers[Via.RevHeader].ToString() == dto.Rev) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Json(dto, Json.Options);
    }

    private NeighborsDto Neighbors(string callerId)
    {
        var dto = new NeighborsDto { Bridging = _config.Current.AllowBridging, Load = _forwarder.Active };
        if (dto.Bridging)
            foreach (var p in _registry.Snapshot().OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (!p.Online || p.Id == callerId || p.Bridged || string.IsNullOrEmpty(p.PublicKey)) continue;
                dto.Nodes.Add(new NeighborDto
                {
                    Id = p.Id, Pk = p.PublicKey, Name = p.Name, User = p.User, Host = p.Host,
                    Cat = p.CatalogVersion, Shares = p.ShareCount, Hops = 0,
                });
            }
        var h = Fnv.Add(Fnv.Seed, dto.Bridging ? "on" : "off");
        foreach (var n in dto.Nodes)
        {
            h = Fnv.Add(h, n.Id); h = Fnv.Add(h, n.Pk); h = Fnv.Add(h, n.Name); h = Fnv.Add(h, n.User); h = Fnv.Add(h, n.Host);
            h = Fnv.Add(h, n.Cat); h = Fnv.Add(h, n.Shares);
        }
        dto.Rev = h.ToString("x");
        return dto;
    }

    /// <summary>Each node we hear is asked every 5 s; on a crowded network (many nodes) up to every 10 s.</summary>
    private static TimeSpan PollInterval(int direct) => TimeSpan.FromSeconds(Math.Clamp(direct / 4.0, 5, 10));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var direct = _registry.Snapshot().Where(p => p.Online && !p.Bridged).ToList(); // only nodes we reach ourselves
            var every = PollInterval(direct.Count);
            foreach (var p in direct)
            {
                if (_skip.TryGetValue(p.Id, out var until) && now < until) continue;
                if (now - _lastAsk.GetValueOrDefault(p.Id) < every) continue;
                if (_busy.TryAdd(p.Id, 0)) { _lastAsk[p.Id] = now; _ = AskAsync(p, ct); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task AskAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var req = _client.Build(peer, HttpMethod.Get, "/p/neighbors");
            if (_lastRev.TryGetValue(peer.Id, out var rev)) req.Headers.TryAddWithoutValidation(Via.RevHeader, rev);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                _skip[peer.Id] = DateTimeOffset.UtcNow + RetryOldNode; // an older Ocean: no bridging through it
                return;
            }
            var load = resp.Headers.TryGetValues(Via.LoadHeader, out var l) && int.TryParse(l.FirstOrDefault(), out var n) ? n : 0;
            var realm = Realm.Tag(_config.Current.Secret);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                // the same nodes as last time: confirm them with the bridge's current load
                if (_lastNodes.TryGetValue(peer.Id, out var known))
                    foreach (var k in known) _registry.ConfirmBridge(k, peer, load, realm);
                return;
            }
            if (!resp.IsSuccessStatusCode) return;
            var dto = await resp.Content.ReadFromJsonAsync<NeighborsDto>(Json.Options, timeout.Token);
            if (dto is null) return;
            if (!dto.Bridging)
            {
                if (_skip.TryAdd(peer.Id, DateTimeOffset.UtcNow + RetryOff)) _log.LogInformation("{Peer} doesn't pass traffic between other PCs (switched off there)", peer.Name);
                _skip[peer.Id] = DateTimeOffset.UtcNow + RetryOff;
                Forget(peer);
                return;
            }
            _skip.TryRemove(peer.Id, out _);
            var nodes = dto.Nodes.Take(500)
                .Where(x => x.Id != _identity.NodeId && x.Id != peer.Id && x.Hops == 0 && SameKey(x)) // one PC in between at most
                .ToList();
            // nodes it no longer hears are no longer reached through it
            if (_lastNodes.TryGetValue(peer.Id, out var before))
                foreach (var gone in before.Where(b => nodes.All(x => x.Id != b.Id)))
                    _registry.DropBridge(peer.Id, gone.Id);
            _lastNodes[peer.Id] = nodes;
            _lastRev[peer.Id] = dto.Rev;
            foreach (var x in nodes) _registry.ConfirmBridge(x, peer, dto.Load, realm);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _log.LogDebug("neighbours of {Peer}: {Msg}", peer.Name, ex.Message);
        }
        finally { _busy.TryRemove(peer.Id, out _); }
    }

    private void Forget(Peer bridge)
    {
        _lastNodes.TryRemove(bridge.Id, out _);
        _lastRev.TryRemove(bridge.Id, out _);
        _registry.DropBridge(bridge.Id);
    }

    /// <summary>The id must be the hash of the key it comes with (the key is what TLS and signatures are checked against).</summary>
    private static bool SameKey(NeighborDto n)
    {
        try { return n.Id.Length == 64 && NodeIdentity.ComputeNodeId(Convert.FromBase64String(n.Pk)) == n.Id; }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Bridge side of <see cref="Via"/>: carries a request from a node that can't reach the target (but reaches us) to
/// the target, and streams the answer back. The request keeps its origin's names, grants and signature; we only add
/// our own proof of the shared secret on the way. What goes out is paced like our own uploads (viewers before
/// downloads, the upload limit, a few downloads at a time), and a public video is served from our own copy or the
/// stream cache so the owner sends each part once however many nodes watch it through here.
/// </summary>
public sealed class ViaForwarder
{
    private static readonly string[] RequestHeaders = { "Range", "If-Range", "If-None-Match", "If-Modified-Since" };
    private static readonly string[] ResponseHeaders = { "Content-Type", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Retry-After", "Cache-Control" };

    private readonly PeerRegistry _registry;
    private readonly PeerClient _client;
    private readonly PeerService _peers;
    private readonly PeerMedia _media;
    private readonly UploadScheduler _uploads;
    private readonly ConfigStore _config;
    private readonly NodeIdentity _identity;
    private readonly ILogger<ViaForwarder> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _announced = new();
    private int _active;

    public ViaForwarder(PeerRegistry registry, PeerClient client, PeerService peers, PeerMedia media, UploadScheduler uploads,
        ConfigStore config, NodeIdentity identity, ILogger<ViaForwarder> log)
    {
        _registry = registry; _client = client; _peers = peers; _media = media; _uploads = uploads;
        _config = config; _identity = identity; _log = log;
    }

    /// <summary>Transfers being passed on right now (reported to neighbours, who pick the least busy bridge).</summary>
    public int Active => Volatile.Read(ref _active);

    public static string RawTarget(HttpContext ctx)
        => ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw ? raw : ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString;

    public async Task ServeAsync(HttpContext ctx)
    {
        var res = ctx.Response;
        if (!Via.TryParse(RawTarget(ctx), out var targetId, out var inner) || !Via.IsRelayable(inner)) { res.StatusCode = 400; return; }
        if (!_config.Current.AllowBridging)
        {
            await FailAsync(ctx, 403, "off", "This PC doesn't pass traffic between other PCs (switched off in its Settings).");
            return;
        }
        _ = int.TryParse(ctx.Request.Headers[Via.HopsHeader], out var hops);
        if (hops >= Via.MaxHops || targetId == _identity.NodeId)
        {
            await FailAsync(ctx, StatusCodes.Status508LoopDetected, "loop", "A request can pass through one PC only.");
            return;
        }
        // only to a PC we hear ourselves: never a second PC in between
        if (!_registry.TryGetOnline(targetId, out var target) || target.Bridged)
        {
            await FailAsync(ctx, 503, "unreachable", "That PC can't be reached from here right now.");
            return;
        }

        var caller = PeerContext.Get(ctx);
        var origin = ctx.Request.Headers[Via.OriginHeader].ToString().ToLowerInvariant();
        var isStream = inner.StartsWith("/p/stream/", StringComparison.Ordinal) && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method));
        var download = string.Equals(ctx.Request.Headers[PeerHeaders.Purpose], PeerHeaders.Download, StringComparison.OrdinalIgnoreCase);
        var kind = download ? UploadKind.Download : UploadKind.Stream;
        IDisposable? slot = null;
        if (isStream && download && HttpMethods.IsGet(ctx.Request.Method))
        {
            // a few downloads at a time through here, as for our own videos; the origin then tries another bridge
            slot = _uploads.TryStartDownload(origin.Length == 64 ? origin : caller.ClientId);
            if (slot is null)
            {
                res.Headers.RetryAfter = "15";
                await FailAsync(ctx, 503, PeerHeaders.Busy, "Busy passing on other downloads; try again shortly.");
                return;
            }
        }

        Interlocked.Increment(ref _active);
        try
        {
            var key = origin + ">" + targetId;
            if (!_announced.TryGetValue(key, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(10))
            {
                _announced[key] = DateTimeOffset.UtcNow;
                _log.LogInformation("passing traffic on from {From} to {To} (they have no network path to each other)",
                    DisplayNames.Of(caller.User, caller.Name), target.Name);
            }

            // A public video of a node we hear: from our own copy or the stream cache (a locked one goes to the owner
            // with the origin's signature, so the owner decides with the origin's grants).
            if (isStream && ItemIdOf(inner) is { } itemId && _peers.FindItem(targetId, itemId) is { Lock: null, Size: > 0 } item)
            {
                await _media.ServeForBridgeAsync(ctx, targetId, item, kind, slot);
                slot = null;
                return;
            }
            await PassThroughAsync(ctx, target, inner, hops, isStream ? kind : null);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            slot?.Dispose();
        }
    }

    /// <summary>Carries the request to the target as is and streams the answer back (paced when it is a video).</summary>
    private async Task PassThroughAsync(HttpContext ctx, Peer target, string inner, int hops, UploadKind? pace)
    {
        var res = ctx.Response;
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

        HttpResponseMessage resp;
        try { resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogDebug("passing on to {To} failed: {Msg}", target.Name, ex.Message);
            await FailAsync(ctx, 502, "failed", $"{target.Name} did not answer.");
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
                Stream dest = pace is { } kind ? new PacedStream(res.Body, _uploads, kind) : res.Body;
                await body.CopyToAsync(dest, 1 << 17, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // the far node went away mid-answer: end abruptly so the requester sees it wasn't complete
                if (!ctx.RequestAborted.IsCancellationRequested) ctx.Abort();
            }
        }
    }

    /// <summary>Our own failure (not the target's answer), marked so the origin tries another bridge right away.</summary>
    private static async Task FailAsync(HttpContext ctx, int status, string fault, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers[Via.ErrorHeader] = fault;
        await ctx.Response.WriteAsJsonAsync(new ErrorDto { Error = fault, Message = message }, Json.Options);
    }

    private static string? ItemIdOf(string inner)
    {
        var rest = inner["/p/stream/".Length..];
        var end = rest.IndexOfAny(new[] { '/', '?' });
        if (end >= 0) rest = rest[..end];
        return rest.Length == 0 ? null : Uri.UnescapeDataString(rest);
    }
}
