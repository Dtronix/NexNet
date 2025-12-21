using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace NexNet.Pools;

/// <summary>
/// Thread-safe pool of StringBuilder instances to reduce allocations.
/// </summary>
internal static class StringBuilderPool
{
    private const int MaxPoolSize = 100;
    private const int MaxBuilderCapacity = 1024;
    private const int DefaultCapacity = 256;

    private static readonly ConcurrentBag<StringBuilder> _pool = new();
    private static int _poolCount;

    /// <summary>
    /// Rents a StringBuilder from the pool or creates a new one.
    /// </summary>
    public static StringBuilder Rent()
    {
        if (_pool.TryTake(out var sb))
        {
            Interlocked.Decrement(ref _poolCount);
            return sb;
        }

        return new StringBuilder(DefaultCapacity);
    }

    /// <summary>
    /// Returns a StringBuilder to the pool for reuse.
    /// </summary>
    public static void Return(StringBuilder? sb)
    {
        if (sb == null)
            return;

        // Don't pool builders that have grown too large
        if (sb.Capacity > MaxBuilderCapacity)
            return;

        // Don't exceed max pool size
        if (Interlocked.Increment(ref _poolCount) > MaxPoolSize)
        {
            Interlocked.Decrement(ref _poolCount);
            return;
        }

        sb.Clear();
        _pool.Add(sb);
    }

    /// <summary>
    /// Clears all pooled StringBuilders.
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
