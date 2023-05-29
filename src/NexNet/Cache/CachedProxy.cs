using System;
using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.Invocation;

namespace NexNet.Cache;

internal class CachedProxy<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ConcurrentBag<TProxy> _cache = new();

    public TProxy Rent(
        INexNetSession<TProxy> session,
        SessionManager sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_cache.TryTake(out var proxy))
            proxy = new TProxy() { CacheManager = session.CacheManager };

        proxy.Configure(session, sessionManager, mode, modeArguments);


        return proxy;
    }

    public void Return(TProxy item)
    {
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
