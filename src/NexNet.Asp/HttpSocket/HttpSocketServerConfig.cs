using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Http;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;

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
