using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Threading.Tasks.Dataflow;
using NexNet.Logging;
using NexNet.Transports;

#pragma warning disable CA1416

namespace NexNet.Websocket;

internal class WebsocketTransportListener : ITransportListener
{
    private readonly WebsocketServerConfig _config;

    private readonly BufferBlock<WebsocketAcceptedConnection> _connectionQueue;

    private WebsocketTransportListener(WebsocketServerConfig config, 
        BufferBlock<WebsocketAcceptedConnection> connectionQueue)
    {
        _config = config;
        _connectionQueue = connectionQueue;
    }

    public ValueTask CloseAsync(bool linger)
    {
        _connectionQueue.Complete();
        _config.IsAccepting = false;
        // Since this is connecting through the Asp webserver, we can't stop it from listening, but we
        // can ignore connections made to this nexus.
        return default;
    }


    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        WebsocketAcceptedConnection? connection = null;
        try
        {
            connection = await _connectionQueue.ReceiveAsync(cancellationToken);

            return WebsocketTransport.CreateFromConnection(connection.Pipe);
        }
        catch (OperationCanceledException)
        {
            // noop.
        }
        catch (InvalidOperationException e)
        {
            // The listener queue is empty and closed.
            _config.Logger?.LogDebug(e, "Listener queue is closed and will no longer receive new connections.");
        }
        catch (Exception e)
        {
            _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");

            // Immediate disconnect.
            if (connection != null)
                await connection.Pipe.CompleteAsync(WebSocketCloseStatus.Empty);
        }

        return null;
    }

    public static ITransportListener Create(
        WebsocketServerConfig config,
        BufferBlock<WebsocketAcceptedConnection> connectionQueue)
    {
        return new WebsocketTransportListener(config, connectionQueue);
    }
}
