using NexNet.Internals;

namespace NexNet.Invocation;


/// <summary>
/// Base context for client hubs to use.
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
        get => _proxy ??= CacheManager.ProxyCache.Rent(Session!, ProxyInvocationMode.Caller, null);
    }

    internal ClientSessionContext(INexNetSession<TProxy> session)
        : base(session)
    {
    }


    internal override void Reset()
    {
        if (_proxy != null)
        {
            CacheManager.ProxyCache.Return(_proxy);
            _proxy = null;
        }

    }
}
