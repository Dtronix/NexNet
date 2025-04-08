using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Configurations for the server to allow connections from HttpSockeet NexNet clients.
/// </summary>
public class HttpSocketServerConfig : AspServerConfig
{
    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<ITransportListener?>(null);
    }
}
