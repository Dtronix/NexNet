using NexNet.Invocation;

namespace NexNet.Pools;

/// <summary>
/// Pool manager for session-specific pools including proxy pool.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
internal class SessionPoolManager<TProxy> : PoolManager
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Pool for proxy instances.
    /// </summary>
    internal readonly ProxyPool<TProxy> ProxyPool;

    /// <summary>
    /// Initializes the session pool manager.
    /// </summary>
    public SessionPoolManager()
    {
        ProxyPool = new ProxyPool<TProxy>();
    }

    /// <inheritdoc />
    public override void Clear()
    {
        ProxyPool.Clear();
        base.Clear();
    }
}
