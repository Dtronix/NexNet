using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;

#pragma warning disable CA1416

namespace NexNet.Asp.HttpSocket;

internal class HttpSocketTransportListener : ITransportListener
{
    private readonly HttpSocketServerConfig _config;

    private readonly BufferBlock<HttpSocketDuplexPipe> _connectionQueue;

    private HttpSocketTransportListener(HttpSocketServerConfig config, 
        BufferBlock<HttpSocketDuplexPipe> connectionQueue)
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
        HttpSocketDuplexPipe? pipe = null;
        try
        {
            pipe = await _connectionQueue.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            
            return new HttpSocketTransport(pipe);
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
            if (pipe != null)
                await pipe.CompleteAsync().ConfigureAwait(false);
        }

        return null;
    }

    public static ITransportListener Create(
        HttpSocketServerConfig config,
        BufferBlock<HttpSocketDuplexPipe> connectionQueue)
    {
        return new HttpSocketTransportListener(config, connectionQueue);
    }
}
