using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Ocean.Core;

public enum NetworkMode { Auto, Classic, Fast }

/// <summary>
/// Decides how Ocean talks to the network on this PC.
/// <para>
/// <b>Fast</b> is .NET's normal asynchronous socket I/O (overlapped operations completed through an I/O completion
/// port). <b>Classic</b> is what programs like IPmsg do: ordinary blocking reads and writes on dedicated threads,
/// never an overlapped operation. Some network add-ons hooked into Windows networking (VPN clients, proxies,
/// "accelerators", security tools; e.g. Astrill's ASProxy) complete overlapped operations twice, and .NET then kills
/// the process ("'overlapped' has already been freed") within a second of the first connection. Nothing of the kind
/// can happen to a blocking call, so Classic survives them.
/// </para>
/// <para>
/// <b>Auto</b> (default) picks Classic when such an add-on is installed (<see cref="WinsockCatalog"/>) or when the
/// fast mode crashed on this PC before (<see cref="RecordCrash"/> leaves a marker file, so a PC heals itself on the
/// next start, even for add-ons that aren't detected). <c>OCEAN_NET_MODE=classic|fast</c> overrides everything.
/// </para>
/// </summary>
public static class NetworkCompat
{
    public const string MarkerFile = "classic-network.flag";
    public static string MarkerPath => Path.Combine(OceanDefaults.DataDir, MarkerFile);

    private static readonly object Lock = new();
    private static bool _decided, _classic;
    private static string _reason = "";
    private static NetworkMode _configured = NetworkMode.Auto;
    private static IReadOnlyList<WinsockCatalog.Provider> _addOns = Array.Empty<WinsockCatalog.Provider>();

    /// <summary>The mode from settings; call before anything opens a socket (a later call re-decides).</summary>
    public static void Configure(NetworkMode mode)
    {
        lock (Lock) { _configured = mode; _decided = false; }
    }

    public static bool Classic { get { Decide(); return _classic; } }
    public static string Reason { get { Decide(); return _reason; } }
    public static IReadOnlyList<WinsockCatalog.Provider> AddOns { get { Decide(); return _addOns; } }

    /// <summary>One line for logs, status and settings.</summary>
    public static string Describe() => Classic ? "Compatibility mode (classic networking): " + Reason : "Fast mode (asynchronous networking): " + Reason;

    private static void Decide()
    {
        if (_decided) return;
        lock (Lock)
        {
            if (_decided) return;
            try { _addOns = WinsockCatalog.LayeredProviders(); } catch { _addOns = Array.Empty<WinsockCatalog.Provider>(); }
            var env = Environment.GetEnvironmentVariable("OCEAN_NET_MODE")?.Trim().ToLowerInvariant();
            var marker = ReadMarker();
            if (env == "fast") { _classic = false; _reason = "OCEAN_NET_MODE=fast"; }
            else if (env == "classic") { _classic = true; _reason = "OCEAN_NET_MODE=classic"; }
            else if (marker is not null) { _classic = true; _reason = "the fast mode crashed on this PC (" + marker + ")"; }
            else if (_configured == NetworkMode.Classic) { _classic = true; _reason = "chosen in Settings"; }
            else if (_configured == NetworkMode.Fast) { _classic = false; _reason = "chosen in Settings"; }
            else if (_addOns.Count > 0) { _classic = true; _reason = "network add-on(s) installed: " + WinsockCatalog.Describe(_addOns); }
            else { _classic = false; _reason = "no network add-ons found"; }
            _decided = true;
        }
    }

    private static string? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var text = File.ReadAllText(MarkerPath).Trim();
            return text.Length == 0 ? "earlier" : text;
        }
        catch { return null; }
    }

    /// <summary>The crash signature of an add-on that completes overlapped I/O twice.</summary>
    public static bool LooksLikeOverlappedCrash(Exception? ex)
    {
        if (ex is null) return false;
        var text = ex.ToString();
        return text.Contains("overlapped", StringComparison.OrdinalIgnoreCase)
               || text.Contains("IOCompletionPoller", StringComparison.Ordinal)
               || text.Contains("SocketAsyncEventArgs", StringComparison.Ordinal)
               || text.Contains("ThreadPoolBoundHandle", StringComparison.Ordinal);
    }

    /// <summary>
    /// Leaves the marker that switches this PC to Classic from the next start on. Returns false when nothing changes
    /// (already Classic, or the marker could not be written).
    /// </summary>
    public static bool RecordCrash(Exception ex)
    {
        if (Classic) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            var first = ex.Message.Split('\n')[0].Trim();
            File.WriteAllText(MarkerPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {ex.GetType().Name}: {first}");
            return true;
        }
        catch { return false; }
    }

    public static void ClearMarker()
    {
        try { File.Delete(MarkerPath); } catch { }
    }

    // ------------------------------------------------------------------ connecting (HttpClient)

    /// <summary>
    /// For <see cref="SocketsHttpHandler.ConnectCallback"/>: a blocking connect on its own thread, then a
    /// <see cref="ClassicSocketStream"/>. Cancellation (the handler's ConnectTimeout) closes the socket.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var ip)
            ? new[] { ip }
            : await Task.Run(() => Dns.GetHostAddresses(host), ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = BaseSockets.Create(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                await ClassicConnectAsync(socket, new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new ClassicSocketStream(socket);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static Task ClassicConnectAsync(Socket socket, EndPoint endpoint, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => { try { socket.Close(); } catch { } });
        var thread = new Thread(() =>
        {
            try { socket.Connect(endpoint); tcs.TrySetResult(); }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct); else tcs.TrySetException(ex);
            }
            finally { registration.Dispose(); }
        }, 128 * 1024) { IsBackground = true, Name = "ocean-connect" };
        thread.Start();
        return tcs.Task;
    }
}

/// <summary>
/// A stream over a connected socket that uses only blocking socket calls, on two dedicated threads: one receives
/// into a bounded queue, one sends from a bounded queue. No overlapped I/O, no completion port (see
/// <see cref="NetworkCompat"/>). The queues give normal TCP back-pressure: a reader that stops consuming stops the
/// sender, a slow peer stops the writer.
/// <para>
/// <see cref="Dispose()"/> is graceful: what was written is still sent, then the socket is shut down and closed.
/// <see cref="Abort"/> closes the socket at once (and so unblocks both threads).
/// </para>
/// </summary>
public sealed class ClassicSocketStream : Stream
{
    private const int ChunkSize = 64 * 1024;
    private const int QueueDepth = 16; // chunks each way (~1 MB) before the other side has to catch up

    private readonly Socket _socket;
    private readonly Channel<ArraySegment<byte>> _inbound = Channel.CreateBounded<ArraySegment<byte>>(
        new BoundedChannelOptions(QueueDepth) { SingleWriter = true, SingleReader = false });
    private readonly Channel<Outgoing> _outbound = Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(QueueDepth) { SingleWriter = false, SingleReader = true });
    private ArraySegment<byte> _current; // unread rest of the last inbound chunk
    private Exception? _sendError;
    private int _disposed;

    private readonly record struct Outgoing(ArraySegment<byte> Data, TaskCompletionSource? Flushed);

    public ClassicSocketStream(Socket socket)
    {
        _socket = socket;
        Start("ocean-net-rx", ReceiveLoop);
        Start("ocean-net-tx", SendLoop);
    }

    public EndPoint? LocalEndPoint { get { try { return _socket.LocalEndPoint; } catch { return null; } } }
    public EndPoint? RemoteEndPoint { get { try { return _socket.RemoteEndPoint; } catch { return null; } } }

    private static void Start(string name, ThreadStart work)
        => new Thread(work, 256 * 1024) { IsBackground = true, Name = name }.Start();

    // ---- socket side ---------------------------------------------------------------------------

    private void ReceiveLoop()
    {
        try
        {
            while (true)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
                int n;
                try { n = _socket.Receive(buffer, 0, ChunkSize, SocketFlags.None); }
                catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
                if (n <= 0) { ArrayPool<byte>.Shared.Return(buffer); break; } // the peer finished sending
                try
                {
                    // waits while the consumer is behind (back-pressure); a disposed stream ends the loop
                    _inbound.Writer.WriteAsync(new ArraySegment<byte>(buffer, 0, n)).AsTask().GetAwaiter().GetResult();
                }
                catch (ChannelClosedException) { ArrayPool<byte>.Shared.Return(buffer); break; }
            }
            _inbound.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // after Dispose/Abort a socket error is just the close; before, the consumer must hear about it
            _inbound.Writer.TryComplete(Volatile.Read(ref _disposed) == 1 ? null : ex);
        }
    }

    private void SendLoop()
    {
        Exception? error = null;
        try
        {
            while (_outbound.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_outbound.Reader.TryRead(out var item))
                {
                    try
                    {
                        if (error is null && item.Data.Count > 0)
                        {
                            var sent = 0;
                            while (sent < item.Data.Count)
                                sent += _socket.Send(item.Data.Array!, item.Data.Offset + sent, item.Data.Count - sent, SocketFlags.None);
                        }
                    }
                    catch (Exception ex) { error ??= ex; }
                    finally
                    {
                        if (item.Data.Array is { } array) ArrayPool<byte>.Shared.Return(array);
                        if (item.Flushed is { } flushed)
                        {
                            if (error is null) flushed.TrySetResult();
                            else flushed.TrySetException(new IOException("The connection was lost while sending.", error));
                        }
                    }
                }
            }
        }
        catch (Exception ex) { error ??= ex; }
        finally
        {
            _sendError = error;
            // Everything queued was sent (or the connection is dead): finish, which also unblocks the receiver.
            if (error is null) { try { _socket.Shutdown(SocketShutdown.Send); } catch { } }
            try { _socket.Close(); } catch { }
        }
    }

    // ---- stream side ---------------------------------------------------------------------------

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (_current.Count == 0)
        {
            if (_current.Array is { } done) { ArrayPool<byte>.Shared.Return(done); _current = default; }
            bool more;
            try { more = await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new IOException("The connection was lost.", ex); }
            if (!more) return 0;
            _inbound.Reader.TryRead(out _current);
        }
        var n = Math.Min(buffer.Length, _current.Count);
        _current.AsSpan(0, n).CopyTo(buffer.Span);
        _current = _current.Slice(n);
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;
        if (_sendError is { } failed) throw new IOException("The connection was lost while sending.", failed);
        var array = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.CopyTo(array);
        try { await _outbound.Writer.WriteAsync(new Outgoing(new ArraySegment<byte>(array, 0, buffer.Length), null), ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { ArrayPool<byte>.Shared.Return(array); throw new ObjectDisposedException(nameof(ClassicSocketStream)); }
    }

    /// <summary>Completes once everything written so far has been handed to the network.</summary>
    public override async Task FlushAsync(CancellationToken ct)
    {
        if (_sendError is { } failed) throw new IOException("The connection was lost while sending.", failed);
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _outbound.Writer.WriteAsync(new Outgoing(default, flushed), ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { return; } // disposed: the send thread drains what it has
        await flushed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var n = ReadAsync(rented.AsMemory(0, buffer.Length)).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(ReadOnlySpan<byte> buffer) => WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Closes the socket right away: pending sends are dropped, both threads end.</summary>
    public void Abort()
    {
        Dispose();
        try { _socket.Close(); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _outbound.Writer.TryComplete(); // the send thread drains, shuts down and closes the socket
        _inbound.Writer.TryComplete();
        if (_current.Array is { } array) { ArrayPool<byte>.Shared.Return(array); _current = default; }
        base.Dispose(disposing);
    }
}
