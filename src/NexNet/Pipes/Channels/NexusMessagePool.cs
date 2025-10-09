using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Thread-safe object pool for message instances
/// </summary>
internal static class NexusMessagePool<TMessage> 
    where TMessage : class, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    private static readonly ConcurrentBag<TMessage> _pool = new();
    // ReSharper disable once StaticMemberInGenericType
    private static int _poolCount;
    private const int MaxPoolSize = 1000; // Prevent unbounded growth
    
    public static int PoolCount => _poolCount;
    
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
    /// Clear all pooled instances
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
