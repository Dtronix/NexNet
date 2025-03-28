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
    private string _path = null!;

    private readonly BufferBlock<IWebSocketPipe> _connectionQueue = new();
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
        return ValueTask.FromResult<ITransportListener>(new WebSocketTransportListener(this, _connectionQueue));
    }

    /// <summary>
    /// Pushes the newly accepted pipe connection to the Nexus server for handling.
    /// </summary>
    /// <param name="pipe">Pipe to have the Nexus server handle.</param>
    /// <returns>
    /// True if the connection was successfully added to the queue.  False if the queue has closed.
    /// Usually means the server has closed.
    /// </returns>
    public bool PushNewConnectionAsync(IWebSocketPipe pipe)
    {
        int count = 1;
        // Loop until we enqueue the connection.
        while(!_connectionQueue.Post(pipe))
        {
            Logger?.LogTrace($"Failed to post connection to queue {count++} times.");
            if (_connectionQueue.Completion.IsCompleted)
            {
                Logger?.LogTrace($"Connection queue is closed.");
                return false;
            }
        }
        return true;
    }
}
