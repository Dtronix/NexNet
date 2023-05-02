using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NexNet.Transports;

public sealed class UdsClientConfig : ClientConfig
{
    private readonly UnixDomainSocketEndPoint _endPoint = null!;
    public required UnixDomainSocketEndPoint EndPoint
    {
        get => _endPoint;
        init
        {
            SocketEndPoint = _endPoint = value;
        }
    }

    public UdsClientConfig()
    {
        SocketAddressFamily = AddressFamily.Unix;
        SocketType = SocketType.Stream;
        SocketProtocolType = ProtocolType.IP;
    }

    public override ValueTask<ITransportBase> ConnectTransport()
    {
        return SocketTransport.ConnectAsync(this);
    }
}

public sealed class UdsServerConfig : ServerConfig
{
    private readonly UnixDomainSocketEndPoint _endPoint = null!;
    public required UnixDomainSocketEndPoint EndPoint
    {
        get => _endPoint;
        init
        {
            SocketEndPoint = _endPoint = value;
        }
    }

    public UdsServerConfig()
    {
        SocketAddressFamily = AddressFamily.Unix;
        SocketType = SocketType.Stream;
        SocketProtocolType = ProtocolType.IP;
    }
    public override ValueTask<ITransportBase> CreateTransport(Socket socket)
    {
        return SocketTransport.CreateFromSocket(socket, this);
    }

}
