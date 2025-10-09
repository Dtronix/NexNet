using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Thread-safe object pool for message instances that implements efficient reuse of message objects
/// to reduce garbage collection pressure and improve performance in high-throughput scenarios.
/// </summary>
/// <typeparam name="TMessage">
/// The message type to pool. Must be a reference type that implements <see cref="INexusPooledMessage{TMessage}"/>
/// and <see cref="IMemoryPackable{TMessage}"/>, and has a parameterless constructor.
/// </typeparam>
internal static class NexusMessagePool<TMessage>
    where TMessage : class, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    private static readonly ConcurrentBag<TMessage> _pool = new();
    // ReSharper disable once StaticMemberInGenericType
    private static int _poolCount;
    private const int MaxPoolSize = 1000; // Prevent unbounded growth
    
    /// <summary>
    /// Gets the current number of messages available in the pool.
    /// </summary>
    public static int PoolCount => _poolCount;
    
    /// <summary>
    /// Retrieves a message instance from the pool, or creates a new one if the pool is empty.
    /// </summary>
    /// <returns>
    /// A message instance ready for use. The caller is responsible for calling <see cref="Return"/>
    /// when finished to return the instance to the pool.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMessage Rent()
    {
        if (_pool.TryTake(out var instance))
        {
            Interlocked.Decrement(ref _poolCount);
            return instance;
        }

        return new TMessage();
    }
    
    /// <summary>
    /// Returns a message instance to the pool for future reuse.
    /// </summary>
    /// <param name="instance">The message instance to return to the pool.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(TMessage instance)
    {
        // Prevent pool from growing too large
        if (_poolCount < MaxPoolSize)
        {
            _pool.Add(instance);
            Interlocked.Increment(ref _poolCount);
        }
    }
    
    /// <summary>
    /// Clears all pooled instances from the pool.
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
