using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Transports;

public abstract class ServerConfig : ConfigBase
{
    /// <summary>
    /// Option: acceptor backlog size
    /// </summary>
    /// <remarks>
    /// This option will set the listening socket's backlog size
    /// </remarks>
    public int AcceptorBacklog { get; init; } = 20;



    /// <summary>
    /// If a client hasn't sent a full "HelloMessage" within this time the client will be disconnected.
    /// </summary>
    public int HandshakeTimeout { get; init; } = 15_000;

    public bool Authenticate { get; set; } = false;

    internal Action? InternalOnConnect;

    public abstract ValueTask<ITransportBase> CreateTransport(Socket socket);

}
