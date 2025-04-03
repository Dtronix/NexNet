using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Configurations for the client to connect to a TCP NexNet server.
/// </summary>
public class TcpClientConfig : ClientConfig
{
    /// <summary>
    /// dual mode socket
    /// </summary>
    /// <remarks>
    /// Specifies whether the Socket is a dual-mode socket used for both IPv4 and IPv6.
    /// Will work only if socket is bound on IPv6 address.
    /// </remarks>
    public bool TcpDualMode { get; set; }
    /// <summary>
    /// keep alive
    /// </summary>
    /// <remarks>
    /// This option will setup SO_KEEPALIVE if the OS support this feature
    /// </remarks>
    public bool TcpKeepAlive { get; set; }
    /// <summary>
    /// TCP keep alive time
    /// </summary>
    /// <remarks>
    /// The number of seconds a TCP connection will remain alive/idle before keepalive probes are sent to the remote
    /// </remarks>
    public int TcpKeepAliveTime { get; set; } = -1;
    /// <summary>
    /// TCP keep alive interval
    /// </summary>
    /// <remarks>
    /// The number of seconds a TCP connection will wait for a keepalive response before sending another keepalive probe
    /// </remarks>
    public int TcpKeepAliveInterval { get; set; } = -1;
    /// <summary>
    /// TCP keep alive retry count
    /// </summary>
    /// <remarks>
    /// The number of TCP keep alive probes that will be sent before the connection is terminated
    /// </remarks>
    public int TcpKeepAliveRetryCount { get; set; } = -1;

    /// <summary>
    /// no delay
    /// </summary>
    /// <remarks>
    /// This option will enable/disable Nagle's algorithm for TCP protocol
    /// </remarks>
    public bool TcpNoDelay { get; set; } = true;

    /// <summary>
    /// Endpoint
    /// </summary>
    public required EndPoint EndPoint { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return SocketTransport.ConnectAsync(this, EndPoint, SocketType.Stream, ProtocolType.Tcp, cancellationToken);
    }
}
