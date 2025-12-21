using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Pools;

/// <summary>
/// Abstract base class for object pools with bounded size.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
internal abstract class PoolBase<T> where T : class
{
    private readonly ConcurrentBag<T> _pool = new();
    private int _poolCount = 0;

    /// <summary>
    /// Maximum number of objects to keep in the pool.
    /// </summary>
    public int MaxSize { get; }

    /// <summary>
    /// Gets the current number of items in the pool (approximate).
    /// </summary>
    public int CurrentCount => Volatile.Read(ref _poolCount);

    /// <summary>
    /// Initializes the pool with the specified maximum size.
    /// </summary>
    /// <param name="maxSize">Maximum number of items to retain in the pool.</param>
    protected PoolBase(int maxSize = 128)
    {
        MaxSize = maxSize;
    }

    /// <summary>
    /// Creates a new instance of the pooled type.
    /// </summary>
    protected abstract T Create();

    /// <summary>
    /// Called when an item is returned to the pool.
    /// Override to perform cleanup (e.g., Reset, Clear).
    /// </summary>
    protected virtual void OnReturn(T item) { }

    /// <summary>
    /// Determines if an item can be returned to the pool.
    /// Override to reject items that shouldn't be pooled.
    /// </summary>
    protected virtual bool CanReturn(T item) => true;

    /// <summary>
    /// Rents an item from the pool or creates a new one.
    /// </summary>
    public T Rent()
    {
        if (_pool.TryTake(out var item))
        {
            Interlocked.Decrement(ref _poolCount);
            return item;
        }
        return Create();
    }

    /// <summary>
    /// Returns an item to the pool for reuse.
    /// </summary>
    public void Return(T? item)
    {
        if (item == null || !CanReturn(item))
            return;

        OnReturn(item);

        // Only pool if under limit to prevent unbounded growth
        if (Interlocked.Increment(ref _poolCount) <= MaxSize)
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
    /// Clears all items from the pool.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
