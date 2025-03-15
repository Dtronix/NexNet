using System.Net;
using System.Net.Security;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Http;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.Websocket;

/// <summary>
/// Configurations for the client to connect to a QUIC NexNet server.
/// </summary>
public class WebsocketClientConfig : ClientConfig
{
    /// <summary>
    /// Endpoint
    /// </summary>
    public required Uri Url { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return WebsocketTransport.ConnectAsync(this, cancellationToken);
    }
}

internal record WebsocketAcceptedConnection(HttpContext Context, IWebSocketPipe Pipe);

/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class WebsocketServerConfig : ServerConfig
{
    
    private string _path ="/ws";
    
    private readonly BufferBlock<WebsocketAcceptedConnection> _connectionQueue = new();
    internal bool IsAccepting = true;
    public string Path
    {
        get => _path;
        set => _path = value;
    }

    public async Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (IsAccepting 
            && context.WebSockets.IsWebSocketRequest 
            && context.Request.Path.Value == _path)
        {
            using var websocket = await context.WebSockets.AcceptWebSocketAsync();
            using var pipe = new SimpleWebSocketPipe(websocket, new WebSocketPipeOptions()
            {
                CloseWhenCompleted = true,
            });

            int count = 1;
            // Loop until we enqueue the connection.
            while(!_connectionQueue.Post(new WebsocketAcceptedConnection(context, pipe)))
            {
                Logger?.LogInfo($"Failed to post connection to queue {count++} times.");
            }

            await pipe.RunAsync(context.RequestAborted);
        }
        else
        {
            await next(context);
        }
    }

    /// <param name="cancellationToken"></param>
    /// <inheritdoc />
    protected override ValueTask<ITransportListener> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(WebsocketTransportListener.Create(this, _connectionQueue));
    }
}
