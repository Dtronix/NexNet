using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;

namespace NexNet.Pipes.Channels;

internal class NexusPooledUnionMessageChannelWriter<TUnion> : NexusPooledMessageChannelWriterBase<TUnion>
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{
    
    public NexusPooledUnionMessageChannelWriter(INexusDuplexPipe pipe)
        : base(pipe.WriterCore, true)
    {

    }

    internal NexusPooledUnionMessageChannelWriter(NexusPipeWriter writer)
        : base(writer, true)
    {

    }

    protected override byte GetMessageHeaderByte<TMessage>()
        => NexusPooledMessageUnionRegistry<TUnion>.GetMessageType<TMessage>();
}

internal class NexusPooledMessageChannelWriters<TMessage> : NexusPooledMessageChannelWriterBase<TMessage>
    where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    
    public NexusPooledMessageChannelWriters(INexusDuplexPipe pipe)
        : base(pipe.WriterCore, false)
    {

    }

    internal NexusPooledMessageChannelWriters(NexusPipeWriter writer)
        : base(writer, false)
    {

    }

    // Not used on this implementation as there is not header.
    protected override byte GetMessageHeaderByte<TMessage2>() => 0;
}

/// <summary>
/// The NexusPooledMessageChannelWriterBase class is a generic class that provides functionality for writing pooled message types to a NexusPipeWriter.
/// </summary>
/// <typeparam name="T">The type of the data that will be written to the NexusPipeWriter.</typeparam>
internal abstract class NexusPooledMessageChannelWriterBase<T> : NexusChannelWriter<T>
{
    private readonly bool _typeHeader;

    protected NexusPooledMessageChannelWriterBase(INexusDuplexPipe pipe, bool typeHeader)
        : base(pipe.WriterCore)
    {
        _typeHeader = typeHeader;
    }
    
    internal NexusPooledMessageChannelWriterBase(NexusPipeWriter writer, bool typeHeader)
        : base(writer)
    {
        _typeHeader = typeHeader;
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

    private void Write<TMessage>(ref TMessage item, ref NexusPipeWriter writer)
    {
        using var writerState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var memoryPackWriter = new MemoryPackWriter<NexusPipeWriter>(ref writer, writerState);
        if (_typeHeader)
        {
            var type = GetMessageHeaderByte<TMessage>();
            memoryPackWriter.WriteUnmanaged(type);
        }

        memoryPackWriter.WriteValue(item);
        memoryPackWriter.Flush();
    }

    private void WriteEnumerable<TMessage>(IEnumerable<TMessage> items, ref NexusPipeWriter writer)
    {
        using var writerState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var memoryPackWriter = new MemoryPackWriter<NexusPipeWriter>(ref writer, writerState);

        if (_typeHeader)
        {
            var type = GetMessageHeaderByte<TMessage>();
            foreach (var item in items)
            {
                memoryPackWriter.WriteUnmanaged(type);
                memoryPackWriter.WriteValue(item);
            }
        }
        else
        {
            foreach (var item in items)
            {
                memoryPackWriter.WriteValue(item);
            }
        }
        

        memoryPackWriter.Flush();
    }

    protected abstract byte GetMessageHeaderByte<TMessage>();
}
