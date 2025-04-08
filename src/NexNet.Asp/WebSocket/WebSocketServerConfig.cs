using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNet.Asp.WebSocket;
/// <summary>
/// Configurations for the server to allow connections from WebSocket NexNet clients.
/// </summary>
public class WebSocketServerConfig : AspServerConfig
{
   
    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<ITransportListener?>(null);
    }

}
