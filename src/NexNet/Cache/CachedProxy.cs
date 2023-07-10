using System;
using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.Invocation;

namespace NexNet.Cache;

internal class CachedProxy<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ConcurrentBag<TProxy> _cache = new();

    /// <summary>
    /// Rents a proxy class.
    /// </summary>
    /// <param name="session">Session for this proxy.  Used in all cases except from the external nexus context.</param>
    /// <param name="sessionManager">Reference to the session manager.  Not used from client invocations.</param>
    /// <param name="sessionCache">Cache for the sessions.</param>
    /// <param name="mode">Mode to set this proxy to.</param>
    /// <param name="modeArguments">Arguments to pass for this invocation mode.</param>
    /// <returns></returns>
    public TProxy Rent(
        INexusSession<TProxy>? session,
        SessionManager? sessionManager,
        SessionCacheManager<TProxy> sessionCache,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        if (!_cache.TryTake(out var proxy))
            proxy = new TProxy() { CacheManager = sessionCache };

        proxy.Configure(session, sessionManager, mode, modeArguments);


        return proxy;
    }

    /// <summary>
    /// Returns the proxy for reuse.
    /// </summary>
    /// <param name="proxy">Proxy to return.</param>
    public void Return(TProxy proxy)
    {
        _cache.Add(proxy);
    }

    /// <summary>
    /// Clears the cache.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
    }
}
