using NexNet.Cache;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

public abstract class SessionContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal INexNetSession<TProxy> Session { get; }
    internal SessionCacheManager<TProxy> CacheManager => Session.CacheManager;

    /// <summary>
    /// Store for this session used to keep and pass variables for the lifetime of this session.
    /// </summary>
    public SessionStore Store => Session.SessionStore!;

    /// <summary>
    /// Id of the current session.
    /// </summary>
    public long Id => Session.Id;

    internal SessionContext(INexNetSession<TProxy> session)
    {
        Session = session;
    }

    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    public void Disconnect()
    {
        Session.DisconnectAsync(DisconnectReason.DisconnectFromHub);
    }

    internal abstract void Reset();
}
