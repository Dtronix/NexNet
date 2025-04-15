using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket;

/// <summary>
/// Configurations for the client to connect to a HttpSocket NexNet server.
/// </summary>
public class HttpSocketClientConfig : HttpClientConfig
{
    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return HttpSocketTransport.ConnectAsync(this, cancellationToken);
    }
}
