using System.Net;
using Microsoft.Net.Http.Headers;
using Ocean.Core;
using Ocean.Core.Media;
using Ocean.Core.Protocol;
using Ocean.Service.Downloads;
using Ocean.Service.Net;

namespace Ocean.Service.Peers;

/// <summary>
/// Answers the player's requests for a video on another PC, spending as little network as possible:
/// <list type="number">
/// <item>the same video in my own shares, or a finished download of it, plays straight from disk;</item>
/// <item>otherwise bytes come through the stream cache (watched parts from disk, the rest from the owner);</item>
/// <item>when the video isn't in the catalog we hold (rare), the request is passed through as is.</item>
/// </list>
/// A bridge serves a public video of a node it hears the same way to a node that can't reach it (see
/// <see cref="ServeForBridgeAsync"/>), paced like its own uploads.
/// </summary>
public sealed class PeerMedia
{
    private readonly PeerService _peers;
    private readonly StreamCache _cache;
    private readonly DownloadManager _downloads;
    private readonly UploadScheduler _uploads;
    private readonly ILogger<PeerMedia> _log;

    public PeerMedia(PeerService peers, StreamCache cache, DownloadManager downloads, UploadScheduler uploads, ILogger<PeerMedia> log)
    {
        _peers = peers; _cache = cache; _downloads = downloads; _uploads = uploads; _log = log;
    }

    public async Task ServeAsync(HttpContext ctx, string peerId, string itemId)
    {
        var item = _peers.FindItem(peerId, itemId);
        if (item is null || item.Size <= 0 || (item.Lock is not null && !item.Unlocked))
        {
            await PassThroughAsync(ctx, peerId, itemId);
            return;
        }

        var local = _peers.OwnCopyPath(item) ?? _downloads.CompletedCopy(peerId, itemId, item.Size);
        if (local is not null)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            await Results.File(local, VideoTypes.ContentType(item.Name), lastModified: item.ModifiedAt, enableRangeProcessing: true).ExecuteAsync(ctx);
            return;
        }

        await ServeCachedAsync(ctx, peerId, item);
    }

    /// <summary>
    /// Bridge: a public video of <paramref name="peerId"/> (a node we hear) for a node that can't reach it. Our own
    /// copy or the stream cache serve it, so one viewer's bytes serve the next and a seek back costs the owner
    /// nothing; what goes out is paced like our own uploads (viewers before downloads, the upload limit).
    /// </summary>
    public async Task ServeForBridgeAsync(HttpContext ctx, string peerId, ItemDto item, UploadKind kind, IDisposable? slot)
    {
        try
        {
            var local = _peers.OwnCopyPath(item) ?? _downloads.CompletedCopy(peerId, item.Id, item.Size);
            if (local is not null)
            {
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (fs is not null)
                {
                    var body = new ThrottledStream(fs, _uploads, kind, slot);
                    slot = null; // released with the stream
                    ctx.Response.Headers.CacheControl = "no-store";
                    await Results.Stream(body, VideoTypes.ContentType(item.Name), lastModified: item.ModifiedAt,
                        entityTag: new EntityTagHeaderValue($"\"{item.Id}-{item.ModifiedAt.UtcTicks}\""), enableRangeProcessing: true).ExecuteAsync(ctx);
                    return;
                }
            }
            await ServeCachedAsync(ctx, peerId, item, new PacedStream(ctx.Response.Body, _uploads, kind), kind == UploadKind.Download);
        }
        finally { slot?.Dispose(); }
    }

    private async Task ServeCachedAsync(HttpContext ctx, string peerId, ItemDto item, Stream? dest = null, bool download = false)
    {
        var size = item.Size;
        var res = ctx.Response;
        res.Headers.AcceptRanges = "bytes";
        res.Headers.CacheControl = "no-store";
        res.Headers.ETag = $"\"{item.Id}-{item.ModifiedAt.UtcTicks}\"";
        res.Headers.LastModified = item.ModifiedAt.ToString("R");
        res.ContentType = VideoTypes.ContentType(item.Name);

        long from = 0, to = size - 1;
        var range = ParseRange(ctx.Request.Headers.Range.ToString(), size);
        if (range is { Valid: false })
        {
            res.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            res.Headers.ContentRange = $"bytes */{size}";
            return;
        }
        if (range is { } r)
        {
            from = r.From; to = r.To;
            res.StatusCode = StatusCodes.Status206PartialContent;
            res.Headers.ContentRange = $"bytes {from}-{to}/{size}";
        }
        else res.StatusCode = StatusCodes.Status200OK;
        res.ContentLength = to - from + 1;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;

        try
        {
            await _cache.CopyAsync(peerId, item, from, to, dest ?? res.Body, fill: true, download, ctx.RequestAborted);
        }
        catch (Exception ex) when (!res.HasStarted && ex is PeerStatusException or PeerUnavailableException or HttpRequestException)
        {
            // nothing sent yet: report the owner's answer like the plain proxy did (a "busy" answer keeps its
            // body and Retry-After, so a download through here waits as it would on the owner)
            res.Clear();
            res.StatusCode = ex switch
            {
                PeerStatusException p => (int)p.Status,
                PeerUnavailableException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status502BadGateway,
            };
            if (ex is PeerStatusException ps && ps.Error is not null)
            {
                if (ps.RetryAfter is { } wait) res.Headers.RetryAfter = ((int)wait.TotalSeconds).ToString();
                await res.WriteAsJsonAsync(new ErrorDto { Error = ps.Error, Message = ps.Message }, Json.Options);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // mid-stream failure (the owner went away, the player hung up): end the response abruptly so the player
            // notices instead of seeing a short file
            if (!ctx.RequestAborted.IsCancellationRequested) _log.LogDebug(ex, "stream of {Item} interrupted", item.Name);
            ctx.Abort();
        }
    }

    /// <summary>The previous behaviour: forward the request to the owner and copy the answer back.</summary>
    private async Task PassThroughAsync(HttpContext ctx, string peerId, string itemId)
    {
        var range = ctx.Request.Headers.Range.ToString();
        HttpResponseMessage resp;
        try { resp = await _peers.OpenStreamAsync(peerId, itemId, string.IsNullOrEmpty(range) ? null : range, ctx.RequestAborted); }
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
    }

    private readonly record struct ByteRange(long From, long To, bool Valid);

    /// <summary>A single "bytes=a-b", "bytes=a-" or "bytes=-n" range; null = whole file (absent or not understood).</summary>
    private static ByteRange? ParseRange(string header, long size)
    {
        if (string.IsNullOrWhiteSpace(header) || !RangeHeaderValue.TryParse(header, out var parsed)
            || !string.Equals(parsed.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) || parsed.Ranges.Count != 1)
            return null;
        var r = parsed.Ranges.First();
        long from, to;
        if (r.From is { } f)
        {
            from = f;
            to = Math.Min(r.To ?? size - 1, size - 1);
        }
        else if (r.To is { } suffix)
        {
            if (suffix <= 0) return new ByteRange(0, 0, false);
            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else return null;
        return from >= size || from > to ? new ByteRange(0, 0, false) : new ByteRange(from, to, true);
    }
}
