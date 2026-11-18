using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Transports;

internal class SocketTransportListener : ITransportListener, IBoundTransportListener
{
    private readonly ServerConfig _config;
    private readonly Socket _socket;
    private readonly Action? _afterClose;

    /// <param name="config">Configuration of the endpoint.</param>
    /// <param name="socket">A socket that is already bound and listening.</param>
    /// <param name="afterClose">Runs after the socket is closed.</param>
    internal SocketTransportListener(ServerConfig config, Socket socket, Action? afterClose = null)
    {
        _config = config;
        _socket = socket;
        _afterClose = afterClose;
        BoundEndPoint = GetBoundEndPoint(socket);
    }

    /// <inheritdoc />
    public EndPoint? BoundEndPoint { get; }

    /// <summary>
    /// Reads <see cref="Socket.LocalEndPoint"/> of a bound socket; null when the platform cannot report it.
    /// </summary>
    internal static EndPoint? GetBoundEndPoint(Socket socket)
    {
        try
        {
            return socket.LocalEndPoint;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public ValueTask CloseAsync(bool linger)
    {
        if (!linger)
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            _afterClose?.Invoke();
            return ValueTask.CompletedTask;
        }

        _socket.Close();
        _afterClose?.Invoke();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts the next connection. Returns null when the accept was canceled, when a connection failed before it
    /// was accepted (reset or aborted by the peer), or when an accepted connection could not be set up. Any other
    /// accept error means the listener itself is unusable, and the original exception is rethrown so the server
    /// faults the endpoint.
    /// </summary>
    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        Socket clientSocket;
        try
        {
            clientSocket = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException e) when (IsTransientAcceptError(e.SocketErrorCode))
        {
            _config.Logger?.LogWarning(e, $"Client connection failed during accept ({e.SocketErrorCode}); listener continues.");
            return null;
        }

        try
        {
            SocketTransport.SetNoDelay(clientSocket);
            return await SocketTransport.CreateFromSocket(clientSocket, _config).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");

            // Immediate disconnect.
            try
            {
                clientSocket.LingerState = new LingerOption(true, 0);
                clientSocket.Close(0);
            }
            catch (Exception closeException)
            {
                _config.Logger?.LogDebug(closeException, "Failed to close the rejected client socket.");
            }
        }

        return null;
    }

    /// <summary>
    /// Accept errors caused by a single pending connection (the peer reset or aborted it before it was accepted).
    /// The listening socket stays usable after these.
    /// </summary>
    internal static bool IsTransientAcceptError(SocketError error) => error
        is SocketError.ConnectionReset
        or SocketError.ConnectionAborted
        or SocketError.NetworkReset;

    public static ITransportListener Create(ServerConfig config, EndPoint endPoint, SocketType socketType, ProtocolType protocolType)
    {
        Socket listener = new Socket(endPoint.AddressFamily, socketType, protocolType);

        try
        {
            listener.Bind(endPoint);
            listener.Listen(config.AcceptorBacklog);
        }
        catch (SocketException e)
        {
            throw new TransportException(e.SocketErrorCode, e.Message, e);
        }
        catch (Exception)
        {
            throw;
        }

        return new SocketTransportListener(config, listener);
    }
}
