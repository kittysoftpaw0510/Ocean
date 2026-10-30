using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Ocean.Core;

namespace Ocean.Service.Net;

/// <summary>A request waiting to be carried over a reverse connection.</summary>
public sealed class RelayJob
{
    public string Id { get; set; } = "";
    public string Method { get; set; } = "GET";
    /// <summary>Path and query on the other node's peer API, e.g. "/p/stream/abc".</summary>
    public string Path { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Small request body (JSON for /p/access), base64.</summary>
    public string? Body { get; set; }
    public string? ContentType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] internal string PeerId = "";
    [System.Text.Json.Serialization.JsonIgnore] internal TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    [System.Text.Json.Serialization.JsonIgnore] internal TaskCompletionSource Consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Requesting side of the reverse connection ("this PC can't connect to node X, so X connects to us").
/// Requests for X are queued here; X long-polls <c>/p/relay/poll</c>, runs each request on its own peer API
/// on our behalf and streams the answer back through <c>/p/relay/response/{id}</c>.
/// </summary>
public sealed class RelayHub
{
    private static readonly TimeSpan WantFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstAnswerTimeout = TimeSpan.FromSeconds(25);

    private readonly ConcurrentDictionary<string, Channel<RelayJob>> _queues = new();
    private readonly ConcurrentDictionary<string, RelayJob> _jobs = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _wanted = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPoll = new();
    private readonly ILogger<RelayHub> _log;

    /// <summary>Raised when a node newly needs a reverse connection (announce right away).</summary>
    public event Action? WantChanged;

    public RelayHub(ILogger<RelayHub> log) { _log = log; }

    /// <summary>Nodes we currently ask to connect back to us (carried in our beacons).</summary>
    public List<string> WantedPeers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _wanted) if (now - kv.Value > WantFor) _wanted.TryRemove(kv.Key, out _);
        return _wanted.Keys.ToList();
    }

    public bool IsWanted(string peerId) => _wanted.ContainsKey(peerId);

    /// <summary>True when the node has polled us recently, i.e. the reverse link is up.</summary>
    public bool IsConnected(string peerId) => _lastPoll.TryGetValue(peerId, out var t) && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(40);

    private Channel<RelayJob> Queue(string peerId) => _queues.GetOrAdd(peerId, _ => Channel.CreateUnbounded<RelayJob>());

    /// <summary>Sends a request to <paramref name="peerId"/> through its reverse connection.</summary>
    public async Task<HttpResponseMessage> SendAsync(string peerId, HttpRequestMessage req, CancellationToken ct)
    {
        var isNew = _wanted.TryAdd(peerId, DateTimeOffset.UtcNow);
        _wanted[peerId] = DateTimeOffset.UtcNow;
        if (isNew) { _log.LogInformation("asking {Peer} to connect back to us (direct connection failed)", peerId[..12]); WantChanged?.Invoke(); }

        var job = new RelayJob { Id = Guid.NewGuid().ToString("N"), Method = req.Method.Method, Path = req.RequestUri!.PathAndQuery, PeerId = peerId };
        foreach (var h in req.Headers) job.Headers[h.Key] = string.Join(",", h.Value);
        if (req.Content is not null)
        {
            job.Body = Convert.ToBase64String(await req.Content.ReadAsByteArrayAsync(ct));
            job.ContentType = req.Content.Headers.ContentType?.ToString();
        }
        _jobs[job.Id] = job;
        await Queue(peerId).Writer.WriteAsync(job, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FirstAnswerTimeout);
        try
        {
            await using var reg = timeout.Token.Register(() => job.Response.TrySetCanceled());
            return await job.Response.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _jobs.TryRemove(job.Id, out _);
            throw new HttpRequestException("The node did not connect back to us (reverse connection)", null, HttpStatusCode.GatewayTimeout);
        }
    }

    /// <summary>Called by the other node: wait up to 25 s for the next request for it.</summary>
    public async Task<RelayJob?> PollAsync(string peerId, CancellationToken ct)
    {
        _lastPoll[peerId] = DateTimeOffset.UtcNow;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            while (true)
            {
                var job = await Queue(peerId).Reader.ReadAsync(wait.Token);
                if (_jobs.ContainsKey(job.Id) && !job.Response.Task.IsCompleted) return job; // skip jobs that already timed out
            }
        }
        catch (OperationCanceledException) { return null; }
        finally { _lastPoll[peerId] = DateTimeOffset.UtcNow; }
    }

    /// <summary>
    /// Called by the other node with the answer: hands status, headers and the streaming body to the waiting
    /// requester and keeps the upload open until the requester has finished reading it.
    /// </summary>
    public async Task<bool> CompleteAsync(string peerId, string jobId, int status, Dictionary<string, string> headers, Stream body, CancellationToken ct)
    {
        if (!_jobs.TryRemove(jobId, out var job) || job.PeerId != peerId) return false;
        // A compressed answer (catalogs) is unpacked here, as the HTTP client would do on a direct connection.
        headers.TryGetValue("Content-Encoding", out var encoding);
        Stream content = new NotifyOnDisposeStream(body, () => job.Consumed.TrySetResult());
        var unpack = encoding?.Trim().ToLowerInvariant() switch
        {
            "br" => (Stream)new System.IO.Compression.BrotliStream(content, System.IO.Compression.CompressionMode.Decompress),
            "gzip" => new System.IO.Compression.GZipStream(content, System.IO.Compression.CompressionMode.Decompress),
            _ => null,
        };
        var resp = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(unpack ?? content) };
        foreach (var (k, v) in headers)
        {
            if (unpack is not null && (k.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) || k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))) continue;
            if (!resp.Headers.TryAddWithoutValidation(k, v)) resp.Content.Headers.TryAddWithoutValidation(k, v);
        }
        if (!job.Response.TrySetResult(resp)) { resp.Dispose(); return false; }
        // keep this request (the upload carrying the body) alive while the consumer reads it
        await using var reg = ct.Register(() => job.Consumed.TrySetResult());
        await job.Consumed.Task;
        return true;
    }

    /// <summary>Stream wrapper that reports when the consumer is done with it.</summary>
    private sealed class NotifyOnDisposeStream : Stream
    {
        private readonly Stream _inner; private readonly Action _done; private int _disposed;
        public NotifyOnDisposeStream(Stream inner, Action done) { _inner = inner; _done = done; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref _disposed, 1) == 0) _done(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Dispose(true); return ValueTask.CompletedTask; }
    }
}
