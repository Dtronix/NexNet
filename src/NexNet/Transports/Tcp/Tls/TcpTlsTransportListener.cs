using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Transports;

internal class TcpTlsTransportListener : ITransportListener
{
    private readonly TcpTlsServerConfig _config;
    private readonly Socket _socket;

    internal TcpTlsTransportListener(TcpTlsServerConfig config, Socket socket)
    {
        _config = config;
        _socket = socket;
    }

    public ValueTask CloseAsync(bool linger)
    {
        if (!linger)
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            return ValueTask.CompletedTask;
        }

        _socket.Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts the next connection and performs the TLS handshake. Returns null when the accept was canceled, when a
    /// connection failed before it was accepted (reset or aborted by the peer), or when an accepted connection could
    /// not be set up (including a failed or timed-out TLS handshake). Any other accept error means the listener itself
    /// is unusable, and the original exception is rethrown so the server faults the endpoint.
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
        catch (SocketException e) when (SocketTransportListener.IsTransientAcceptError(e.SocketErrorCode))
        {
            _config.Logger?.LogWarning(e, $"Client connection failed during accept ({e.SocketErrorCode}); listener continues.");
            return null;
        }

        try
        {
            SocketTransport.SetNoDelay(clientSocket);
            return await TcpTlsTransport.CreateFromSocket(clientSocket, _config).ConfigureAwait(false);
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
    /// Creates a new <see cref="ITransportListener"/> for the given <see cref="TcpTlsServerConfig"/>.
    /// </summary>
    /// <param name="config">Configuration for the listener.</param>
    /// <param name="endPoint">Connection endpoint.</param>
    /// <param name="socketType">Type of socket.</param>
    /// <param name="protocolType">Protocol type.</param>
    /// <returns>Configured Transport listener.</returns>
    public static ITransportListener Create(TcpTlsServerConfig config, EndPoint endPoint, SocketType socketType, ProtocolType protocolType)
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

        return new TcpTlsTransportListener(config, listener);
    }
}
