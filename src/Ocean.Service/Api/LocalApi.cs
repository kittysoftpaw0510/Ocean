using System.Net;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Access;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Downloads;
using Ocean.Service.Net;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Api;

public sealed record HostInfo(bool RunningAsService);

/// <summary>Loopback API used by the Ocean UI.</summary>
public static class LocalApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        // ---------------------------------------------------------------- status / events
        g.MapGet("/status", (ConfigStore config, NodeIdentity id, CatalogService catalog, HostInfo host) =>
        {
            var c = config.Current;
            return Results.Json(new NodeStatusDto
            {
                NodeId = id.NodeId, NodeName = c.NodeName, UserName = c.UserName, Host = Environment.MachineName,
                Version = OceanDefaults.AppVersion, RealmSet = Realm.Normalize(c.Secret).Length > 0, SharingPaused = c.SharingPaused,
                RunningAsService = host.RunningAsService, DataDir = OceanDefaults.DataDir, PeerPort = c.PeerPort, NewDays = c.NewDays,
                CatalogVersion = catalog.Version,
            }, Json.Options);
        });

        g.MapGet("/events", async (HttpContext ctx, EventHub hub) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            try
            {
                await foreach (var frame in hub.Subscribe(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
        });

        // ---------------------------------------------------------------- peers
        g.MapGet("/peers", (PeerService peers) => Results.Json(peers.Peers(), Json.Options));

        // Forget an offline node now (its snapshot is deleted). An online node would reappear at once, so refuse.
        g.MapDelete("/peers/{peerId}", (string peerId, PeerRegistry registry) =>
        {
            if (!registry.TryGet(peerId, out var p)) return Results.NotFound();
            if (p.Online) return Results.Json(new ErrorDto { Error = "online", Message = "This node is online; it can be removed once it goes offline." }, Json.Options, statusCode: 409);
            registry.Remove(peerId);
            return Results.Ok();
        });

        // Manual refresh: say hello now (others answer right away) and re-check every online node's shares.
        g.MapPost("/peers/refresh", (PeerService peers, IEnumerable<IHostedService> hosted) =>
        {
            hosted.OfType<DiscoveryService>().FirstOrDefault()?.AnnounceNow();
            peers.RefreshAll();
            return Results.Ok();
        });

        g.MapGet("/peers/{peerId}/catalog", async (string peerId, bool? refresh, PeerService peers, HttpContext ctx) =>
        {
            var cat = await peers.GetCatalogAsync(peerId, refresh == true, ctx.RequestAborted);
            // The node is announced (so it is on the network) but we could not connect to it.
            return cat is null ? Results.Json(new ErrorDto { Error = "unreachable", Message = "Couldn't connect to this PC." }, Json.Options, statusCode: 503)
                               : Results.Json(cat, Json.Options);
        });

        g.MapPost("/peers/{peerId}/visited", (string peerId, PeerService peers) => { peers.MarkVisited(peerId); return Results.Ok(); });

        g.MapPost("/peers/{peerId}/unlock", async (string peerId, HttpContext ctx, PeerService peers) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UnlockRequestDto>(Json.Options, ctx.RequestAborted);
            if (req is null || string.IsNullOrEmpty(req.LockId)) return Results.BadRequest();
            try { return Results.Json(await peers.UnlockAsync(peerId, req.LockId, req.Password, ctx.RequestAborted), Json.Options); }
            catch (PeerUnavailableException ex) { return Results.Json(new AccessResponseDto { Status = AccessStatus.Error, Message = ex.Message }, Json.Options); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { return Results.Json(new AccessResponseDto { Status = AccessStatus.Error, Message = "Could not reach the peer" }, Json.Options); }
        });

        g.MapGet("/peers/{peerId}/unlock/{requestId}", async (string peerId, string requestId, HttpContext ctx, PeerService peers) =>
        {
            try { return Results.Json(await peers.PollUnlockAsync(peerId, requestId, ctx.RequestAborted), Json.Options); }
            catch (PeerUnavailableException ex) { return Results.Json(new AccessResponseDto { Status = AccessStatus.Error, Message = ex.Message }, Json.Options); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { return Results.Json(new AccessResponseDto { Status = AccessStatus.Error, Message = "Could not reach the peer" }, Json.Options); }
        });

        g.MapGet("/peers/{peerId}/thumb/{itemId}", async (string peerId, string itemId, HttpContext ctx, PeerService peers) =>
        {
            var path = await peers.GetThumbPathAsync(peerId, itemId, ctx.RequestAborted);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, "image/jpeg");
        });

        g.MapMethods("/peers/{peerId}/stream/{itemId}/{name?}", new[] { "GET", "HEAD" }, async (string peerId, string itemId, HttpContext ctx, PeerService peers) =>
        {
            var range = ctx.Request.Headers.Range.ToString();
            HttpResponseMessage resp;
            try { resp = await peers.OpenStreamAsync(peerId, itemId, string.IsNullOrEmpty(range) ? null : range, ctx.RequestAborted); }
            catch (PeerUnavailableException) { ctx.Response.StatusCode = 503; return; }
            catch (HttpRequestException) { ctx.Response.StatusCode = 502; return; }
            catch (OperationCanceledException) { return; }
            using (resp)
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;
                if (resp.Content.Headers.ContentType is { } ctype) ctx.Response.ContentType = ctype.ToString();
                if (resp.Content.Headers.ContentLength is { } len) ctx.Response.ContentLength = len;
                if (resp.Content.Headers.ContentRange is { } cr) ctx.Response.Headers.ContentRange = cr.ToString();
                if (resp.Headers.ETag is { } etag) ctx.Response.Headers.ETag = etag.ToString();
                if (resp.Content.Headers.LastModified is { } lm) ctx.Response.Headers.LastModified = lm.ToString("R");
                ctx.Response.Headers.AcceptRanges = "bytes";
                ctx.Response.Headers.CacheControl = "no-store";
                if (HttpMethods.IsHead(ctx.Request.Method)) return;
                try
                {
                    await using var src = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                    await src.CopyToAsync(ctx.Response.Body, 1 << 17, ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        });

        // ---------------------------------------------------------------- my shares
        g.MapGet("/shares", (CatalogService catalog) => Results.Json(catalog.Shares.Select(catalog.ToDto).ToList(), Json.Options));

        g.MapPost("/shares", async (HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<ShareUpsertDto>(Json.Options, ctx.RequestAborted);
            // A share may start empty (a named folder you add videos to later), but then it needs a name.
            if (dto is null || (dto.Roots.Count == 0 && string.IsNullOrWhiteSpace(dto.Name)))
                return Results.Json(new ErrorDto { Error = "invalid", Message = "Give the folder a name, or pick a folder or files to share" }, Json.Options, statusCode: 400);
            if (dto.Mode == AccessMode.Password && dto.Id is null && string.IsNullOrEmpty(dto.Password))
                return Results.Json(new ErrorDto { Error = "invalid", Message = "A password is required for password-protected shares" }, Json.Options, statusCode: 400);
            return Results.Json(catalog.Upsert(dto), Json.Options);
        });

        g.MapDelete("/shares/{id}", (string id, CatalogService catalog) => catalog.Remove(id) ? Results.Ok() : Results.NotFound());

        g.MapPost("/shares/{id}/rescan", (string id, CatalogService catalog) => { _ = catalog.RescanAsync(id); return Results.Accepted(); });

        g.MapGet("/shares/{id}/items", (string id, CatalogService catalog) => Results.Json(catalog.LocalItems(id), Json.Options));

        g.MapPut("/shares/{id}/items/{itemId}/policy", async (string id, string itemId, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<ItemOverrideUpsertDto>(Json.Options, ctx.RequestAborted);
            if (dto is null) return Results.BadRequest();
            return catalog.SetOverride(id, itemId, dto) ? Results.Ok() : Results.NotFound();
        });

        g.MapPost("/shares/{id}/add", async (string id, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<AddToShareDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || dto.Paths.Count == 0) return Results.BadRequest();
            return catalog.AddRoots(id, dto.Paths, dto.Folder)
                ? Results.Ok()
                : Results.Json(new ErrorDto { Error = "invalid", Message = "Nothing to add: pick video files or folders." }, Json.Options, statusCode: 400);
        });

        // ---- organizing a share into folders (virtual: files on disk are never moved) ----
        g.MapPost("/shares/{id}/move", async (string id, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<MoveItemsDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || dto.ItemIds.Count == 0) return Results.BadRequest();
            return catalog.MoveItems(id, dto.ItemIds, dto.Folder) ? Results.Ok() : Results.NotFound();
        });

        g.MapPost("/shares/{id}/folders", async (string id, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<FolderOpDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Path)) return Results.BadRequest();
            return catalog.CreateFolder(id, dto.Path) ? Results.Ok() : Results.NotFound();
        });

        g.MapPost("/shares/{id}/folders/rename", async (string id, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<FolderOpDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Path) || string.IsNullOrWhiteSpace(dto.To)) return Results.BadRequest();
            return catalog.RenameFolder(id, dto.Path, dto.To)
                ? Results.Ok()
                : Results.Json(new ErrorDto { Error = "invalid", Message = "That name is not possible here (a folder cannot be moved into itself)." }, Json.Options, statusCode: 400);
        });

        g.MapPost("/shares/{id}/folders/delete", async (string id, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<FolderOpDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Path)) return Results.BadRequest();
            return catalog.DeleteFolder(id, dto.Path) ? Results.Ok() : Results.NotFound();
        });

        g.MapGet("/shares/thumb/{itemId}", async (string itemId, HttpContext ctx, ThumbnailService thumbs) =>
        {
            var path = await thumbs.GetOrCreateAsync(itemId, ctx.RequestAborted);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, "image/jpeg");
        });

        // ---------------------------------------------------------------- access requests & grants
        g.MapGet("/access/pending", (AccessService access) => Results.Json(access.Pending(), Json.Options));

        g.MapPost("/access/{requestId}/decide", async (string requestId, HttpContext ctx, AccessService access) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<DecisionDto>(Json.Options, ctx.RequestAborted);
            if (dto is null || dto.Decision is not ("once" or "always" or "deny")) return Results.BadRequest();
            return access.Decide(requestId, dto.Decision) ? Results.Ok() : Results.NotFound();
        });

        g.MapGet("/access/grants", (AccessService access) => Results.Json(access.Grants(), Json.Options));

        g.MapDelete("/access/grants/{id}", (string id, AccessService access) => access.Revoke(id) ? Results.Ok() : Results.NotFound());

        // ---------------------------------------------------------------- downloads
        g.MapGet("/downloads", (DownloadManager dl) => Results.Json(dl.List(), Json.Options));

        g.MapPost("/downloads", async (HttpContext ctx, DownloadManager dl) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<DownloadRequestDto>(Json.Options, ctx.RequestAborted);
            if (req is null || string.IsNullOrEmpty(req.PeerId)) return Results.BadRequest();
            try { return Results.Json(await dl.EnqueueAsync(req, ctx.RequestAborted), Json.Options); }
            catch (PeerUnavailableException ex) { return Results.Json(new ErrorDto { Error = "unavailable", Message = ex.Message }, Json.Options, statusCode: 503); }
        });

        g.MapPost("/downloads/clear", (DownloadManager dl) => Results.Json(new { removed = dl.ClearCompleted() }));
        g.MapPost("/downloads/{id}/cancel", (string id, DownloadManager dl) => dl.Cancel(id) ? Results.Ok() : Results.NotFound());
        g.MapPost("/downloads/{id}/retry", (string id, DownloadManager dl) => dl.Retry(id) ? Results.Ok() : Results.NotFound());
        g.MapDelete("/downloads/{id}", (string id, DownloadManager dl) => dl.Remove(id) ? Results.Ok() : Results.NotFound());

        // ---------------------------------------------------------------- settings
        g.MapGet("/settings", (ConfigStore config) =>
        {
            var c = config.Current;
            return Results.Json(new SettingsDto
            {
                NodeName = c.NodeName, UserName = c.UserName, Secret = c.Secret, DownloadDir = c.DownloadDir,
                UploadLimitKBps = c.UploadLimitKBps, SharingPaused = c.SharingPaused, NewDays = c.NewDays,
                MaxConcurrentDownloads = c.MaxConcurrentDownloads, NotifyNewVideos = c.NotifyNewVideos, OfflineKeepHours = c.OfflineKeepHours,
            }, Json.Options);
        });

        g.MapPut("/settings", async (HttpContext ctx, ConfigStore config, BandwidthLimiter limiter, EventHub events) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<SettingsDto>(Json.Options, ctx.RequestAborted);
            if (dto is null) return Results.BadRequest();
            config.Update(c =>
            {
                if (!string.IsNullOrWhiteSpace(dto.NodeName)) c.NodeName = dto.NodeName.Trim();
                if (!string.IsNullOrWhiteSpace(dto.UserName)) c.UserName = dto.UserName.Trim();
                if (dto.Secret is not null) c.Secret = Realm.Normalize(dto.Secret);
                if (!string.IsNullOrWhiteSpace(dto.DownloadDir)) c.DownloadDir = dto.DownloadDir.Trim();
                c.UploadLimitKBps = Math.Max(0, dto.UploadLimitKBps);
                c.SharingPaused = dto.SharingPaused;
                c.NewDays = Math.Clamp(dto.NewDays, 1, 90);
                c.MaxConcurrentDownloads = Math.Clamp(dto.MaxConcurrentDownloads, 1, 8);
                c.NotifyNewVideos = dto.NotifyNewVideos;
                c.OfflineKeepHours = Math.Clamp(dto.OfflineKeepHours, 1, 24 * 30);
            });
            limiter.BytesPerSecond = config.Current.UploadLimitKBps * 1024L;
            events.Publish("settings");
            return Results.Ok();
        });

        g.MapPost("/sharing/pause", (bool paused, ConfigStore config, EventHub events) =>
        {
            config.Update(c => c.SharingPaused = paused);
            events.Publish("settings");
            return Results.Ok();
        });

        g.MapPost("/shutdown", (HostInfo host, IHostApplicationLifetime life) =>
        {
            if (host.RunningAsService) return Results.StatusCode(403);
            _ = Task.Run(async () => { await Task.Delay(200); life.StopApplication(); });
            return Results.Ok();
        });
    }
}
