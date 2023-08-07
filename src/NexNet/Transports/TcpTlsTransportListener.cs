using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class TcpTlsTransportListener : ITransportListener
{
    private readonly TcpTlsServerConfig _config;
    private readonly Socket _socket;

    private TcpTlsTransportListener(TcpTlsServerConfig config, Socket socket)
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

    public async Task<ITransport?> AcceptTransportAsync()
    {
        var clientSocket = await _socket.AcceptAsync().ConfigureAwait(false);

        SocketConnection.SetRecommendedServerOptions(clientSocket);

        try
        {
            return await TcpTlsTransport.CreateFromSocket(clientSocket, _config).ConfigureAwait(false);
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

    public static ITransportListener Create(TcpTlsServerConfig config, EndPoint endPoint, SocketType socketType, ProtocolType protocolType)
    {
        Socket listener = new Socket(endPoint.AddressFamily, socketType, protocolType);

        listener.Bind(endPoint);
        listener.Listen(config.AcceptorBacklog);

        return new TcpTlsTransportListener(config, listener);
    }
}
