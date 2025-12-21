using System.Collections.Concurrent;
using System.Threading;
using NexNet.Internals;
using NexNet.Invocation;

namespace NexNet.Pools;

/// <summary>
/// Pool for proxy invocation instances.
/// </summary>
/// <typeparam name="TProxy">Proxy type.</typeparam>
internal class ProxyPool<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ConcurrentBag<TProxy> _pool = new();
    private int _poolCount;

    /// <summary>
    /// Maximum items to retain in the pool.
    /// </summary>
    internal const int MaxPoolSize = 64;

    /// <summary>
    /// Rents a proxy from the pool and configures it.
    /// </summary>
    /// <param name="session">Session for this proxy.</param>
    /// <param name="sessionManager">Reference to the session manager.</param>
    /// <param name="sessionPool">Pool manager for the session.</param>
    /// <param name="mode">Invocation mode.</param>
    /// <param name="modeArguments">Arguments for the invocation mode.</param>
    /// <returns>Configured proxy instance.</returns>
    public TProxy Rent(
        INexusSession<TProxy>? session,
        IServerSessionManager? sessionManager,
        SessionPoolManager<TProxy> sessionPool,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        TProxy proxy;
        if (_pool.TryTake(out var pooledProxy))
        {
            Interlocked.Decrement(ref _poolCount);
            proxy = pooledProxy;
        }
        else
        {
            proxy = new TProxy { PoolManager = sessionPool };
        }

        proxy.Configure(session, sessionManager, mode, modeArguments);
        return proxy;
    }

    /// <summary>
    /// Returns a proxy to the pool.
    /// </summary>
    public void Return(TProxy? proxy)
    {
        if (proxy == null)
            return;

        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(proxy);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }

    /// <summary>
    /// Clears all pooled proxies.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
