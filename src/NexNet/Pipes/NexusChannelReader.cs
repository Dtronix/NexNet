using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Pipes;

/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusChannelReader<T> : INexusChannelReader<T>
{
    internal readonly NexusPipeReader Reader;

    /// <inheritdoc/>
    public bool IsComplete => Reader.IsCompleted;

    /// <inheritdoc/>
    public long BufferedLength => Reader.BufferedLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusChannelReader(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {
    }

    internal NexusChannelReader(NexusPipeReader reader)
    {
        Reader = reader;
    }

    /// <inheritdoc/>
    public virtual ValueTask<IReadOnlyList<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync(static (in T input) => input, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual async ValueTask<IReadOnlyList<TTo>> ReadAsync<TTo>(Converter<T, TTo> converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return ListPool<TTo>.Empty;

        var list = ListPool<TTo>.Rent();

        // Read the data from the pipe reader.
        while (true)
        {
            var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            // Check if the result is completed or canceled.
            if (result.IsCompleted && result.Buffer.Length == 0)
                return ListPool<TTo>.Empty;

            if (result.IsCanceled)
                return ListPool<TTo>.Empty;

            var readAmount = Read<TTo>(result.Buffer, Reader, list, converter);

            if (result.IsCompleted && readAmount == 0)
                return ListPool<TTo>.Empty;

            if (list.Count == 0)
                continue;

            return list;
        }
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.</param>
    /// <param name="converter">The converter used to convert the data.</param>
    /// <returns>An enumerable collection of type T.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Read<TTo>(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<TTo> list, Converter<T, TTo> converter)
    {
        var length = buffer.Length;

        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
        int successfulConsumedCount = 0;
        while ((length - reader.Consumed) > 0)
        {
            try
            {
                list.Add(converter.Invoke(reader.ReadValue<T>()!));
                successfulConsumedCount = reader.Consumed;
            }
            catch
            {
                break;
            }
        }

        if (successfulConsumedCount > 0)
        {
            pipeReader.AdvanceTo(successfulConsumedCount);
        }

        return successfulConsumedCount;
    }
}
