using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Threading;
using NexNet.Serialization;

namespace NexNet.Pipes;

/// <summary>
/// Writes typed items to a NexusPipeWriter. Each item is written as exactly one MessagePack value by the item
/// type's formatter.
/// </summary>
/// <typeparam name="T">The type of the data that will be written to the NexusPipeWriter.</typeparam>
internal class NexusChannelWriter<T> : INexusChannelWriter<T>
{
    internal NexusPipeWriter Writer;

    // Resolved once at construction so a missing formatter fails at channel creation rather than at the first write.
    private readonly NexusFormatter<T> _formatter;
    
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
        : this(pipe.WriterCore)
    {
    }

    /// <summary>
    /// Initializes a writer that always uses the specified NexNet formatter (used for internal protocol unions).
    /// </summary>
    internal NexusChannelWriter(INexusDuplexPipe pipe, NexusFormatter<T> formatter)
        : this(pipe.WriterCore, formatter)
    {
    }

    internal NexusChannelWriter(NexusPipeWriter writer, NexusFormatter<T>? formatter = null)
    {
        Writer = writer;
        _formatter = formatter ?? NexusFormatterRegistry.Get<T>();
    }


    /// <summary>
    /// Asynchronously writes the specified item to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="item">The item to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
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

    /// <inheritdoc />
    public async ValueTask CompleteAsync()
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);
        
        await Writer.CompleteAsync().ConfigureAwait(false);
    }


    private void Write(T item, NexusPipeWriter nexusPipeWriter)
    {
        var writer = new MsgPackWriter(nexusPipeWriter);
        _formatter.Serialize(ref writer, item);
        writer.Flush();
    }

    private void WriteEnumerable(IEnumerable<T> items, NexusPipeWriter nexusPipeWriter)
    {
        var writer = new MsgPackWriter(nexusPipeWriter);
        foreach (var item in items)
            _formatter.Serialize(ref writer, item);
        writer.Flush();
    }
}
