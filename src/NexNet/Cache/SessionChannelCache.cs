using NexNet.Invocation;

namespace NexNet.Cache;

internal class SessionCacheManager<TProxy> : CacheManager
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal readonly CachedProxy<TProxy> ProxyCache;

    public SessionCacheManager()
    {
        ProxyCache = new CachedProxy<TProxy>();
    }

    public override void Clear()
    {
        ProxyCache.Clear();
        base.Clear();
    }
}
