using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Ocean.Core;

namespace Ocean.Service.Net;

/// <summary>
/// Kestrel transport whose listening and accepted sockets live on Windows' own network provider
/// (<see cref="BaseSockets"/>), so an incompatible network add-on on the PC can't crash the service. Used only when
/// <see cref="BaseSockets.Bypass"/> is on; everything above the socket (TLS, HTTP) is Kestrel's own code.
/// </summary>
public sealed class BaseProviderTransportFactory : IConnectionListenerFactory, IDisposable
{
    private readonly SocketConnectionContextFactory _contexts;
    private readonly ILogger _log;

    public BaseProviderTransportFactory(ILoggerFactory loggers)
    {
        _log = loggers.CreateLogger<BaseProviderTransportFactory>();
        _contexts = new SocketConnectionContextFactory(new SocketConnectionFactoryOptions(), loggers.CreateLogger<SocketConnectionContextFactory>());
    }

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
        _log.LogInformation("listening on {Endpoint} on Windows' own network provider (network add-ons bypassed)", socket.LocalEndPoint);
        if (BaseSockets.FallbackReason is { } why) _log.LogWarning("could not always use Windows' own network provider: {Why}", why);
        return new ValueTask<IConnectionListener>(new Listener(socket, _contexts));
    }

    public void Dispose() => _contexts.Dispose();

    private sealed class Listener : IConnectionListener
    {
        private readonly Socket _socket;
        private readonly SocketConnectionContextFactory _contexts;

        public Listener(Socket socket, SocketConnectionContextFactory contexts) { _socket = socket; _contexts = contexts; }

        public EndPoint EndPoint => _socket.LocalEndPoint!;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken ct = default)
        {
            while (true)
            {
                // The accepted socket is created up front on the base provider too (otherwise Windows would pick it
                // through the add-on).
                var accept = BaseSockets.Create(_socket.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var s = await _socket.AcceptAsync(accept, ct);
                    s.NoDelay = true;
                    return _contexts.Create(s);
                }
                catch (ObjectDisposedException) { accept.Dispose(); return null; }
                catch (OperationCanceledException) { accept.Dispose(); return null; }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.InvalidArgument)
                {
                    accept.Dispose();
                    return null; // listener closed
                }
                catch (SocketException)
                {
                    accept.Dispose(); // a client gave up before we accepted it: keep listening
                }
            }
        }

        public ValueTask UnbindAsync(CancellationToken ct = default) { _socket.Dispose(); return default; }

        public ValueTask DisposeAsync() { _socket.Dispose(); return default; }
    }
}
