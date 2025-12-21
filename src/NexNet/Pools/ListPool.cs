using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace NexNet.Pools;

/// <summary>
/// Static pool for List&lt;T&gt; instances with capacity management.
/// </summary>
/// <typeparam name="T">Element type of the list.</typeparam>
internal static class ListPool<T>
{
    private static readonly ConcurrentBag<List<T>> _pool = new();
    private static int _poolCount;

    /// <summary>
    /// Maximum number of lists to keep in the pool.
    /// </summary>
    internal const int MaxPoolSize = 128;

    /// <summary>
    /// Maximum capacity to retain when returning a list.
    /// Lists larger than this will be trimmed.
    /// </summary>
    internal const int MaxRetainedCapacity = 1024;

    /// <summary>
    /// Default capacity for new or trimmed lists.
    /// </summary>
    internal const int DefaultCapacity = 64;

    /// <summary>
    /// Empty list singleton.
    /// </summary>
    public static IReadOnlyList<T> Empty { get; } = new List<T>(0);

    /// <summary>
    /// Gets the current pool count (for diagnostics).
    /// </summary>
    public static int PoolCount => Volatile.Read(ref _poolCount);

    /// <summary>
    /// Rents a list from the pool.
    /// </summary>
    public static List<T> Rent()
    {
        if (_pool.TryTake(out var list))
        {
            Interlocked.Decrement(ref _poolCount);
            return list;
        }
        return new List<T>();
    }

    /// <summary>
    /// Returns a list to the pool.
    /// </summary>
    public static void Return(List<T>? list)
    {
        if (list == null)
            return;

        list.Clear();

        // Trim oversized lists to prevent memory bloat
        if (list.Capacity > MaxRetainedCapacity)
        {
            list.Capacity = DefaultCapacity;
        }

        // Only pool if under limit
        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(list);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }

    /// <summary>
    /// Clears all pooled lists.
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
