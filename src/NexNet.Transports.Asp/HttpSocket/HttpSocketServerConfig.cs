using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Http;
using NexNet.Logging;
using NexNet.Transports.HttpSocket;

namespace NexNet.Transports.Asp.HttpSocket;

internal record HttpSocketAcceptedConnection(HttpContext Context, HttpSocketDuplexPipe Pipe);

/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class HttpSocketServerConfig : ServerConfig
{
    private string _path ="/ws";
    
    internal readonly BufferBlock<HttpSocketAcceptedConnection> ConnectionQueue = new();
    internal bool IsAccepting = true;
    
    /// <summary>
    /// Path that the NexNet server binds to on the host.
    /// </summary>
    public string Path
    {
        get => _path;
        set => _path = value;
    }

    public void PushNewConnectionAsync(HttpContext context, HttpSocketDuplexPipe pipe)
    {
        int count = 1;
        // Loop until we enqueue the connection.
        while(!ConnectionQueue.Post(new HttpSocketAcceptedConnection(context, pipe)))
        {
            Logger?.LogInfo($"Failed to post connection to queue {count++} times.");
        }
    } 
    
    /// <param name="cancellationToken"></param>
    /// <inheritdoc />
    protected override ValueTask<ITransportListener> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(HttpSocketTransportListener.Create(this, ConnectionQueue));
    }
}
