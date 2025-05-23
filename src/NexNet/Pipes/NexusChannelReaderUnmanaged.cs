using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    public override async ValueTask<bool> ReadAsync<TTo>(List<TTo> list, Converter<T, TTo>? converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return false;

        // Read the data from the pipe reader.
        var result = await Reader.ReadAtLeastAsync(_tSize, cancellationToken).ConfigureAwait(false);

        var bufferLength = result.Buffer.Length;
        // Check if the result is completed or canceled.
        if (result.IsCompleted && bufferLength == 0)
            return false;

        if (result.IsCanceled)
            return false;

        var consumed = Read(result.Buffer, Reader, list, converter);

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Read<TTo>(
        ReadOnlySequence<byte> buffer,
        NexusPipeReader pipeReader,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
        var length = buffer.Length;
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);

        while ((length - reader.Consumed) >= _tSize)
        {
            list.Add(converter == null 
                ? reader.ReadValue<TTo>()! 
                : converter.Invoke(reader.ReadValue<T>()!));
        }

        pipeReader.AdvanceTo(reader.Consumed, reader.Consumed);

        return reader.Consumed;
    }
}
