using System.Collections.Concurrent;
using System.Threading;
using NexNet.Internals;
using NexNet.Pipes;

namespace NexNet.Pools;

/// <summary>
/// Pool for NexusPipeManager instances.
/// </summary>
internal class PipeManagerPool
{
    private readonly ConcurrentBag<NexusPipeManager> _pool = new();
    private int _poolCount;

    /// <summary>
    /// Maximum items to retain in the pool.
    /// </summary>
    internal const int MaxPoolSize = 32;

    /// <summary>
    /// Rents a pipe manager from the pool and sets it up.
    /// </summary>
    /// <param name="session">Session to associate with the pipe manager.</param>
    /// <returns>Configured pipe manager.</returns>
    public NexusPipeManager Rent(INexusSession session)
    {
        NexusPipeManager manager;
        if (_pool.TryTake(out var pooled))
        {
            Interlocked.Decrement(ref _poolCount);
            manager = pooled;
        }
        else
        {
            manager = new NexusPipeManager();
        }

        manager.Setup(session);
        return manager;
    }

    /// <summary>
    /// Returns a pipe manager to the pool.
    /// </summary>
    public void Return(NexusPipeManager? manager)
    {
        if (manager == null)
            return;

        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(manager);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }

    /// <summary>
    /// Clears all pooled managers.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
