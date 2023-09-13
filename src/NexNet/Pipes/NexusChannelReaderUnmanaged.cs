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

    /// <inheritdoc/>
    public override ValueTask<IReadOnlyList<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync(static (in T t) => t, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<TTo>> ReadAsync<TTo>(Converter<T, TTo> converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return ListPool<TTo>.Empty;

        var list = ListPool<TTo>.Rent();

        // Read the data from the pipe reader.
        var result = await Reader.ReadAtLeastAsync(_tSize, cancellationToken).ConfigureAwait(false);

        // Check if the result is completed or canceled.
        if (result.IsCompleted && result.Buffer.Length == 0)
            return ListPool<TTo>.Empty;

        if (result.IsCanceled)
            return ListPool<TTo>.Empty;

        Read(result.Buffer, Reader, list, converter);

        return list;
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.</param>
    /// <returns>An enumerable collection of type T.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Read(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<T> list)
    {
        Read(buffer, pipeReader, list, static (in T t) => t);
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
    private static void Read<TTo>(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<TTo> list, Converter<T, TTo> converter)
    {
        var length = buffer.Length;

        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);

        while ((length - reader.Consumed) >= _tSize)
        {
            list.Add(converter.Invoke(reader.ReadValue<T>()!));
        }

        pipeReader.AdvanceTo(reader.Consumed);
    }
}
