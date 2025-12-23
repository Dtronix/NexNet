using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Invocation;
using NexNet.RateLimiting;

namespace NexNet.Transports;

/// <summary>
/// Base configuration file for servers.
/// </summary>
public abstract class ServerConfig : ConfigBase
{
    
    /// <summary>
    /// Gets the connection mode that the server is to operate in.
    /// </summary>
    internal ServerConnectionMode ConnectionMode { get; }
    
    /// <summary>
    /// Option: acceptor backlog size
    /// </summary>
    /// <remarks>
    /// This option will set the listening socket's backlog size
    /// </remarks>
    public int AcceptorBacklog { get; init; } = 20;

    /// <summary>
    /// Set to true to authenticate teh client connection.
    /// </summary>
    public bool Authenticate { get; set; } = false;

    /// <summary>
    /// Configuration for connection rate limiting. Null = disabled (default).
    /// </summary>
    public ConnectionRateLimitConfig? RateLimiting { get; set; }

    /// <summary>
    /// Creates the listener and starts.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Listener interface.</returns>
    protected abstract ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken);

    internal Action? InternalOnConnect = null;

    /// <summary>
    /// Base configuration class for servers, providing options for the server connection
    /// </summary>
    protected ServerConfig(ServerConnectionMode connectionMode)
    {
        ConnectionMode = connectionMode;
    }

    internal ValueTask<ITransportListener?> CreateServerListener(CancellationToken cancellationToken)
    {
        return OnCreateServerListener(cancellationToken);
    }

    /// <summary>
    /// Gets the session manager, creating a default LocalServerSessionManager if none is configured.
    /// </summary>
    /// <returns>The session manager instance.</returns>
    internal IServerSessionManager GetSessionManager()
    {
        return new LocalServerSessionManager();
    }
}
