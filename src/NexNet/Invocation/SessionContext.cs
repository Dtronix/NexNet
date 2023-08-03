using System.IO.Pipelines;
using System.Threading.Tasks;
using System;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Base context for hubs to use.
/// </summary>
/// <typeparam name="TProxy">Proxy class used for invocation.</typeparam>
public abstract class SessionContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal INexusSession<TProxy> Session { get; }
    internal SessionManager? SessionManager { get; }
    internal SessionCacheManager<TProxy> CacheManager => Session.CacheManager;

    /// <summary>
    /// Logger.
    /// </summary>
    public INexusLogger? Logger => Session.Logger;

    /// <summary>
    /// Store for this session used to keep and pass variables for the lifetime of this session.
    /// </summary>
    public SessionStore Store => Session.SessionStore!;

    /// <summary>
    /// Id of the current session.
    /// </summary>
    public long Id => Session.Id;

    internal SessionContext(INexusSession<TProxy> session, SessionManager? sessionManager)
    {
        Session = session;
        SessionManager = sessionManager;
    }

    /// <summary>
    /// Creates a pipe for use with the current session.
    /// </summary>
    /// <returns>Pipe for communication over teh current session.</returns>
    public INexusDuplexPipe CreatePipe()
    {
        return Session.PipeManager.GetPipe() 
               ?? throw new InvalidOperationException("Can't create a pipe due to session being closed.");
    }

    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    public Task DisconnectAsync()
    {
        return Session.DisconnectAsync(DisconnectReason.Graceful);
    }

    internal abstract void Reset();
}
