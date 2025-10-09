using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;

namespace NexNet.Pipes.Channels;

/// <summary>
/// The NexusChannelWriterUnmanaged class is a generic class that provides functionality for writing unmanaged types to a NexusPipeWriter.
/// </summary>
/// <typeparam name="TUnion">The type of the data that will be written to the NexusPipeWriter. This type must be unmanaged.</typeparam>
internal class NexusPooledMessageChannelWriter<TUnion> : NexusChannelWriter<TUnion>
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{

    /// <summary>
    /// Initializes a new instance of the NexusChannelWriterUnmanaged class with the specified pipe.
    /// </summary>
    /// <param name="pipe">The duplex pipe to be used for writing.</param>
    public NexusPooledMessageChannelWriter(INexusDuplexPipe pipe)
        : base(pipe.WriterCore)
    {

    }
    
    internal NexusPooledMessageChannelWriter(NexusPipeWriter writer)
        : base(writer)
    {

    }

    /// <summary>
    /// Asynchronously writes the specified item of unmanaged type to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="item">The item of unmanaged type to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    public override async ValueTask<bool> WriteAsync<TMessage>(TMessage item, CancellationToken cancellationToken = default)
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);

        Write(ref item, ref Writer);
        
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
    /// Asynchronously writes the specified item of unmanaged type to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="items">The items of unmanaged type to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    public override async ValueTask<bool> WriteAsync<TMessage>(IEnumerable<TMessage> items, CancellationToken cancellationToken = default)
    {
        using var sLock = await ModificationSemaphore.WaitDisposableAsync().ConfigureAwait(false);

        WriteEnumerable(items, ref Writer);

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

    private static void Write<TMessage>(ref TMessage item, ref NexusPipeWriter writer)
    {
        using var writerState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var memoryPackWriter = new MemoryPackWriter<NexusPipeWriter>(ref writer, writerState);
        var type = NexusMessageUnionRegistry<TUnion>.GetMessageType<TMessage>();
        memoryPackWriter.WriteUnmanaged(type);
        memoryPackWriter.WriteValue(item);
        memoryPackWriter.Flush();
    }

    private static void WriteEnumerable<TMessage>(IEnumerable<TMessage> items, ref NexusPipeWriter writer)
    {
        using var writerState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var memoryPackWriter = new MemoryPackWriter<NexusPipeWriter>(ref writer, writerState);
        var type = NexusMessageUnionRegistry<TUnion>.GetMessageType<TMessage>();
        foreach (var item in items)
        {
            memoryPackWriter.WriteUnmanaged(type);
            memoryPackWriter.WriteValue(item);
        }

        memoryPackWriter.Flush();
    }
}
