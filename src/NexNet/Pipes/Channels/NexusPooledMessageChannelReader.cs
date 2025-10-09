using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="TUnion">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusPooledUnionMessageChannelReader<TUnion> : NexusPooledMessageChannelReaderBase<TUnion>
    where TUnion : class, INexusPooledMessageUnion<TUnion>
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
    protected override bool ParseValues(List<TUnion> list, MemoryPackReader reader)
    {
        var value = NexusMessageUnionRegistry<TUnion>.Rent(reader.ReadUnmanaged<byte>());
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
/// <typeparam name="TMessage">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusPooledMessageChannelReader<TMessage> : NexusPooledMessageChannelReaderBase<TMessage>
    where TMessage : NexusBasePooledMessage<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
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
    protected override bool ParseValues(List<TMessage> list, MemoryPackReader reader)
    {
        var value= NexusMessagePool<TMessage>.Rent();
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
