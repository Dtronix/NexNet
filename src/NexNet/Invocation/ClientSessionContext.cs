using NexNet.Internals;

namespace NexNet.Invocation;

public sealed class ClientSessionContext<TProxy> : SessionContext<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private TProxy? _proxy;
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
