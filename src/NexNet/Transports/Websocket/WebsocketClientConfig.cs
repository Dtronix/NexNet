using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.Websocket;

/// <summary>
/// Configurations for the client to connect to a QUIC NexNet server.
/// </summary>
public class WebsocketClientConfig : ClientConfig
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
