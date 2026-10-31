using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Ocean.Core;

namespace Ocean.Service.Net;

/// <summary>
/// Kestrel transport for <see cref="NetworkCompat"/>'s classic mode: a blocking accept loop on its own thread and,
/// per connection, a <see cref="ClassicSocketStream"/> (blocking receive and send on two threads) exposed to Kestrel
/// as pipes. No overlapped I/O anywhere, so a network add-on that completes it twice has nothing to break. TLS and
/// HTTP above it are Kestrel's own code.
/// </summary>
public sealed class ClassicTransportFactory : IConnectionListenerFactory
{
    private readonly ILogger<ClassicTransportFactory> _log;

    public ClassicTransportFactory(ILogger<ClassicTransportFactory> log) { _log = log; }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        if (endpoint is not IPEndPoint ip) throw new NotSupportedException($"Endpoint {endpoint} is not supported");
        var socket = BaseSockets.Create(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (ip.Address.Equals(IPAddress.IPv6Any)) socket.DualMode = true;
            socket.Bind(ip);
            socket.Listen(512);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _log.LogInformation("listening on {Endpoint} (classic networking: blocking sockets on threads)", socket.LocalEndPoint);
        return new ValueTask<IConnectionListener>(new Listener(socket));
    }

    private sealed class Listener : IConnectionListener
    {
        private readonly Socket _socket;
        private readonly Channel<Socket> _accepted = Channel.CreateUnbounded<Socket>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public Listener(Socket socket)
        {
            _socket = socket;
            new Thread(AcceptLoop, 256 * 1024) { IsBackground = true, Name = "ocean-accept" }.Start();
        }

        public EndPoint EndPoint => _socket.LocalEndPoint!;

        private void AcceptLoop()
        {
            while (true)
            {
                Socket client;
                try { client = _socket.Accept(); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.TimedOut)
                {
                    continue; // a client gave up before we got to it: keep listening
                }
                catch { break; } // the listener was closed
                if (!_accepted.Writer.TryWrite(client)) { client.Dispose(); break; }
            }
            _accepted.Writer.TryComplete();
        }

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken ct = default)
        {
            try
            {
                var socket = await _accepted.Reader.ReadAsync(ct);
                return new ClassicConnection(socket);
            }
            catch (ChannelClosedException) { return null; }
            catch (OperationCanceledException) { return null; }
        }

        public ValueTask UnbindAsync(CancellationToken ct = default)
        {
            try { _socket.Close(); } catch { }
            return default;
        }

        public ValueTask DisposeAsync()
        {
            try { _socket.Close(); } catch { }
            while (_accepted.Reader.TryRead(out var s)) s.Dispose();
            return default;
        }
    }
}

internal sealed class ClassicConnection : ConnectionContext,
    IConnectionIdFeature, IConnectionTransportFeature, IConnectionItemsFeature, IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private static long _nextId;
    private readonly ClassicSocketStream _stream;
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<object, object?> _items = new();
    private int _disposed;

    public ClassicConnection(Socket socket)
    {
        try { socket.NoDelay = true; } catch { }
        _stream = new ClassicSocketStream(socket);
        ConnectionId = "classic-" + Interlocked.Increment(ref _nextId).ToString("x");
        LocalEndPoint = _stream.LocalEndPoint;
        RemoteEndPoint = _stream.RemoteEndPoint;
        Transport = new DuplexPipe(
            PipeReader.Create(_stream, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true)),
            PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true)));
        Features.Set<IConnectionIdFeature>(this);
        Features.Set<IConnectionTransportFeature>(this);
        Features.Set<IConnectionItemsFeature>(this);
        Features.Set<IConnectionLifetimeFeature>(this);
        Features.Set<IConnectionEndPointFeature>(this);
    }

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get => _items; set => throw new NotSupportedException(); }
    public override IDuplexPipe Transport { get; set; }
    public override CancellationToken ConnectionClosed { get => _closed.Token; set { } }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _stream.Abort();
        Cancel();
    }

    public override void Abort() => Abort(new ConnectionAbortedException("The connection was aborted by the server."));

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return default;
        _stream.Dispose();
        Cancel();
        return default;
    }

    private void Cancel()
    {
        try { _closed.Cancel(); } catch { }
    }

    private sealed class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader input, PipeWriter output) { Input = input; Output = output; }
        public PipeReader Input { get; }
        public PipeWriter Output { get; }
    }
}
