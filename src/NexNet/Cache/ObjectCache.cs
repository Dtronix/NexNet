using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Cache;

internal static class ObjectCache<T> where T : class, new()
{
    // The pool of available objects
    private static readonly ConcurrentBag<T> _pool = new();
    private static int _poolCount = 0;

    /// <summary>
    /// Maximum number of objects to keep in the pool.
    /// </summary>
    internal const int MaxPoolSize = 128;

    /// <summary>
    /// Get the current pool size (for diagnostics).
    /// </summary>
    public static int PoolCount => Volatile.Read(ref _poolCount);

    /// <summary>
    /// Rent an instance of T from the cache.
    /// If none are available, a new one is constructed via its parameterless ctor.
    /// </summary>
    public static T Rent()
    {
        if (_pool.TryTake(out var item))
        {
            Interlocked.Decrement(ref _poolCount);
            return item;
        }
        return new T();
    }

    /// <summary>
    /// Return an instance to the cache for reuse.
    /// </summary>
    public static void Return(T item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        // Only pool if under limit to prevent unbounded growth
        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(item);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
            // Let GC handle it - pool is at capacity
        }
    }

    /// <summary>
    /// Clear out all pooled items (e.g. to release references).
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
