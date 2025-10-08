using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes.Channels;





// ============================================================================
// Core Interfaces
// ============================================================================

/// <summary>
/// Base interface for all union messages with pooling support
/// </summary>
public interface INexusPooledMessage<TSelf> where TSelf : INexusPooledMessage<TSelf>, new()
{
    /// <summary>Unique identifier for this message type within its union</summary>
    static abstract byte UnionId { get; }
    
    /// <summary>Rent an instance from the pool</summary>
    static abstract TSelf Rent();
    
    /// <summary>Return this instance to the pool</summary>
    void Return();
}

/// <summary>
/// Marker interface for union groups that automatically registers message types
/// </summary>
public interface INexusMessageUnion<TUnion>
    where TUnion : INexusMessageUnion<TUnion>
{
    /// <summary>Register all message types for this union</summary>
    static abstract void RegisterMessages(UnionBuilder<TUnion> registerer);
}


public sealed class UnionBuilder<TUnion> where TUnion : INexusMessageUnion<TUnion>
{
    private readonly Dictionary<byte, UnionEntry<TUnion>> _entries = new();
    
    public void Add<TMessage>() 
        where TMessage : TUnion, INexusPooledMessage<TMessage>, new()
    {
        byte id = TMessage.UnionId;
        if (_entries.ContainsKey(id))
            throw new InvalidOperationException($"UnionId {id} already registered");
        
        _entries[id] = new UnionEntry<TUnion>(TMessage.Rent, typeof(TMessage));
    }
    
    internal FrozenDictionary<byte, UnionEntry<TUnion>> Build()
        => _entries.ToFrozenDictionary();
}

public record UnionEntry<TUnion>(Func<TUnion> Renter, Type Type)
    where TUnion : INexusMessageUnion<TUnion>;
// ============================================================================
// Union Registry
// ============================================================================

/// <summary>
/// Central registry for union message types with AOT-friendly design
/// </summary>
public static class UnionRegistry<TUnion> 
    where TUnion : INexusMessageUnion<TUnion>
{

    private static readonly FrozenDictionary<byte, UnionEntry<TUnion>> Unions;
    
    static UnionRegistry()
    {
        var registerer = new UnionBuilder<TUnion>();
        TUnion.RegisterMessages(registerer);
        Unions = registerer.Build();
    }
    
    /// <summary>
    /// Rent a message by its type
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMessage Rent<TMessage>() 
        where TMessage : TUnion, INexusPooledMessage<TMessage>, new()
    {
        return TMessage.Rent();
    }
    
    /// <summary>
    /// Rent a message by its UnionId, returns as TUnion base type
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TUnion Rent(byte unionId)
    {
        if (!Unions.TryGetValue(unionId, out var factory))
        {
            throw new InvalidOperationException(
                $"UnionId {unionId} is not registered for union {typeof(TUnion).Name}");
        }
        
        return factory.Renter();
    }
    
    /// <summary>
    /// Get metadata about registered messages
    /// </summary>
    public static IReadOnlyDictionary<byte, UnionEntry<TUnion>> GetRegisteredMessages()
    {
        return Unions;
    }
    
    /// <summary>
    /// Check if a UnionId is registered
    /// </summary>
    public static bool IsRegistered(byte unionId)
    {
        return Unions.ContainsKey(unionId);
    }
    
    /// <summary>
    /// Get the type associated with a UnionId
    /// </summary>
    public static Type? GetMessageType(byte unionId)
    {
        return Unions.TryGetValue(unionId, out var type) ? type.Type : null;
    }
}


/// <summary>
/// Thread-safe object pool for message instances
/// </summary>
public static class NexusMessagePool<TMessage> 
    where TMessage : INexusPooledMessage<TMessage>, new()
{
    private static readonly ConcurrentBag<TMessage> _pool = new();
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


// ============================================================================
// Example Usage
// ============================================================================

// Define a union group
public abstract class NetworkMessageUnion : INexusMessageUnion<NetworkMessageUnion>
{
    public static void RegisterMessages(UnionBuilder<NetworkMessageUnion> registerer)
    {
        registerer.Add<LoginMessage>();
        registerer.Add<ChatMessage>();
        registerer.Add<DisconnectMessage>();
    }
}


[MemoryPackable]
public partial class LoginMessage : NetworkMessageUnion, INexusPooledMessage<LoginMessage>
{
    public static byte UnionId => 1;
    
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    
    public static LoginMessage Rent() => NexusMessagePool<LoginMessage>.Rent();
    public void Return() => NexusMessagePool<LoginMessage>.Return(this);
}

[MemoryPackable]
public partial class ChatMessage : NetworkMessageUnion, INexusPooledMessage<ChatMessage>
{
    public static byte UnionId => 2;
    
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    
    public static ChatMessage Rent() => NexusMessagePool<ChatMessage>.Rent();
    public void Return() => NexusMessagePool<ChatMessage>.Return(this);
}

[MemoryPackable]
public partial class DisconnectMessage : NetworkMessageUnion, INexusPooledMessage<DisconnectMessage>
{
    public static byte UnionId => 3;
    
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
    
    public static DisconnectMessage Rent() => NexusMessagePool<DisconnectMessage>.Rent();
    public void Return() => NexusMessagePool<DisconnectMessage>.Return(this);
}

public class StandAloneMessage : NexusBasePooledMessage<StandAloneMessage>
{
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
}

public abstract class NexusBasePooledMessage<T> : INexusPooledMessage<T>
    where T : NexusBasePooledMessage<T>, INexusPooledMessage<T>, new()
{
    // Not used as this is not a union.
    public static byte UnionId => 0;
    public static T Rent() => NexusMessagePool<T>.Rent();
    public void Return() => NexusMessagePool<T>.Return(Unsafe.As<T>(this));
}



/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusPooledUnionMessageChannelReader<T> : NexusPooledMessageChannelReaderBase<T>
    where T : INexusMessageUnion<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusPooledUnionMessageChannelReader(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {

    }

    internal NexusPooledUnionMessageChannelReader(NexusPipeReader reader)
        : base(reader)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override bool ParseValues(List<T> list, MemoryPackReader reader)
    {
        var value = UnionRegistry<T>.Rent(reader.ReadUnmanaged<byte>());
        reader.ReadValue(ref value);
                
        if(value == null)
            return false;
                
        list.Add(value);
        return true;
    }
}

/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusPooledMessageChannelReader<T> : NexusPooledMessageChannelReaderBase<T>
    where T : NexusBasePooledMessage<T>, INexusPooledMessage<T>, new()
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NexusPooledMessageChannelReader{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusPooledMessageChannelReader(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {

    }

    internal NexusPooledMessageChannelReader(NexusPipeReader reader)
        : base(reader)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override bool ParseValues(List<T> list, MemoryPackReader reader)
    {
        var value = T.Rent();
        reader.ReadValue(ref value);
                
        if(value == null)
            return false;
                
        list.Add(value);
        return true;
    }
}



/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal abstract class NexusPooledMessageChannelReaderBase<T> : NexusChannelReader<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusPooledMessageChannelReaderBase(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {

    }

    internal NexusPooledMessageChannelReaderBase(NexusPipeReader reader)
        : base(reader)
    {
    }

    public override async ValueTask<bool> ReadAsync<TTo>(
        List<TTo> list, Converter<T, TTo>? converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return false;
        
        if(converter != null)
            throw new InvalidOperationException("Can't convert on pooled message reader.");
        
        if(typeof(TTo) != typeof(T))
            throw new InvalidOperationException("Reader type and TTo must be the same as conversions are not allowed.");

        var tList = Unsafe.As<List<T>>(list);

        // Read the data from the pipe reader.
        var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        
        var bufferLength = result.Buffer.Length;
        // Check if the result is completed or canceled.
        if (result.IsCompleted && bufferLength == 0)
            return false;

        if (result.IsCanceled)
            return false;
        
        
        var buffer = result.Buffer;
        var length = buffer.Length;
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
        int consumed = 0;
        try
        {
            while (reader.Remaining > 0)
            {     
                // Set the consumed prior to any work as the work may fail.
                consumed = reader.Consumed;
                
                if (!ParseValues(tList, reader))
                    break;
            }
        }
        catch (MemoryPackSerializationException)
        {
            // Reached the end of the buffer. 
        }
        
        Reader.AdvanceTo(consumed, (int)length);

        // There is left over data in the buffer that is less than the size of T.
        // Nothing can be done with this data, so return false.
        if (result.IsCompleted && consumed == 0)
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected abstract bool ParseValues(List<T> list, MemoryPackReader reader);
}
