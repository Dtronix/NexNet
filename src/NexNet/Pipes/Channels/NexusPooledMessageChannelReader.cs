using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes.Channels;

public class NexusPoolMessageMapItem<T>(byte Id, Func<T> RentMessage)
    where T : INexusPooledMessage<T>
{
    public byte Id { get; init; } = Id;
    public Func<T> RentMessage { get; init; } = RentMessage;
    
}

public interface INexusPooledMessage<T>
    where T : INexusPooledMessage<T>
{
    static abstract IEnumerable<NexusPoolMessageMapItem<T>> GetUnionMap();

    public void Return() => 
}

public class msg : INexusPooledMessage<msg>
{
    public static IEnumerable<NexusPoolMessageMapItem<msg>> GetUnionMap()
    {
        return [new (0, () => new msg())];
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
internal class NexusPooledMessageChannelReader<T> : NexusChannelReader<T>
    where T : INexusPooledMessage<T>
{
    private static FrozenDictionary<byte, Func<T>> _map;
    private static Func<T>? _singleMap;
    static NexusPooledMessageChannelReader()
    {
        _map = T.GetUnionMap().ToFrozenDictionary(k => k.Id, e => e.RentMessage);
        _singleMap = _map.Count == 1 ? _map.First().Value : null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
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

    public override async ValueTask<bool> ReadAsync<TTo>(List<TTo> list, Converter<T, TTo>? converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return false;

        // Read the data from the pipe reader.
        var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        
        var bufferLength = result.Buffer.Length;
        // Check if the result is completed or canceled.
        if (result.IsCompleted && bufferLength == 0)
            return false;

        if (result.IsCanceled)
            return false;
        
        var consumed = _singleMap == null
            ? ReadUnion(result.Buffer, Reader, list, converter)
            : Read(result.Buffer, Reader, list, converter);

        // There is left over data in the buffer that is less than the size of T.
        // Nothing can be done with this data, so return false.
        if (result.IsCompleted && consumed == 0)
            return false;

        return true;
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.  Will append the </param>
    /// <param name="converter">An optional converter used to convert the data.</param>
    /// <returns>The consumed amount of bytes from the buffer.</returns>
    private static int Read<TTo>(
        ReadOnlySequence<byte> buffer,
        NexusPipeReader pipeReader,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
        var length = buffer.Length;
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
        
        while ((length - reader.Consumed) >= 0)
        {
            var type = _map[reader.ReadUnmanaged<byte>()].Invoke();
            reader.ReadValue(ref type);
            
            list.Add(converter == null 
                ? reader.ReadValue<TTo>()! 
                : converter.Invoke(reader.ReadValue<T>(!));
        }

        pipeReader.AdvanceTo(reader.Consumed, reader.Consumed);

        return reader.Consumed;
    }
    
    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.  Will append the </param>
    /// <param name="converter">An optional converter used to convert the data.</param>
    /// <returns>The consumed amount of bytes from the buffer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUnion<TTo>(
        ReadOnlySequence<byte> buffer,
        NexusPipeReader pipeReader,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
        var length = buffer.Length;
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
        
        
        
        reader.Advance(1);

        while ((length - reader.Consumed) >= 0)
        {
            var type = _map[reader.ReadUnmanaged<byte>()].Invoke();
            reader.ReadValue(ref type);
            
            list.Add(converter == null 
                ? reader.ReadValue<TTo>()! 
                : converter.Invoke(reader.ReadValue<T>(!));
        }

        pipeReader.AdvanceTo(reader.Consumed, reader.Consumed);

        return reader.Consumed;
    }
}
