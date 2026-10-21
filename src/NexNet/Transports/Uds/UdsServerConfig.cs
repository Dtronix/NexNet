using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.Uds;

/// <summary>
/// Configurations for the server to allow connections from Unix Domain Socket (UDS) NexNet clients.
/// </summary>
public sealed class UdsServerConfig : ServerConfig
{
    /// <inheritdoc />
    protected internal override string TransportName => "uds";

    private UnixDomainSocketEndPoint _endPoint = null!;

    /// <summary>
    /// Endpoint to bind to.
    /// </summary>
    public UnixDomainSocketEndPoint EndPoint
    {
        get => _endPoint;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _endPoint = value;
        }
    }

    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return new ValueTask<ITransportListener?>(SocketTransportListener.Create(this, EndPoint, SocketType.Stream, ProtocolType.IP));
    }
    
    /// <summary>
    /// Sets the server mode.
    /// </summary>
    public UdsServerConfig()
        : base(ServerConnectionMode.Listener)
    {
        
    }
}
