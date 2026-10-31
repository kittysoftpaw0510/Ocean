using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Ocean.Core;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;

namespace Ocean.App.Services;

/// <summary>Typed client for the service's loopback API, including the Server-Sent Events stream.</summary>
public sealed class ApiClient
{
    /// <summary>
    /// True when the app's own service could not be reached at all (stopped, restarting, being updated). Errors the
    /// service reports itself (including problems reaching other PCs) arrive as <see cref="ApiException"/> instead.
    /// </summary>
    public static bool IsServiceUnreachable(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded } => true,
        HttpRequestException { InnerException: System.Net.Sockets.SocketException or IOException } => true,
        _ => false,
    };

    public string BaseUrl { get; }
    public string Token { get; }
    private readonly HttpClient _http;

    public ApiClient(int port, string token)
    {
        BaseUrl = $"http://127.0.0.1:{port}";
        Token = token;
        // Classic networking (see NetworkCompat): blocking sockets on threads, no overlapped I/O.
        HttpMessageHandler transport = NetworkCompat.Classic ? new SocketsHttpHandler { ConnectCallback = NetworkCompat.ConnectAsync } : new HttpClientHandler();
        _http = new HttpClient(new LoggingHandler(transport)) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("X-Ocean-Local", token);
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException.From(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(Json.Options, ct);
    }

    public async Task<T?> PostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(path, Content(body), ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException.From(resp, ct);
        if (resp.Content.Headers.ContentLength == 0) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json.Options, ct);
    }

    public async Task PostAsync(string path, object? body = null, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(path, Content(body), ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException.From(resp, ct);
    }

    public async Task PutAsync(string path, object body, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsync(path, Content(body), ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException.From(resp, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(path, ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException.From(resp, ct);
    }

    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static HttpContent Content(object? body)
        => body is null ? new StringContent("{}", Encoding.UTF8, "application/json")
                        : new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");

    // ---- URL helpers -------------------------------------------------------------------

    public string StreamUrl(string peerId, string itemId, string name)
    {
        var safe = SafeNames.FileName(name, "video");
        if (safe.Length > 80) safe = Path.GetFileNameWithoutExtension(safe)[..Math.Min(60, Path.GetFileNameWithoutExtension(safe).Length)] + Path.GetExtension(safe);
        return $"{BaseUrl}/api/peers/{peerId}/stream/{itemId}/{Uri.EscapeDataString(safe)}?k={Token}";
    }

    public string PeerThumbPath(string peerId, string itemId) => $"/api/peers/{peerId}/thumb/{itemId}";
    public string LocalThumbPath(string itemId) => $"/api/shares/thumb/{itemId}";

    // ---- events ------------------------------------------------------------------------

    /// <summary>Reads the SSE stream; yields (eventType, jsonData). Caller handles reconnects.</summary>
    public async IAsyncEnumerable<(string Type, string Data)> EventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? type = null;
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0)
            {
                if (type is not null) yield return (type, data.ToString());
                type = null; data.Clear();
                continue;
            }
            if (line[0] == ':') continue; // heartbeat/comment
            if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].TrimStart());
        }
    }
}

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }

    private ApiException(int status, string? code, string message) : base(message) { StatusCode = status; Code = code; }

    public static async Task<ApiException> From(HttpResponseMessage resp, CancellationToken ct)
    {
        string? code = null; var message = $"Service answered {(int)resp.StatusCode}";
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options, ct);
            if (err is not null) { code = err.Error; message = err.Message ?? err.Error; }
        }
        catch { }
        return new ApiException((int)resp.StatusCode, code, message);
    }
}

/// <summary>
/// Logs calls to the service that fail, get an error answer, or are slow (successful calls are not logged: the
/// app polls often). Never logs headers, so the access token stays out of the log.
/// </summary>
internal sealed class LoggingHandler : DelegatingHandler
{
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(3);

    public LoggingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var what = $"{request.Method} {request.RequestUri?.PathAndQuery}";
        try
        {
            var resp = await base.SendAsync(request, ct);
            var code = (int)resp.StatusCode;
            var missingThumb = code == 404 && what.Contains("/thumb/", StringComparison.Ordinal);
            if (!resp.IsSuccessStatusCode && code != 304 && !missingThumb)
                AppLog.Warn("Api", $"{what} -> {code} {resp.ReasonPhrase} in {sw.ElapsedMilliseconds} ms");
            else if (sw.Elapsed > Slow && !what.Contains("/api/events", StringComparison.Ordinal))
                AppLog.Warn("Api", $"{what} slow: {sw.ElapsedMilliseconds} ms");
            return resp;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the app gave up on purpose (closed a page, a 3 s connection probe...)
        }
        catch (Exception ex)
        {
            AppLog.Warn("Api", $"{what} failed after {sw.ElapsedMilliseconds} ms", ex);
            throw;
        }
    }
}
