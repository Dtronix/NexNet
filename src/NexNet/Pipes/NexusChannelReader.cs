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
/// Reads typed items from a duplex pipe. Each item is one MessagePack value preceded by its length
/// (<see cref="ChannelFraming"/>), decoded by the item type's formatter from exactly that many bytes.
/// </summary>
/// <typeparam name="T">The type of data to be read from the duplex pipe.</typeparam>
/// <remarks>
/// An item that breaks the framing (a length over the limit, a malformed prefix, an item that does not decode to
/// exactly its length) resets the pipe; the session stays up. An incomplete item waits for more data; one larger
/// than the pipe's window gets an extra grant from the session budget (docs/internals/flow-control-v2.md, 4.5).
/// </remarks>
internal class NexusChannelReader<T> : INexusChannelReader<T>
{
    internal readonly NexusPipeReader Reader;

    private readonly NexusFormatter<T> _formatter;
    private readonly NexusSerializerOptions _options;

    // Largest item accepted: this side's MaxMessageSize.
    private readonly int _maxItemSize;

    /// <summary>
    /// Limit for readers created without a session (tests and fuzzing).
    /// </summary>
    internal const int DefaultMaxItemSize = 16 * 1024 * 1024;

    /// <inheritdoc/>
    public bool IsComplete => Reader.IsCompleted;

    /// <inheritdoc/>
    public long BufferedLength => Reader.BufferedLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReader{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusChannelReader(INexusDuplexPipe pipe)
        : this(pipe.ReaderCore, null, GetOptions(pipe), GetMaxItemSize(pipe))
    {
    }

    /// <summary>
    /// Initializes a reader that always uses the specified NexNet formatter (used for internal protocol unions).
    /// </summary>
    internal NexusChannelReader(INexusDuplexPipe pipe, NexusFormatter<T> formatter)
        : this(pipe.ReaderCore, formatter, GetOptions(pipe), GetMaxItemSize(pipe))
    {
    }

    internal NexusChannelReader(NexusPipeReader reader, NexusFormatter<T>? formatter = null, NexusSerializerOptions? options = null, int maxItemSize = DefaultMaxItemSize)
    {
        Reader = reader;
        _options = options ?? NexusSerializerOptions.Untrusted;
        _formatter = formatter ?? NexusFormatterRegistry.Get<T>();
        _maxItemSize = maxItemSize;
    }

    private static NexusSerializerOptions? GetOptions(INexusDuplexPipe pipe)
        => (pipe as NexusDuplexPipe)?.Session?.Config.SerializerOptions;

    private static int GetMaxItemSize(INexusDuplexPipe pipe)
        => (pipe as NexusDuplexPipe)?.Session?.Config.MaxMessageSize ?? DefaultMaxItemSize;

    /// <summary>
    /// Decodes every complete item currently buffered, without waiting for more data. Returns the number of bytes
    /// consumed. For tests and fuzzing.
    /// </summary>
    /// <remarks>
    /// Everything buffered is marked examined (see <see cref="ReadItems{TTo}"/>), so a later call returns 0 until new
    /// bytes arrive.
    /// </remarks>
    internal long ReadAvailable<TTo>(List<TTo> list, Converter<T, TTo>? converter)
    {
        if (!Reader.TryRead(out var result) || result.Buffer.Length == 0)
            return 0;

        return ReadItems(result.Buffer, list, converter, out _, out _);
    }

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

            long readAmount;
            int pendingItemLength;
            long pendingItemMissing;
            try
            {
                readAmount = ReadItems(result.Buffer, list, converter, out pendingItemLength, out pendingItemMissing);
            }
            catch (Exception e) when (e is NexusSerializationException or NexusMessageTooLargeException)
            {
                Reader.ResetPipe();
                throw;
            }

            if (result.IsCompleted && readAmount == 0)
                return false;

            // Waiting for the rest of an item: make sure the writer has the credit to send it. Done even when items
            // were decoded, as the whole buffer is now examined and the next read waits for new data.
            if (pendingItemLength > 0 && !await MakeRoomFor(pendingItemLength, pendingItemMissing, cancellationToken).ConfigureAwait(false))
                return list.Count > 0;

            if (list.Count > 0)
                return true;
        }
    }

    /// <summary>
    /// Lets the writer send the rest of an item of <paramref name="itemLength"/> bytes (prefix included) that is
    /// waiting at the start of the buffer, <paramref name="missing"/> bytes of which have not arrived: grants back
    /// consumed credit early if the writer needs it, and for an item larger than the window, an extra grant from the
    /// session budget. Returns false if canceled while waiting for the budget.
    /// </summary>
    private ValueTask<bool> MakeRoomFor(int itemLength, long missing, CancellationToken cancellationToken)
    {
        Reader.EnsureCredit(missing);
        return itemLength > Reader.Window
            ? Reader.GrantExtraAsync(itemLength - Reader.Window, cancellationToken)
            : new ValueTask<bool>(true);
    }

    /// <summary>
    /// Reads all complete items from the buffer.
    /// </summary>
    /// <param name="buffer">Buffered pipe data.</param>
    /// <param name="list">Receives the items.</param>
    /// <param name="converter">Converts each item, or null.</param>
    /// <param name="pendingItemLength">The length (prefix included) of an incomplete item left at the start of the
    /// remaining data, or 0.</param>
    /// <param name="pendingItemMissing">How many bytes of that item have not arrived yet.</param>
    /// <returns>Bytes consumed.</returns>
    /// <exception cref="NexusMessageTooLargeException">An item's length exceeds the limit.</exception>
    /// <exception cref="NexusSerializationException">A malformed prefix, or an item that does not decode to exactly
    /// its length.</exception>
    private long ReadItems<TTo>(
        ReadOnlySequence<byte> buffer,
        List<TTo> list,
        Converter<T, TTo>? converter,
        out int pendingItemLength,
        out long pendingItemMissing)
    {
        pendingItemLength = 0;
        pendingItemMissing = 0;

        // One reader for the whole buffer, limited to each item in turn while it is decoded.
        var reader = new MsgPackReader(buffer, _options);
        var bufferLength = buffer.Length;
        long consumed = 0;

        while (consumed < bufferLength)
        {
            reader.LimitTo(bufferLength);
            if (!ChannelFraming.TryReadLength(ref reader, out var length))
                break; // incomplete prefix

            if (length > _maxItemSize)
                throw new NexusMessageTooLargeException(length, _maxItemSize);

            var itemStart = reader.Consumed;
            if (bufferLength - itemStart < length)
            {
                // Incomplete item: wait for the rest.
                pendingItemLength = (int)(itemStart - consumed) + length;
                pendingItemMissing = length - (bufferLength - itemStart);
                break;
            }

            var itemEnd = itemStart + length;
            reader.LimitTo(itemEnd);
            T? item = default;
            _formatter.Deserialize(ref reader, ref item);
            if (reader.Consumed != itemEnd)
                throw new NexusSerializationException("Channel item does not decode to exactly its length.");

            consumed = itemEnd;
            list.Add(converter == null ? Unsafe.As<T, TTo>(ref item!) : converter(item!));
        }

        // Everything was examined: if an item is incomplete, the next read waits for more data instead of spinning.
        Reader.AdvanceToExamined(consumed, buffer.Length);
        return consumed;
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
