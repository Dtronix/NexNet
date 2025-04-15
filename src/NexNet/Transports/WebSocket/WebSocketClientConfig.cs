using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// Configurations for the client to connect to a WebSocket NexNet server.
/// </summary>
public class WebSocketClientConfig : HttpClientConfig
{
    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return WebSocketTransport.ConnectAsync(this, cancellationToken);
    }
}
