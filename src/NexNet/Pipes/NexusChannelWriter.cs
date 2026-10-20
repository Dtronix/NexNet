using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Threading;
using NexNet.Serialization;

namespace NexNet.Pipes;

/// <summary>
/// Writes typed items to a NexusPipeWriter. Each item is one MessagePack value written by the item type's
/// formatter, preceded by its length (<see cref="ChannelFraming"/>).
/// </summary>
/// <typeparam name="T">The type of the data that will be written to the NexusPipeWriter.</typeparam>
internal class NexusChannelWriter<T> : INexusChannelWriter<T>
{
    internal NexusPipeWriter Writer;

    // Resolved once at construction so a missing formatter fails at channel creation rather than at the first write.
    private readonly NexusFormatter<T> _formatter;

    // Largest item the peer accepts (the smaller of both sides' MaxMessageSize).
    private readonly int _maxItemSize;
    
    // Semaphore to ensure the underlying channel does not get used concurrently.
    protected readonly SemaphoreSlim ModificationSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Gets a value indicating whether the reading operation from the duplex pipe is complete.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelWriter{T}"/> class with the specified pipe.
    /// </summary>
    /// <param name="pipe">The duplex pipe to be used for writing.</param>
    public NexusChannelWriter(INexusDuplexPipe pipe)
        : this(pipe.WriterCore, null, GetMaxItemSize(pipe))
    {
    }

    /// <summary>
    /// Initializes a writer that always uses the specified NexNet formatter (used for internal protocol unions).
    /// </summary>
    internal NexusChannelWriter(INexusDuplexPipe pipe, NexusFormatter<T> formatter)
        : this(pipe.WriterCore, formatter, GetMaxItemSize(pipe))
    {
    }

    internal NexusChannelWriter(NexusPipeWriter writer, NexusFormatter<T>? formatter = null, int maxItemSize = int.MaxValue)
    {
        Writer = writer;
        _formatter = formatter ?? NexusFormatterRegistry.Get<T>();
        _maxItemSize = maxItemSize;
    }

    private static int GetMaxItemSize(INexusDuplexPipe pipe)
        => (pipe as NexusDuplexPipe)?.Session?.SendMaxMessageSize ?? int.MaxValue;


    /// <summary>
    /// Asynchronously writes the specified item to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="item">The item to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    /// <exception cref="NexusMessageTooLargeException">The item is larger than the peer accepts. Nothing is sent and
    /// the channel stays usable.</exception>
    public virtual async ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);

        Write(item, Writer);

        var flushResult = await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (flushResult.IsCompleted)
        {
            IsComplete = true;
            return false;
        }

        if (flushResult.IsCanceled)
            return false;

        return true;

    }

    /// <summary>
    /// Asynchronously writes the specified items to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="items">The items to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    /// <exception cref="NexusMessageTooLargeException">An item is larger than the peer accepts. The items before it
    /// are sent with the next flush; the channel stays usable.</exception>
    public virtual async ValueTask<bool> WriteAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);
        
        WriteEnumerable(items, Writer);

        var flushResult = await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (flushResult.IsCompleted)
        {
            IsComplete = true;
            return false;
        }

        if (flushResult.IsCanceled)
            return false;

        return true;
    }

    /// <summary>
    /// Flushes everything written so far. Serialized with writes, unlike flushing the underlying pipe writer.
    /// </summary>
    internal async ValueTask FlushAsync()
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);
        await Writer.FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync()
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);
        
        await Writer.CompleteAsync().ConfigureAwait(false);
    }


    private void Write(T item, NexusPipeWriter nexusPipeWriter)
    {
        var scratch = PooledArrayBufferWriter.Rent();
        try
        {
            WriteFramed(item, scratch, nexusPipeWriter);
        }
        finally
        {
            scratch.Return();
        }
    }

    private void WriteEnumerable(IEnumerable<T> items, NexusPipeWriter nexusPipeWriter)
    {
        var scratch = PooledArrayBufferWriter.Rent();
        try
        {
            foreach (var item in items)
            {
                WriteFramed(item, scratch, nexusPipeWriter);
                scratch.Clear();
            }
        }
        finally
        {
            scratch.Return();
        }
    }

    /// <summary>
    /// Serializes the item into <paramref name="scratch"/> to learn its length, checks the length, then writes the
    /// length prefix and the item.
    /// </summary>
    private void WriteFramed(T item, PooledArrayBufferWriter scratch, NexusPipeWriter output)
    {
        var writer = new MsgPackWriter(scratch);
        _formatter.Serialize(ref writer, item);
        writer.Flush();

        var length = scratch.WrittenCount;
        if (length > _maxItemSize)
            throw new NexusMessageTooLargeException(length, _maxItemSize);

        // Prefix and item in one append, so a flush never sends a prefix without its item.
        var total = ChannelFraming.PrefixLength(length) + length;
        var span = output.GetSpan(total);
        var prefix = ChannelFraming.WriteLength(span, length);
        scratch.WrittenSpan.CopyTo(span[prefix..]);
        output.Advance(total);
    }
}
