﻿using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

#pragma warning disable CA1416

namespace NexNet.Asp.WebSocket;

internal class WebSocketTransportListener : ITransportListener
{
    private readonly WebSocketServerConfig _config;

    private readonly BufferBlock<IWebSocketPipe> _connectionQueue;

    public WebSocketTransportListener(WebSocketServerConfig config, 
        BufferBlock<IWebSocketPipe> connectionQueue)
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
        IWebSocketPipe? pipe = null;
        try
        {
            pipe = await _connectionQueue.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            return WebSocketTransport.CreateFromConnection(pipe);
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
                await pipe.CompleteAsync(WebSocketCloseStatus.Empty).ConfigureAwait(false);
        }

        return null;
    }
}
