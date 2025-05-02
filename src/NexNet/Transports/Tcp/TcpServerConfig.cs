using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Configurations for the server to allow connections from TCP NexNet clients.
/// </summary>
public class TcpServerConfig : ServerConfig
{
    /// <summary>
    /// dual mode socket
    /// </summary>
    /// <remarks>
    /// Specifies whether the Socket is a dual-mode socket used for both IPv4 and IPv6.
    /// Will work only if socket is bound on IPv6 address.
    /// </remarks>
    public bool DualMode { get; set; }
    /// <summary>
    /// keep alive
    /// </summary>
    /// <remarks>
    /// This option will setup SO_KEEPALIVE if the OS support this feature
    /// </remarks>
    public bool KeepAlive { get; set; }
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
    /// reuse address
    /// </summary>
    /// <remarks>
    /// This option will enable/disable SO_REUSEADDR if the OS support this feature
    /// </remarks>
    public bool ReuseAddress { get; set; }
    /// <summary>
    /// enables a socket to be bound for exclusive access
    /// </summary>
    /// <remarks>
    /// This option will enable/disable SO_EXCLUSIVEADDRUSE if the OS support this feature
    /// </remarks>
    public bool ExclusiveAddressUse { get; set; }

    /// <summary>
    /// Endpoint to bind to.
    /// </summary>
    public required EndPoint EndPoint { get; set; }

    /// <summary>
    /// Sets the server mode.
    /// </summary>
    public TcpServerConfig()
        : base(ServerConnectionMode.Listener)
    {
        
    }
    
    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return new ValueTask<ITransportListener?>(SocketTransportListener.Create(this, EndPoint, SocketType.Stream, ProtocolType.Tcp));
    }

}
