using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports.HttpSocket;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// Configurations for the client to connect to a QUIC NexNet server.
/// </summary>
public class HttpSocketClientConfig : ClientConfig
{
    /// <summary>
    /// Endpoint
    /// </summary>
    public required Uri Url { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return HttpSocketTransport.ConnectAsync(this, cancellationToken);
    }
}
