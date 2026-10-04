using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Pipes;

/// <summary>
/// Reads typed items from a duplex pipe. Each item is exactly one MessagePack value (or, with the MemoryPack
/// payload format and no explicit formatter, one MemoryPack value).
/// </summary>
/// <typeparam name="T">The type of data to be read from the duplex pipe.</typeparam>
/// <remarks>
/// Incomplete items are detected without exceptions: the reader probes each item with
/// <see cref="MsgPackReader.TrySkip"/> and only deserializes once the item is known to be complete.
/// </remarks>
internal class NexusChannelReader<T> : INexusChannelReader<T>
{
    internal readonly NexusPipeReader Reader;

    // Null only with the MemoryPack payload format when no explicit formatter was supplied.
    private readonly NexusFormatter<T>? _formatter;
    private readonly NexusSerializerOptions _options;

    /// <inheritdoc/>
    public bool IsComplete => Reader.IsCompleted;

    /// <inheritdoc/>
    public long BufferedLength => Reader.BufferedLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReader{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusChannelReader(INexusDuplexPipe pipe)
        : this(pipe.ReaderCore, null, GetOptions(pipe))
    {
    }

    /// <summary>
    /// Initializes a reader that always uses the specified NexNet formatter (used for internal protocol unions).
    /// </summary>
    internal NexusChannelReader(INexusDuplexPipe pipe, NexusFormatter<T> formatter)
        : this(pipe.ReaderCore, formatter, GetOptions(pipe))
    {
    }

    internal NexusChannelReader(NexusPipeReader reader, NexusFormatter<T>? formatter = null, NexusSerializerOptions? options = null)
    {
        Reader = reader;
        _options = options ?? NexusSerializerOptions.Untrusted;
#if NEXNET_MEMORYPACK
        _formatter = formatter;
#else
        _formatter = formatter ?? NexusFormatterRegistry.Get<T>();
#endif
    }

    private static NexusSerializerOptions? GetOptions(INexusDuplexPipe pipe)
        => (pipe as NexusDuplexPipe)?.Session?.Config.SerializerOptions;

    /// <inheritdoc/>
    public virtual async ValueTask<bool> ReadAsync<TTo>(List<TTo> list, Converter<T, TTo>? converter, CancellationToken cancellationToken = default)
    {
        if (IsComplete && BufferedLength == 0)
            return false;

        // Read the data from the pipe reader.
        while (true)
        {
            var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            // Check if the result is completed or canceled.
            if (result.IsCompleted && result.Buffer.Length == 0)
                return false;

            if (result.IsCanceled)
                return false;

            var readAmount = _formatter != null
                ? ReadMessagePack(result.Buffer, list, converter)
                : ReadMemoryPack(result.Buffer, Reader, list, converter);

            if (result.IsCompleted && readAmount == 0)
                return false;

            if (list.Count == 0)
                continue;

            return true;
        }
    }

    /// <summary>
    /// Reads all complete items from the buffer. Each item is probed with <see cref="MsgPackReader.TrySkip"/> before
    /// it is deserialized, so a partial trailing item never throws.
    /// </summary>
    private long ReadMessagePack<TTo>(
        ReadOnlySequence<byte> buffer,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
        var reader = new MsgPackReader(buffer, _options);
        long consumed = 0;
        var formatter = _formatter!;

        while (true)
        {
            var probe = reader; // snapshot
            if (!probe.TrySkip())
                break; // incomplete item: wait for more data

            T? item = default;
            formatter.Deserialize(ref reader, ref item);

            // A formatter must consume exactly one value.
            if (reader.Consumed != probe.Consumed)
                throw new NexusSerializationException("Channel formatter did not consume exactly one MessagePack value.");

            list.Add(converter == null ? Unsafe.As<T, TTo>(ref item!) : converter(item!));
            consumed = reader.Consumed;
        }

        var remaining = buffer.Length - consumed;
        if (remaining > _options.MaxBufferedItemSize)
            throw NexusSerializationException.LengthExceedsRemaining(remaining, _options.MaxBufferedItemSize);

        // Everything was examined: if an item is incomplete, the next read waits for more data instead of spinning.
        Reader.AdvanceToExamined(consumed, buffer.Length);
        return consumed;
    }

    /// <summary>
    /// Reads data with MemoryPack (legacy payload format).
    /// </summary>
    private static int ReadMemoryPack<TTo>(
        ReadOnlySequence<byte> buffer,
        NexusPipeReader pipeReader,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
#if NEXNET_MEMORYPACK
        var length = buffer.Length;

        using var readerState = MemoryPack.MemoryPackReaderOptionalStatePool.Rent(MemoryPack.MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPack.MemoryPackReader(buffer, readerState);
        int consumedLength = 0;
        int examinedLength = 0;
        while ((length - reader.Consumed) > 0)
        {
            try
            {
                // If the converter is null, read the value directly. Otherwise read the value and convert it.
                list.Add(converter == null
                    ? reader.ReadValue<TTo>()!
                    : converter.Invoke(reader.ReadValue<T>()!));
                consumedLength = reader.Consumed;
                examinedLength = reader.Consumed;
            }
            catch
            {
                // Incomplete message data - mark position and wait for more data
                examinedLength = reader.Consumed;
                break;
            }
        }

        if (consumedLength > 0)
        {
            pipeReader.AdvanceTo(consumedLength, examinedLength);
        }
        else if (examinedLength > 0)
        {
            // When deserialization fails, we need to examine the data but not consume it
            // This tells the pipe reader we looked at the data but need more bytes
            pipeReader.AdvanceTo(0, examinedLength);
        }

        return consumedLength;
#else
        throw new InvalidOperationException("MemoryPack payloads are not available in this build.");
#endif
    }

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        var list = ListPool<T>.Rent();

        while (true)
        {
            var previousBufferLength = BufferedLength;

            if (IsComplete && previousBufferLength == 0)
                break;

            list.Clear();
            var readResult = await ReadAsync(list, null, cancellationToken)
                .ConfigureAwait(false);

            if (readResult == false && previousBufferLength == BufferedLength)
                break;

            if (list.Count > 0)
            {
                foreach (var item in list)
                    yield return item;
            }
        }

        // Return the list to the pool.
        ListPool<T>.Return(list);
    }
}
