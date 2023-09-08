﻿using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes;

/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusChannelReaderUnmanaged<T> : NexusChannelReader<T>
    where T : unmanaged
{
    // ReSharper disable once StaticMemberInGenericType
    private static readonly int _tSize;

    static unsafe NexusChannelReaderUnmanaged()
    {
        _tSize = sizeof(T);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusChannelReaderUnmanaged(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {

    }

    internal NexusChannelReaderUnmanaged(NexusPipeReader reader)
        : base(reader)
    {
    }

    /// <summary>
    /// Asynchronously reads data from the duplex pipe.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A task that represents the asynchronous read operation. The value of the TResult parameter contains an enumerable collection of type T.
    /// If the read operation is completed or canceled, the returned task will contain an empty collection.
    /// </returns>
    public override async ValueTask<IReadOnlyList<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return EmptyList;

        List?.Clear();

        // Read the data from the pipe reader.
        var result = await Reader.ReadAtLeastAsync(_tSize, cancellationToken).ConfigureAwait(false);

        // Check if the result is completed or canceled.
        if (result.IsCompleted && result.Buffer.Length == 0)
            return EmptyList;

        if (result.IsCanceled)
            return EmptyList;
            
        Read(result.Buffer, Reader, List!);

        return List!;
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.</param>
    /// <returns>An enumerable collection of type T.</returns>
    private static void Read(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<T> list)
    {
        var length = buffer.Length;
        
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
            
        while ((length - reader.Consumed) >= _tSize)
        {
            list.Add(reader.ReadValue<T>());
        }
            
        pipeReader.AdvanceTo(reader.Consumed);
    }
}
