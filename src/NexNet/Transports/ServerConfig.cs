using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base configuration file for servers.
/// </summary>
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

    /// <summary>
    /// Set to true to authenticate teh client connection.
    /// </summary>
    public bool Authenticate { get; set; } = false;

    /// <summary>
    /// Creates the listener and starts.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Listener interface.</returns>
    protected abstract ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken);

    internal ValueTask<ITransportListener?> CreateServerListener(CancellationToken cancellationToken)
    {
        return OnCreateServerListener(cancellationToken);
    }

    internal Action? InternalOnConnect;

}
