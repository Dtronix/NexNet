using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NexNet.Logging;
using NexNet.Transports.HttpSocket;

#pragma warning disable CA1416

namespace NexNet.Transports.Asp.HttpSocket;

internal class HttpSocketTransportListener : ITransportListener
{
    private readonly HttpSocketServerConfig _config;

    private readonly BufferBlock<HttpSocketAcceptedConnection> _connectionQueue;

    private HttpSocketTransportListener(HttpSocketServerConfig config, 
        BufferBlock<HttpSocketAcceptedConnection> connectionQueue)
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
        HttpSocketAcceptedConnection? connection = null;
        try
        {
            connection = await _connectionQueue.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            return HttpSocketTransport.CreateFromConnection(connection.Pipe);
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
                await connection.Pipe.CompleteAsync().ConfigureAwait(false);
        }

        return null;
    }

    public static ITransportListener Create(
        HttpSocketServerConfig config,
        BufferBlock<HttpSocketAcceptedConnection> connectionQueue)
    {
        return new HttpSocketTransportListener(config, connectionQueue);
    }
}
