using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Pools;

/// <summary>
/// Pool for CancellationTokenSource instances.
/// Only pools sources that can be reset.
/// </summary>
internal class CancellationTokenSourcePool
{
    private readonly ConcurrentBag<CancellationTokenSource> _pool = new();
    private int _poolCount;

    /// <summary>
    /// Maximum items to retain in the pool.
    /// </summary>
    internal const int MaxPoolSize = 64;

    /// <summary>
    /// Rents a CancellationTokenSource from the pool.
    /// </summary>
    public CancellationTokenSource Rent()
    {
        if (_pool.TryTake(out var cts))
        {
            Interlocked.Decrement(ref _poolCount);
            return cts;
        }
        return new CancellationTokenSource();
    }

    /// <summary>
    /// Returns a CancellationTokenSource to the pool.
    /// Only pools if the source can be reset.
    /// </summary>
    public void Return(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        // Only pool if it can be reset (not cancelled, not disposed)
        if (!cts.TryReset())
            return;

        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(cts);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }

    /// <summary>
    /// Clears all pooled items.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
