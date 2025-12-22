using System.Threading.Tasks;
using System;
using NexNet.Internals;
using NexNet.Pools;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Logging;

namespace NexNet.Invocation;

/// <summary>
/// Base context for hubs to use.
/// </summary>
/// <typeparam name="TProxy">Proxy class used for invocation.</typeparam>
public abstract class SessionContext<TProxy> : ISessionContext
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal INexusSession<TProxy> Session { get; }
    internal IServerSessionManager? SessionManager { get; }
    internal SessionPoolManager<TProxy> PoolManager => Session.PoolManager;

    /// <summary>
    /// Logger.
    /// </summary>
    public INexusLogger? Logger => Session.Logger;

    /// <summary>
    /// Store for this session used to keep and pass variables for the lifetime of this session.
    /// </summary>
    public SessionStore Store => Session.SessionStore;

    /// <summary>
    /// Id of the current session.
    /// </summary>
    public long Id => Session.Id;

    /// <summary>
    /// The remote address of the connected client.
    /// For IP-based transports, this is the IP address.
    /// For Unix Domain Sockets, this may be null or the socket path.
    /// When behind a proxy with TrustProxyHeaders enabled, this is the original client IP.
    /// </summary>
    public string? RemoteAddress => Session.RemoteAddress;

    /// <summary>
    /// The remote port of the connected client.
    /// Null for transports that don't use ports (e.g., Unix Domain Sockets).
    /// </summary>
    public int? RemotePort => Session.RemotePort;

    internal SessionContext(INexusSession<TProxy> session, IServerSessionManager? sessionManager)
    {
        Session = session;
        SessionManager = sessionManager;
    }

    /// <summary>
    /// Creates a pipe for use with the current session.
    /// </summary>
    /// <returns>Pipe for communication over teh current session.</returns>
    public IRentedNexusDuplexPipe CreatePipe()
    {
        return Session.PipeManager.RentPipe() 
               ?? throw new InvalidOperationException("Can't create a pipe due to session being closed.");
    }

    /// <summary>
    /// Creates an unmanaged duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of unmanaged data to be transmitted through the channel.</typeparam>
    /// <returns>An instance of <see cref="INexusDuplexUnmanagedChannel{T}"/> that allows for bidirectional communication of unmanaged data.</returns>
    /// <remarks>
    /// This method is optimized for unmanaged types and should be used over the non-unmanaged version when possible.
    /// </remarks>
    public INexusDuplexUnmanagedChannel<T> CreateUnmanagedChannel<T>()
        where T : unmanaged
    {
        return CreatePipe().GetUnmanagedChannel<T>();
    }


    /// <summary>
    /// Creates a duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of data to be transmitted through the channel.</typeparam>
    /// <returns>An instance of <see cref="INexusDuplexChannel{T}"/> that allows for bidirectional communication of data.</returns>
    /// <remarks>
    /// This method creates a channel from a rented pipe and is suitable for any MessagePack data type.
    /// </remarks>
    public INexusDuplexChannel<T> CreateChannel<T>()
    {
        return CreatePipe().GetChannel<T>();
    }

    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    public Task DisconnectAsync()
    {
        return Session.DisconnectAsync(DisconnectReason.Graceful);
    }

    Task ISessionContext.DisconnectAsync(DisconnectReason reason)
    {
        return Session.DisconnectAsync(reason);
    }

    /// <inheritdoc />
    public abstract void Reset();
}
