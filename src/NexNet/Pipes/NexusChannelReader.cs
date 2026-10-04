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

    // Largest item read so far. While the buffer holds well over this many bytes, items are deserialized without
    // probing first (see ReadMessagePack).
    private long _largestItem;

    // Extra bytes required beyond twice the largest item before an item is read without probing.
    private const int OptimisticSlack = 16;

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
    /// Reads all complete items from the buffer without ever failing on a partial trailing item.
    /// </summary>
    /// <remarks>
    /// Near the end of the buffer each item's length is probed first (the TrySkip walk, without consuming), so an
    /// incomplete item just waits for more data. While the remaining bytes exceed twice the largest item seen so
    /// far, items are deserialized directly. If such an item turns out to be larger and fails, the reader is
    /// restored and the item is probed: an incomplete item waits for more data, a complete one is malformed and the
    /// error is rethrown. That fallback needs an item more than twice the largest seen, so it happens at most about
    /// log2(<see cref="NexusSerializerOptions.MaxBufferedItemSize"/>) times per channel.
    /// </remarks>
    private long ReadMessagePack<TTo>(
        ReadOnlySequence<byte> buffer,
        List<TTo> list,
        Converter<T, TTo>? converter)
    {
        var reader = new MsgPackReader(buffer, _options);
        var bufferLength = buffer.Length;
        long offset = 0; // position of the reader's start within buffer (changes only after a failed optimistic read)
        long consumed = 0;
        var formatter = _formatter!;
        var largest = _largestItem;
        var optimisticLimit = largest > 0 ? bufferLength - (2 * largest + OptimisticSlack) : -1;

        while (consumed < bufferLength)
        {
            T? item = default;
            if (consumed <= optimisticLimit)
            {
                try
                {
                    formatter.Deserialize(ref reader, ref item);
                }
                catch (NexusSerializationException)
                {
                    // Larger than any item so far: it may just be incomplete. Restart at the item and probe it.
                    offset = consumed;
                    reader = new MsgPackReader(buffer.Slice(consumed), _options);
                    if (!reader.TryGetNextValueLength(out _))
                        break; // incomplete item: wait for more data

                    throw;
                }
            }
            else
            {
                // An incomplete item stops the loop: wait for more data.
                if (!reader.TryGetNextValueLength(out var length))
                    break;

                formatter.Deserialize(ref reader, ref item);

                // A formatter must consume exactly one value.
                if (offset + reader.Consumed - consumed != length)
                    throw new NexusSerializationException("Channel formatter did not consume exactly one MessagePack value.");
            }

            var end = offset + reader.Consumed;
            if (end - consumed > largest)
            {
                largest = end - consumed;
                optimisticLimit = bufferLength - (2 * largest + OptimisticSlack);
            }

            list.Add(converter == null ? Unsafe.As<T, TTo>(ref item!) : converter(item!));
            consumed = end;
        }

        _largestItem = largest;

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
