using NexNet.Internals;

namespace NexNet.Invocation;


/// <summary>
/// Base context for client nexuses to use.
/// </summary>
/// <typeparam name="TProxy">Proxy class used for invocation.</typeparam>
public sealed class ClientSessionContext<TProxy> : SessionContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private TProxy? _proxy;

    /// <summary>
    /// Proxy used to invoke methods on the server.
    /// </summary>
    public TProxy Proxy
    {
        get => _proxy ??= CacheManager.ProxyCache.Rent(
            Session,
            SessionManager,
            Session.CacheManager,
            ProxyInvocationMode.Caller,
            null);
    }

    internal ClientSessionContext(INexusSession<TProxy> session)
        : base(session, null)
    {
    }


    /// <inheritdoc />
    public override void Reset()
    {
        if (_proxy != null)
        {
            CacheManager.ProxyCache.Return(_proxy);
            _proxy = null;
        }

    }
}
