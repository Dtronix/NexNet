using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class SocketTransportListener : ITransportListener
{
    private readonly ServerConfig _config;
    private readonly Socket _socket;

    private SocketTransportListener(ServerConfig config, Socket socket)
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

    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        Socket clientSocket = null!;
        try
        {
            clientSocket = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);

            SocketConnection.SetRecommendedServerOptions(clientSocket);
            return await SocketTransport.CreateFromSocket(clientSocket, _config).ConfigureAwait(false);
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

        listener.Bind(endPoint);
        listener.Listen(config.AcceptorBacklog);

        return new SocketTransportListener(config, listener);
    }
}
