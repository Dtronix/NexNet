using System.Net;
using System.Net.Security;
using NexNet.Transports;

namespace NexNet.Quic;

/// <summary>
/// Configurations for the client to connect to a QUIC NexNet server.
/// </summary>
public class QuicClientConfig : ClientConfig
{

    private SslClientAuthenticationOptions _sslClientAuthenticationOptions = null!;

    /// <summary>
    /// Authentication options for the client to connect to the server.  Must be set.
    /// </summary>
    public SslClientAuthenticationOptions SslClientAuthenticationOptions
    {
        get => _sslClientAuthenticationOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _sslClientAuthenticationOptions = value;
        }
    }
    /// <summary>
    /// Endpoint
    /// </summary>
    public required EndPoint EndPoint { get; set; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        return QuicTransport.ConnectAsync(this, cancellationToken);
    }
}

/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class QuicServerConfig : ServerConfig
{

    private SslServerAuthenticationOptions _sslServerAuthenticationOptions = null!;

    /// <summary>
    /// Authentication options for the incoming client connection.  Must be set.
    /// </summary>
    public SslServerAuthenticationOptions SslServerAuthenticationOptions
    {
        get => _sslServerAuthenticationOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _sslServerAuthenticationOptions = value;
        }
    }

    /// <summary>
    /// Endpoint to bind to.
    /// </summary>
    public required IPEndPoint EndPoint { get; set; }


    /// <param name="cancellationToken"></param>
    /// <inheritdoc />
    protected override ValueTask<ITransportListener> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return QuicTransportListener.Create(this, cancellationToken);
    }
}
