using System.Net;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Access;
using Ocean.Service.Cast;
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
                NetworkInfo = NetworkCompat.Describe(),
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
        g.MapGet("/peers/{peerId}/info", (string peerId, PeerService peers) =>
            peers.Info(peerId) is { } info ? Results.Json(info, Json.Options) : Results.NotFound());

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

        // The player's requests: own copy or finished download from disk, otherwise through the stream cache.
        // ---- votes and tags ----
        g.MapPost("/peers/{peerId}/rate/{itemId}", async (string peerId, string itemId, HttpContext ctx, PeerService peers) =>
        {
            var rate = await ctx.Request.ReadFromJsonAsync<RateDto>(Json.Options, ctx.RequestAborted);
            if (rate is null || rate.Vote is < -1 or > 1) return Results.BadRequest();
            return Results.Json(await peers.RateAsync(peerId, itemId, rate, ctx.RequestAborted), Json.Options);
        });
        g.MapPut("/shares/{id}/items/{itemId}/tags", async (string id, string itemId, HttpContext ctx, CatalogService catalog) =>
        {
            var dto = await ctx.Request.ReadFromJsonAsync<TagsDto>(Json.Options, ctx.RequestAborted);
            if (dto is null) return Results.BadRequest();
            return catalog.SetTags(id, itemId, dto.Tags) ? Results.Ok() : Results.NotFound();
        });
        g.MapGet("/top", (string? tag, int? limit, Social.TopList top) => Results.Json(top.Build(tag, Math.Clamp(limit ?? 30, 1, 200)), Json.Options));
        g.MapGet("/search", (string? q, int? limit, Social.NetSearch search) => Results.Json(search.Search(q ?? "", Math.Clamp(limit ?? 200, 1, 1000)), Json.Options));

        // ---- updates (GitHub releases): status, a check now; the app runs the downloaded installer ----
        g.MapGet("/update", (Update.Updater updater) => Results.Json(updater.Status, Json.Options));
        g.MapPost("/update/check", async (HttpContext ctx, Update.Updater updater) => Results.Json(await updater.CheckAsync(ctx.RequestAborted), Json.Options));
        // "Update now": downloads the newest version in the background (progress as "update" events)
        g.MapPost("/update/download", async (HttpContext ctx, Update.Updater updater) => Results.Json(await updater.DownloadNowAsync(ctx.RequestAborted), Json.Options));

        // ---- multicast: mine (host) ----
        g.MapGet("/cast", (CastHost cast) => Results.Json(cast.Status(), Json.Options));
        g.MapGet("/cast/limit", (CastHost cast) => Results.Json(cast.Limit(), Json.Options));
        g.MapPost("/cast/start", async (HttpContext ctx, CastHost cast) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CastStartDto>(Json.Options, ctx.RequestAborted);
            if (req is null || string.IsNullOrEmpty(req.ItemId)) return Results.BadRequest();
            var st = cast.Start(req.ItemId, req.PreferLight);
            return Results.Json(st, Json.Options); // Error says why it could not start
        });
        g.MapPost("/cast/stop", (CastHost cast) => { cast.Stop(); return Results.Ok(); });
        g.MapPost("/cast/detach", (CastHost cast) => { cast.Detach(); return Results.Ok(); }); // the player closed; keep it running
        g.MapPost("/cast/state", async (HttpContext ctx, CastHost cast) =>
        {
            var st = await ctx.Request.ReadFromJsonAsync<CastHostStateDto>(Json.Options, ctx.RequestAborted);
            if (st is null) return Results.BadRequest();
            cast.UpdateState(st.Position, st.Playing);
            return Results.Ok();
        });
        // ---- multicast: the one I'm watching (viewer) ----
        g.MapGet("/cast/view", (CastReceiver rx) => Results.Json(rx.Status() ?? new CastViewDto(), Json.Options));
        g.MapPost("/peers/{peerId}/cast/{sessionId}/join", async (string peerId, string sessionId, HttpContext ctx, CastReceiver rx) =>
        {
            try { return Results.Json(await rx.JoinAsync(peerId, sessionId, ctx.RequestAborted), Json.Options); }
            catch (PeerStatusException ex) when (ex.Status == HttpStatusCode.Forbidden) { return Results.Json(new ErrorDto { Error = ex.Error ?? "locked", Message = ex.Message }, Json.Options, statusCode: 403); }
            catch (Exception ex) when (ex is PeerUnavailableException or HttpRequestException or InvalidOperationException or PeerStatusException)
            { return Results.Json(new ErrorDto { Error = "cast", Message = ex.Message }, Json.Options, statusCode: 409); }
        });
        g.MapPost("/cast/leave", async (CastReceiver rx) => { await rx.LeaveAsync(); return Results.Ok(); });

        // lighter copy of another node's video: how it is doing, or (POST) ask the owner to make one
        g.MapMethods("/peers/{peerId}/light/{itemId}", new[] { "GET", "POST" }, async (string peerId, string itemId, HttpContext ctx, PeerService peers) =>
            Results.Json(await peers.LightAsync(peerId, itemId, HttpMethods.IsPost(ctx.Request.Method), ctx.RequestAborted), Json.Options));

        g.MapMethods("/peers/{peerId}/stream/{itemId}/{name?}", new[] { "GET", "HEAD" }, (string peerId, string itemId, HttpContext ctx, PeerMedia media)
            => media.ServeAsync(ctx, peerId, itemId));

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
        g.MapGet("/settings", (ConfigStore config, FirewallRules firewall, StreamCache cache, Media.LightCopies light) =>
        {
            var c = config.Current;
            return Results.Json(new SettingsDto
            {
                LightCopies = c.LightCopies, LightKeepDays = c.LightKeepDays, LightCacheGB = c.LightCacheGB, CpuLimitPercent = c.CpuLimitPercent,
                LightReadyCount = light.ReadyCount, LightUsedBytes = light.UsedBytes, LightAvailable = Media.LightCopies.Ffmpeg is not null,
                NodeName = c.NodeName, UserName = c.UserName, Secret = c.Secret, DownloadDir = c.DownloadDir,
                UploadLimitKBps = c.UploadLimitKBps, SharingPaused = c.SharingPaused, NewDays = c.NewDays,
                MaxConcurrentDownloads = c.MaxConcurrentDownloads, NotifyNewVideos = c.NotifyNewVideos, OfflineKeepHours = c.OfflineKeepHours,
                AllowPublicNetworks = c.AllowPublicNetworks, FirewallManaged = firewall.CanManage, AllowBridging = c.AllowBridging, CheckUpdates = c.CheckUpdates,
                UpdateMode = Update.Updater.ModeOf(c),
                NetworkMode = c.NetworkMode,
                StreamCacheGB = c.StreamCacheGB, StreamCacheUsedBytes = cache.UsedBytes,
            }, Json.Options);
        });

        g.MapPut("/settings", async (HttpContext ctx, ConfigStore config, UploadScheduler limiter, EventHub events) =>
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
                c.AllowPublicNetworks = dto.AllowPublicNetworks;
                c.AllowBridging = dto.AllowBridging;
                if (UpdateModes.Normalize(dto.UpdateMode) is { } updates) { c.UpdateMode = updates; c.CheckUpdates = updates != UpdateModes.Manual; }
                else { c.CheckUpdates = dto.CheckUpdates; c.UpdateMode = null; } // an older app: on/off only
                c.LightCopies = dto.LightCopies;
                c.LightKeepDays = Math.Clamp(dto.LightKeepDays, 1, 365);
                c.LightCacheGB = Math.Clamp(dto.LightCacheGB, 1, 2000);
                if (dto.CpuLimitPercent > 0) c.CpuLimitPercent = Math.Clamp(dto.CpuLimitPercent, 10, 100);
                if (Enum.TryParse<NetworkMode>(dto.NetworkMode, true, out var mode)) c.NetworkMode = mode.ToString().ToLowerInvariant();
                c.StreamCacheGB = Math.Clamp(dto.StreamCacheGB, 0, 500);
            });
            limiter.BytesPerSecond = config.Current.UploadLimitKBps * 1024L;
            events.Publish("settings");
            return Results.Ok();
        });

        g.MapPost("/cache/clear", (StreamCache cache) => { cache.Clear(); return Results.Json(new { used = cache.UsedBytes }); });

        g.MapPost("/sharing/pause", (bool paused, ConfigStore config, EventHub events) =>
        {
            config.Update(c => c.SharingPaused = paused);
            events.Publish("settings");
            return Results.Ok();
        });

        // Restart the service (a changed networking mode applies at start). As a Windows service: exit with a failure
        // code and let the service manager restart it (ServiceRestart); as a background process: stop, the app starts it again.
        g.MapPost("/service/restart", (HostInfo host, IHostApplicationLifetime life) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                if (host.RunningAsService) ServiceRestart.ExitForRestart(); else life.StopApplication();
            });
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
