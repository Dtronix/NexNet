using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Configurations for the client to connect to a TLS TCP NexNet server.
/// </summary>
public sealed class TcpTlsClientConfig : TcpClientConfig
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
    /// Number of milliseconds to timeout a SSL connection attempt.  This occurs between the connection is
    /// initially established and the connection starts TLS communication.
    /// </summary>
    public int SslConnectionTimeout { get; set; } = 5000;

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport()
    {
        return TcpTlsTransport.ConnectAsync(this, EndPoint, SocketType.Stream, ProtocolType.Tcp);
    }
}

/// <summary>
/// Configurations for the server to allow connections from TLS TCP NexNet clients.
/// </summary>
public sealed class TcpTlsServerConfig : TcpServerConfig
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
    /// Number of milliseconds to timeout a SSL connection attempt.  This occurs between the connection is
    /// initially established and the connection starts TLS communication.
    /// </summary>
    public int SslConnectionTimeout { get; set; } = 5000;

    /// <inheritdoc />
    protected override ITransportListener OnCreateServerListener()
    {
        return TcpTlsTransportListener.Create(this, EndPoint, SocketType.Stream, ProtocolType.Tcp);
    }
}
