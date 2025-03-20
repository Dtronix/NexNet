using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Http;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNet.Asp.WebSocket;
/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class WebSocketServerConfig : ServerConfig
{
    private string _path ="/ws";
    
    internal readonly BufferBlock<IWebSocketPipe> ConnectionQueue = new();
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
        return ValueTask.FromResult<ITransportListener>(new WebSocketTransportListener(this, ConnectionQueue));
    }

    public bool PushNewConnectionAsync(IWebSocketPipe pipe)
    {
        int count = 1;
        // Loop until we enqueue the connection.
        while(!ConnectionQueue.Post(pipe))
        {
            Logger?.LogTrace($"Failed to post connection to queue {count++} times.");
            if (ConnectionQueue.Completion.IsCompleted)
            {
                Logger?.LogTrace($"Connection queue is closed.");
                return false;
            }
        }
        return true;
    }
}
