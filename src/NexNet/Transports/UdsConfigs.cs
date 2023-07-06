using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Configurations for the client to connect to a Unix Domain Socket (UDS) NexNet server.
/// </summary>
public sealed class UdsClientConfig : ClientConfig
{
    private UnixDomainSocketEndPoint _endPoint = null!;

    /// <summary>
    /// Endpoint to connect to.
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
    public override int PipeFlushChunkSize { get; set; } = 1024 * 4;

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport()
    {
        return SocketTransport.ConnectAsync(this, EndPoint, SocketType.Stream, ProtocolType.IP);
    }
}

/// <summary>
/// Configurations for the server to allow connections from Unix Domain Socket (UDS) NexNet clients.
/// </summary>
public sealed class UdsServerConfig : ServerConfig
{
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
    public override int PipeFlushChunkSize { get; set; } = 1024 * 4;

    /// <inheritdoc />
    protected override ITransportListener OnCreateServerListener()
    {
        return SocketTransportListener.Create(this, EndPoint, SocketType.Stream, ProtocolType.IP);
    }
}
