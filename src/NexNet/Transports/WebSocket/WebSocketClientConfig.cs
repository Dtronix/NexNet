using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// Configurations for the client to connect to a QUIC NexNet server.
/// </summary>
public class WebSocketClientConfig : ClientConfig
{
    /// <summary>
    /// Endpoint
    /// </summary>
    public required Uri Url { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return WebSocketTransport.ConnectAsync(this, cancellationToken);
    }
}
