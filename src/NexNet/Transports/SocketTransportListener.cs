using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Transports;

internal class SocketTransportListener : ITransportListener
{
    private readonly ServerConfig _config;
    private readonly Socket _socket;
    private readonly Action? _afterClose;

    internal SocketTransportListener(ServerConfig config, Socket socket, Action? afterClose = null)
    {
        _config = config;
        _socket = socket;
        _afterClose = afterClose;
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

    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        Socket clientSocket = null!;
        try
        {
            clientSocket = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);

            SocketTransport.SetNoDelay(clientSocket);
            return await SocketTransport.CreateFromSocket(clientSocket, _config).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // noop.
        }
        catch (Exception e)
        {
            _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");

            // Immediate disconnect.
            clientSocket.LingerState = new LingerOption(true, 0);
            clientSocket.Close(0);
        }

        return null;
    }

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
