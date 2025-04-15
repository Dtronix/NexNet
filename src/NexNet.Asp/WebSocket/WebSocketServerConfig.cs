using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

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
