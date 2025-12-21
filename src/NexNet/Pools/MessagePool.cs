using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Pools;

/// <summary>
/// High-performance message pool using hybrid thread-local + shared pool strategy.
/// Handles both same-thread and cross-thread rent/return patterns efficiently.
/// </summary>
/// <typeparam name="T">Message type to pool.</typeparam>
internal class MessagePool<T> : IPooledMessage
    where T : class, IMessageBase, new()
{
    /// <summary>
    /// Thread-local single-slot cache for zero-contention fast path.
    /// </summary>
    [ThreadStatic]
    private static T? _threadLocal;

    /// <summary>
    /// Shared pool for cross-thread returns and overflow.
    /// </summary>
    private static readonly ConcurrentBag<T> _sharedPool = new();

    /// <summary>
    /// Approximate count of items in the shared pool.
    /// </summary>
    private static int _sharedPoolCount;

    /// <summary>
    /// Maximum items to retain in the shared pool.
    /// </summary>
    internal const int MaxSharedPoolSize = 256;

    /// <summary>
    /// Rents a message from the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        // Fast path: try thread-local slot first (no contention)
        var item = _threadLocal;
        if (item != null)
        {
            _threadLocal = null;
            item.MessageCache = this;
            return item;
        }

        // Slow path: try shared pool
        if (_sharedPool.TryTake(out item))
        {
            Interlocked.Decrement(ref _sharedPoolCount);
            item.MessageCache = this;
            return item;
        }

        // Create new instance
        item = new T();
        item.MessageCache = this;
        return item;
    }

    /// <summary>
    /// Deserializes a message from the buffer, renting from pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(in ReadOnlySequence<byte> bodySequence)
    {
        // Fast path: try thread-local slot first
        var item = _threadLocal;
        if (item != null)
        {
            _threadLocal = null;
        }
        else if (_sharedPool.TryTake(out item))
        {
            Interlocked.Decrement(ref _sharedPoolCount);
        }
        else
        {
            item = new T();
        }

        item.MessageCache = this;
        MemoryPackSerializer.Deserialize(bodySequence, ref item);
        return item;
    }

    /// <summary>
    /// Deserializes a message and returns as interface.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence)
    {
        // Fast path: try thread-local slot first
        T? item = _threadLocal;
        if (item != null)
        {
            _threadLocal = null;
        }
        else if (_sharedPool.TryTake(out item))
        {
            Interlocked.Decrement(ref _sharedPoolCount);
        }
        else
        {
            item = new T();
        }

        item.MessageCache = this;
        MemoryPackSerializer.Deserialize(bodySequence, ref item);
        return item!;
    }

    /// <summary>
    /// Returns a message to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(IMessageBase? item)
    {
        if (item == null)
            return;

        var typedItem = Unsafe.As<T>(item);

        // Fast path: try thread-local slot first (no contention)
        if (_threadLocal == null)
        {
            _threadLocal = typedItem;
            return;
        }

        // Slow path: use shared pool with size limit
        if (Interlocked.Increment(ref _sharedPoolCount) <= MaxSharedPoolSize)
        {
            _sharedPool.Add(typedItem);
        }
        else
        {
            Interlocked.Decrement(ref _sharedPoolCount);
            // Let GC handle it - pool is at capacity
        }
    }

    /// <summary>
    /// Clears the shared pool. Thread-local items are not cleared.
    /// </summary>
    public void Clear()
    {
        while (_sharedPool.TryTake(out _))
        {
            Interlocked.Decrement(ref _sharedPoolCount);
        }
    }
}
