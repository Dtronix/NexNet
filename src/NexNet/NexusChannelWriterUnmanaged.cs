﻿using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Pipes;

namespace NexNet;

/// <summary>
/// The NexusChannelWriterUnmanaged class is a generic class that provides functionality for writing unmanaged types to a NexusPipeWriter.
/// </summary>
/// <typeparam name="T">The type of the data that will be written to the NexusPipeWriter. This type must be unmanaged.</typeparam>
public class NexusChannelWriterUnmanaged<T> : NexusChannelWriter<T>
    where T : unmanaged
{

    /// <summary>
    /// Initializes a new instance of the NexusChannelWriterUnmanaged class with the specified pipe.
    /// </summary>
    /// <param name="pipe">The duplex pipe to be used for writing.</param>
    public NexusChannelWriterUnmanaged(INexusDuplexPipe pipe)
        : base(pipe.WriterCore)
    {

    }


    internal NexusChannelWriterUnmanaged(NexusPipeWriter writer)
        : base(writer)
    {

    }

    /// <summary>
    /// Asynchronously writes the specified item of unmanaged type to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="item">The item of unmanaged type to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    public override async ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        MemoryPackSerializer.Serialize<T, NexusPipeWriter>(Writer, item);

        var flushResult = await Writer.FlushAsync(cancellationToken);

        if (flushResult.IsCompleted)
        {
            IsComplete = true;
            return false;
        }

        if (flushResult.IsCanceled)
            return false;

        return true;
    }
}
