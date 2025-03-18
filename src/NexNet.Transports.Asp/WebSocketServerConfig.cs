﻿using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Http;

namespace NexNet.Transports.WebSocket.Asp;
internal record WebSocketAcceptedConnection(HttpContext Context, IWebSocketPipe Pipe);
/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class WebSocketServerConfig : ServerConfig
{
    private string _path ="/ws";
    
    internal readonly BufferBlock<WebSocketAcceptedConnection> ConnectionQueue = new();
    internal bool IsAccepting = true;
    
    /// <summary>
    /// Path that the NexNet server binds to on the host.
    /// </summary>
    public string Path
    {
        get => _path;
        set => _path = value;
    }
    
    /// <param name="cancellationToken"></param>
    /// <inheritdoc />
    protected override ValueTask<ITransportListener> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(WebsocketTransportListener.Create(this, ConnectionQueue));
    }
}
