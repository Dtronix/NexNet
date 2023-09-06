using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Pipes;

namespace NexNet;


/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// This structure is optimized for performance when working with unmanaged types.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="NexNet.INexusDuplexPipe"/> for reading data.
/// </remarks>
public class NexusChannelReaderUnmanaged<T>
    where T : unmanaged
{
    private readonly NexusPipeReader _reader;

    // ReSharper disable once StaticMemberInGenericType
    private static readonly int _tSize;
    private readonly List<T> _list;

    static unsafe NexusChannelReaderUnmanaged()
    {
        _tSize = sizeof(T);
    }


    internal NexusChannelReaderUnmanaged(NexusPipeReader reader)
    {
        _reader = reader;
        _list = new List<T>();
    }


    /// <summary>
    /// Asynchronously reads data from the duplex pipe.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A task that represents the asynchronous read operation. The value of the TResult parameter contains an enumerable collection of type T.
    /// If the read operation is completed or canceled, the returned task will contain an empty collection.
    /// </returns>
    public async ValueTask<IEnumerable<T>> ReadAsync(CancellationToken cancellationToken)
    {
        // Read the data from the pipe reader.
        var result = await _reader.ReadAtLeastAsync(_tSize, cancellationToken);

        // Check if the result is completed or canceled.
        if (result.IsCompleted || result.IsCanceled)
        {
            return Enumerable.Empty<T>();
        }

        return Read(result.Buffer, _reader, _list);
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <returns>An enumerable collection of type T.</returns>
    private static IEnumerable<T> Read(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<T> list)
    {
        var length = buffer.Length;
        
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
            
        while ((length - reader.Consumed) > _tSize)
        {
            list.Add(reader.ReadUnmanaged<T>());
        }
            
        pipeReader.AdvanceTo(reader.Consumed);
        return list;
    }
}
