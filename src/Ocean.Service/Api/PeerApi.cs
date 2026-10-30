using Microsoft.Net.Http.Headers;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Media;
using Ocean.Core.Protocol;
using Ocean.Service.Access;
using Ocean.Service.Catalog;
using Ocean.Service.Net;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Api;

/// <summary>Identity of the calling node, derived from its TLS client certificate by the gate middleware.</summary>
public sealed class PeerContext
{
    public string ClientId { get; init; } = "";
    public string Name { get; init; } = "";
    public string User { get; init; } = "";
    public string Host { get; init; } = "";
    public IReadOnlyDictionary<string, string> Grants { get; init; } = new Dictionary<string, string>();

    public static PeerContext Get(HttpContext ctx) => (PeerContext)ctx.Items["ocean.peer"]!;

    public static PeerContext FromRequest(HttpContext ctx, string clientId)
    {
        var grants = new Dictionary<string, string>();
        var raw = ctx.Request.Headers["X-Ocean-Grants"].ToString();
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) grants[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return new PeerContext
        {
            ClientId = clientId,
            Name = PeerClient.FromAscii(ctx.Request.Headers["X-Ocean-Name"]),
            User = PeerClient.FromAscii(ctx.Request.Headers["X-Ocean-User"]),
            Host = PeerClient.FromAscii(ctx.Request.Headers["X-Ocean-Host"]),
            Grants = grants,
        };
    }
}

public static class PeerApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/p");

        g.MapGet("/ping", (ConfigStore config, NodeIdentity id, CatalogService catalog) =>
            Results.Json(new PingDto { NodeId = id.NodeId, NodeName = config.Current.NodeName, UserName = config.Current.UserName, CatalogVersion = catalog.Version }, Json.Options));

        g.MapGet("/catalog", (HttpContext ctx, ConfigStore config, NodeIdentity id, CatalogService catalog, AccessService access) =>
        {
            var pc = PeerContext.Get(ctx);
            var check = access.Checker(pc.ClientId, pc.Grants);
            var cfg = config.Current;
            var cat = catalog.BuildCatalog(id.NodeId, cfg.NodeName, cfg.UserName, cfg.SharingPaused, check);
            // ETag depends on the version and on which locks this caller has open.
            var open = string.Join(',', cat.Shares.Where(s => s.Unlocked).Select(s => s.Id).Concat(cat.Shares.SelectMany(s => s.Items).Where(i => i.Lock is not null && i.Unlocked).Select(i => i.Id)).Order());
            var etag = $"\"{cat.Version}-{open.GetHashCode():x}\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Json(cat, Json.Options);
        });

        g.MapPost("/access", async (HttpContext ctx, AccessService access) =>
        {
            var pc = PeerContext.Get(ctx);
            var req = await ctx.Request.ReadFromJsonAsync<AccessRequestDto>(Json.Options, ctx.RequestAborted);
            if (req is null || string.IsNullOrEmpty(req.LockId)) return Results.Json(new AccessResponseDto { Status = AccessStatus.Invalid }, Json.Options, statusCode: 400);
            return Results.Json(access.Request(pc.ClientId, pc.Name, pc.User, pc.Host, req), Json.Options);
        });

        g.MapGet("/access/{requestId}", (HttpContext ctx, string requestId, AccessService access) =>
            Results.Json(access.Poll(requestId, PeerContext.Get(ctx).ClientId), Json.Options));

        g.MapGet("/thumb/{itemId}", async (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, ThumbnailService thumbs, ConfigStore config) =>
        {
            if (config.Current.SharingPaused) return Results.StatusCode(503);
            if (!catalog.TryGetItem(itemId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            var path = await thumbs.GetOrCreateAsync(itemId, ctx.RequestAborted);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/jpeg", lastModified: item.ModifiedAt, entityTag: new EntityTagHeaderValue($"\"{item.Id}-{item.ModifiedAt.UtcTicks}\""));
        });

        g.MapMethods("/stream/{itemId}", new[] { "GET", "HEAD" }, (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, BandwidthLimiter limiter, ConfigStore config) =>
        {
            if (config.Current.SharingPaused) return Results.StatusCode(503);
            if (!catalog.TryGetItem(itemId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            var full = catalog.FullPath(share, item);
            FileStream fs;
            try
            {
                fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (DirectoryNotFoundException) { return Results.NotFound(); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            Stream body = limiter.Enabled ? new ThrottledStream(fs, limiter) : fs;
            ctx.Response.Headers.CacheControl = "private, no-transform";
            return Results.Stream(body, VideoTypes.ContentType(full), lastModified: item.ModifiedAt,
                entityTag: new EntityTagHeaderValue($"\"{item.Id}-{item.ModifiedAt.UtcTicks}\""), enableRangeProcessing: true);
        });
    }

    private static IResult Locked(ShareDefinition share, ItemRecord item)
    {
        LockInfo? l = null;
        if (share.Mode != AccessMode.Public) l = new LockInfo { Id = CatalogService.ShareLock(share.Id), Mode = share.Mode };
        else if (share.Overrides.TryGetValue(item.Id, out var ov) && ov.Mode is ItemAccessMode.Password or ItemAccessMode.Ask)
            l = new LockInfo { Id = CatalogService.ItemLock(item.Id), Mode = ov.Mode == ItemAccessMode.Password ? AccessMode.Password : AccessMode.Ask };
        return Results.Json(new ErrorDto { Error = "locked", Message = "This item is locked", Lock = l }, Json.Options, statusCode: 403);
    }
}
