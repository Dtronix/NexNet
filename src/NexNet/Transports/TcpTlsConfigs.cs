using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace NexNet.Transports;

public sealed class TcpTlsClientConfig : TcpClientConfig
{
    public SslClientAuthenticationOptions SslClientAuthenticationOptions { get; set; }
    public int SslConnectionTimeout { get; set; } = 50000;

    public TcpTlsClientConfig()
        : base()
    {
    }

    public override ValueTask<ITransportBase> ConnectTransport()
    {
        return TcpTlsTransport.ConnectAsync(this);
    }
}

public sealed class TcpTlsServerConfig : TcpServerConfig
{
    public SslServerAuthenticationOptions SslServerAuthenticationOptions { get; set; }
    public int SslConnectionTimeout { get; set; } = 50000;

    public TcpTlsServerConfig()
        : base()
    {
    }

    public override ValueTask<ITransportBase> CreateTransport(Socket socket)
    {
        return TcpTlsTransport.CreateFromSocket(socket, this);
    }
}
