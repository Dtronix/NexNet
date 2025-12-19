using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace NexNet.Cache;

internal static class ListPool<T>
{
    private static readonly ConcurrentBag<List<T>> _pool = new ConcurrentBag<List<T>>();
    private static int _poolCount = 0;

    /// <summary>
    /// Maximum number of lists to keep in the pool.
    /// </summary>
    internal const int MaxPoolSize = 128;

    /// <summary>
    /// Maximum capacity to retain when returning a list to the pool.
    /// Lists larger than this will be trimmed to DefaultCapacity.
    /// </summary>
    internal const int MaxRetainedCapacity = 1024;

    /// <summary>
    /// Default capacity to reset oversized lists to.
    /// </summary>
    internal const int DefaultCapacity = 64;

    public static IReadOnlyList<T> Empty { get; } = new List<T>(0);

    /// <summary>
    /// Get the current pool size (for diagnostics).
    /// </summary>
    public static int PoolCount => Volatile.Read(ref _poolCount);

    public static List<T> Rent()
    {
        if (_pool.TryTake(out var list))
        {
            Interlocked.Decrement(ref _poolCount);
            return list;
        }
        return new List<T>();
    }

    public static void Return(List<T> list)
    {
        if (list == null)
            return;

        list.Clear();

        // Trim oversized lists to prevent memory bloat, but retain reasonable capacity
        // Do NOT set Capacity = 0 as it defeats pooling by forcing reallocation
        if (list.Capacity > MaxRetainedCapacity)
        {
            list.Capacity = DefaultCapacity;
        }

        // Only pool if under limit to prevent unbounded growth
        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(list);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
            // Let GC handle it - pool is at capacity
        }
    }

    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
