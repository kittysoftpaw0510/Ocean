using Microsoft.Net.Http.Headers;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Media;
using Ocean.Core.Protocol;
using Ocean.Service.Access;
using Ocean.Service.Cast;
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
            // ETag = version + what this caller can see (which locks it has open). Stable across restarts.
            var open = string.Join(',', cat.Shares.Where(s => s.Unlocked).Select(s => s.Id).Concat(cat.Shares.SelectMany(s => s.Items).Where(i => i.Lock is not null && i.Unlocked).Select(i => i.Id)).Order());
            var view = Fnv.Hash(open + (cfg.SharingPaused ? "|paused" : ""));
            var etag = CatalogTags.Make(cat.Version, view);
            ctx.Response.Headers.CacheControl = "no-cache";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                ctx.Response.Headers.ETag = etag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            // Delta: the caller holds an older version with the same view, so send only what changed since then.
            if (CatalogTags.TryParse(ctx.Request.Headers[CatalogTags.BaseHeader].ToString(), out var baseVersion, out var baseView)
                && baseView == view && catalog.ChangesSince(baseVersion) is { } changes)
            {
                if (changes.Version == baseVersion)
                {
                    ctx.Response.Headers.ETag = CatalogTags.Make(baseVersion, view);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                var total = cat.Shares.Sum(s => s.Items.Count);
                var delta = new CatalogDto
                {
                    NodeId = cat.NodeId, NodeName = cat.NodeName, UserName = cat.UserName, Host = cat.Host,
                    AppVersion = cat.AppVersion, Os = cat.Os,
                    Version = changes.Version, GeneratedAt = cat.GeneratedAt, DeltaFrom = baseVersion,
                    RemovedItems = changes.Removed.ToList(), Cast = cat.Cast, CastPlan = cat.CastPlan,
                };
                foreach (var s in cat.Shares)
                    delta.Shares.Add(new ShareDto
                    {
                        Id = s.Id, Name = s.Name, Mode = s.Mode, Lock = s.Lock, Unlocked = s.Unlocked, ItemCount = s.ItemCount,
                        TotalBytes = s.TotalBytes, CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt, NewestItemAt = s.NewestItemAt,
                        Folders = s.Folders, Items = s.Items.Where(i => changes.Changed.Contains(i.Id)).ToList(),
                    });
                // A delta carrying most of the catalog anyway is no better than the catalog itself.
                if (delta.Shares.Sum(s => s.Items.Count) <= total / 2 + 16)
                {
                    ctx.Response.Headers.ETag = CatalogTags.Make(changes.Version, view);
                    return Results.Json(delta, Json.Options);
                }
            }
            ctx.Response.Headers.ETag = etag;
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

        // ---- reverse connection (the caller is a node we could not reach directly) ----
        g.MapPost("/relay/poll", async (HttpContext ctx, RelayHub hub) =>
        {
            var hold = int.TryParse(ctx.Request.Headers[RelayHub.HoldHeader].ToString(), out var h) ? h : 25;
            var job = await hub.PollAsync(PeerContext.Get(ctx).ClientId, ctx.RequestAborted, hold);
            return job is null ? Results.NoContent() : Results.Json(job, Json.Options);
        });

        g.MapPost("/relay/response/{jobId}", async (HttpContext ctx, string jobId, RelayHub hub) =>
        {
            // the answer's body can be a whole video: no request size limit for this upload
            var size = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = null;
            if (!int.TryParse(ctx.Request.Headers["X-Relay-Status"], out var status)) return Results.BadRequest();
            Dictionary<string, string> headers;
            try { headers = Json.Deserialize<Dictionary<string, string>>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers["X-Relay-Headers"].ToString()))) ?? new(); }
            catch { headers = new(); }
            var ok = await hub.CompleteAsync(PeerContext.Get(ctx).ClientId, jobId, status, headers, ctx.Request.Body, ctx.RequestAborted);
            return ok ? Results.Ok() : Results.NotFound();
        });

        // ---- relaying between nodes that have no network path to each other (see Via) ----
        g.MapGet("/neighbors", (HttpContext ctx, MeshService mesh) => mesh.Serve(ctx));

        g.MapMethods("/via/{target}/{**rest}", new[] { "GET", "HEAD", "POST" }, (HttpContext ctx, ViaForwarder via) => via.ServeAsync(ctx));

        // ---- votes (see Ratings): one per calling PC, under the video's own access rule ----
        g.MapPost("/rate/{itemId}", async (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, Social.Ratings ratings) =>
        {
            if (!catalog.TryGetItem(itemId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            var rate = await ctx.Request.ReadFromJsonAsync<RateDto>(Json.Options, ctx.RequestAborted) ?? new RateDto();
            if (rate.Vote is { } v && v is < -1 or > 1) return Results.BadRequest();
            return Results.Json(ratings.Rate(itemId, pc.ClientId, rate), Json.Options);
        });

        // ---- notes (see Social.Notes): read, write, vote, under the video's own access rule ----
        // The writer is the calling PC (its node id and name come from its certificate and announcements). Writes are
        // POSTs only: requests through a bridge (/p/via) or a reverse link pass GET, HEAD and POST.
        g.MapGet("/notes/{itemId}", (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, Social.Notes notes) =>
            NoteAccess(ctx, itemId, catalog, access, out var pc) ?? Results.Json(notes.List(itemId, pc.ClientId, callerIsOwner: false), Json.Options));
        g.MapPost("/notes/{itemId}", async (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, Social.Notes notes) =>
        {
            if (NoteAccess(ctx, itemId, catalog, access, out var pc) is { } refused) return refused;
            var write = await ctx.Request.ReadFromJsonAsync<NoteWriteDto>(Json.Options, ctx.RequestAborted);
            if (write is null) return Results.BadRequest();
            var name = DisplayNames.Of(pc.User, pc.Name);
            return Results.Json(notes.Add(itemId, pc.ClientId, name, pc.Host, write, callerIsOwner: false, notifyOwner: true), Json.Options);
        });
        g.MapPost("/notes/{itemId}/{noteId}/edit", async (HttpContext ctx, string itemId, string noteId, CatalogService catalog, AccessService access, Social.Notes notes) =>
        {
            if (NoteAccess(ctx, itemId, catalog, access, out var pc) is { } refused) return refused;
            var write = await ctx.Request.ReadFromJsonAsync<NoteWriteDto>(Json.Options, ctx.RequestAborted);
            if (write is null) return Results.BadRequest();
            return notes.Edit(itemId, noteId, pc.ClientId, write, callerIsOwner: false) is { } r ? Results.Json(r, Json.Options) : NoteGone();
        });
        g.MapPost("/notes/{itemId}/{noteId}/delete", (HttpContext ctx, string itemId, string noteId, CatalogService catalog, AccessService access, Social.Notes notes) =>
            NoteAccess(ctx, itemId, catalog, access, out var pc)
            ?? (notes.Delete(itemId, noteId, pc.ClientId, callerIsOwner: false) is { } r ? Results.Json(r, Json.Options) : NoteGone()));
        g.MapPost("/notes/{itemId}/{noteId}/vote", async (HttpContext ctx, string itemId, string noteId, CatalogService catalog, AccessService access, Social.Notes notes) =>
        {
            if (NoteAccess(ctx, itemId, catalog, access, out var pc) is { } refused) return refused;
            var vote = await ctx.Request.ReadFromJsonAsync<NoteVoteDto>(Json.Options, ctx.RequestAborted);
            if (vote is null || vote.Vote is < -1 or > 1) return Results.BadRequest();
            return notes.Vote(itemId, noteId, pc.ClientId, vote.Vote, callerIsOwner: false) is { } r ? Results.Json(r, Json.Options) : NoteGone();
        });

        // ---- multicast (see CastHost): join under the video's own access rule, report in, leave ----
        g.MapPost("/cast/{sessionId}/join", (HttpContext ctx, string sessionId, CastHost cast, CatalogService catalog, AccessService access) =>
        {
            var itemId = cast.ItemIdOf(sessionId);
            if (itemId is null || !catalog.TryGetItem(itemId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            var join = cast.Join(sessionId, pc.ClientId, DisplayNames.Of(pc.User, pc.Name));
            return join is null ? Results.NotFound() : Results.Json(join, Json.Options);
        });
        g.MapPost("/cast/{sessionId}/beat", async (HttpContext ctx, string sessionId, CastHost cast) =>
        {
            var beat = await ctx.Request.ReadFromJsonAsync<CastBeatDto>(Json.Options, ctx.RequestAborted) ?? new CastBeatDto();
            var state = cast.Beat(sessionId, PeerContext.Get(ctx).ClientId, beat);
            return state is null ? Results.NotFound() : Results.Json(state, Json.Options);
        });
        g.MapPost("/cast/{sessionId}/leave", (HttpContext ctx, string sessionId, CastHost cast) => { cast.Leave(sessionId, PeerContext.Get(ctx).ClientId); return Results.Ok(); });
        g.MapGet("/cast/{sessionId}/state", (string sessionId, CastHost cast) => Results.Json(cast.State(sessionId), Json.Options));

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

        // ---- lighter copies for watching (see LightCopies): how one is doing, or (POST) "I'm watching this, make one" ----
        g.MapMethods("/light/{itemId}", new[] { "GET", "POST" }, (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, Media.LightCopies light) =>
        {
            if (!catalog.TryGetItem(itemId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            return Results.Json(light.Request(itemId, enqueue: HttpMethods.IsPost(ctx.Request.Method)), Json.Options);
        });

        g.MapMethods("/stream/{itemId}", new[] { "GET", "HEAD" }, (HttpContext ctx, string itemId, CatalogService catalog, AccessService access, UploadScheduler uploads, ConfigStore config, Media.LightCopies light, ViewerTracker viewers) =>
        {
            if (config.Current.SharingPaused) return Results.StatusCode(503);
            // "{id}@light": the lighter copy instead of the original (same locks)
            var wantLight = LightVariant.Split(itemId, out var baseId);
            if (!catalog.TryGetItem(baseId, out var share, out var item)) return Results.NotFound();
            var pc = PeerContext.Get(ctx);
            if (!catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants))) return Locked(share, item);
            var full = wantLight ? light.PathFor(baseId, item) : catalog.FullPath(share, item);
            if (full is null) return Results.NotFound();
            if (HttpMethods.IsGet(ctx.Request.Method)) ctx.Response.RegisterForDispose(viewers.Track(baseId, pc.ClientId)); // "3 watching" on my side
            // Saving a copy can wait; watching can't. Downloads beyond a few at once are asked to come back shortly.
            var kind = string.Equals(ctx.Request.Headers[PeerHeaders.Purpose], PeerHeaders.Download, StringComparison.OrdinalIgnoreCase)
                ? UploadKind.Download : UploadKind.Stream;
            IDisposable? slot = null;
            if (kind == UploadKind.Download && HttpMethods.IsGet(ctx.Request.Method))
            {
                slot = uploads.TryStartDownload(pc.ClientId);
                if (slot is null)
                {
                    ctx.Response.Headers.RetryAfter = "15";
                    return Results.Json(new ErrorDto { Error = PeerHeaders.Busy, Message = "Busy sending other downloads; try again shortly." }, Json.Options, statusCode: 503);
                }
            }
            FileStream fs;
            try
            {
                fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { slot?.Dispose(); return Results.NotFound(); }
            catch (DirectoryNotFoundException) { slot?.Dispose(); return Results.NotFound(); }
            catch (UnauthorizedAccessException) { slot?.Dispose(); return Results.StatusCode(403); }
            catch { slot?.Dispose(); throw; }
            // always paced and metered: tells the scheduler who is watching and how fast this PC uploads
            var body = new ThrottledStream(fs, uploads, kind, slot);
            ctx.Response.Headers.CacheControl = "private, no-transform";
            return Results.Stream(body, VideoTypes.ContentType(full), lastModified: item.ModifiedAt,
                entityTag: new EntityTagHeaderValue($"\"{item.Id}-{item.ModifiedAt.UtcTicks}\""), enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// Null when the calling PC may see the notes of this video (the video's own access rule); otherwise the answer:
    /// "gone" (not shared here any more; 404 with a body, which an Ocean without notes doesn't send) or locked.
    /// </summary>
    private static IResult? NoteAccess(HttpContext ctx, string itemId, CatalogService catalog, AccessService access, out PeerContext pc)
    {
        pc = PeerContext.Get(ctx);
        if (!catalog.TryGetItem(itemId, out var share, out var item)) return NoteGone();
        return catalog.IsItemAccessible(share, item, access.Checker(pc.ClientId, pc.Grants)) ? null : Locked(share, item);
    }

    private static IResult NoteGone() => Results.Json(new ErrorDto { Error = "gone", Message = "This video or note is no longer here" }, Json.Options, statusCode: 404);

    private static IResult Locked(ShareDefinition share, ItemRecord item)
    {
        LockInfo? l = null;
        if (share.Mode != AccessMode.Public) l = new LockInfo { Id = CatalogService.ShareLock(share.Id), Mode = share.Mode };
        else if (share.Overrides.TryGetValue(item.Id, out var ov) && ov.Mode is ItemAccessMode.Password or ItemAccessMode.Ask)
            l = new LockInfo { Id = CatalogService.ItemLock(item.Id), Mode = ov.Mode == ItemAccessMode.Password ? AccessMode.Password : AccessMode.Ask };
        return Results.Json(new ErrorDto { Error = "locked", Message = "This item is locked", Lock = l }, Json.Options, statusCode: 403);
    }
}
