using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// The channel writer is responsible for writing items of a specified type to the underlying INexusPipeWriter.
/// </summary>
/// <typeparam name="T">The type of the items that will be written to the INexusPipeWriter.</typeparam>
public interface INexusChannelWriter<in T>
{
    /// <summary>
    /// Gets a value indicating whether the reading operation from the duplex pipe is complete.
    /// </summary>
    bool IsComplete { get; set; }

    /// <summary>
    /// Asynchronously writes the specified item of unmanaged type to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="item">The item of unmanaged type to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously writes the specified item of unmanaged type to the underlying NexusPipeWriter.
    /// </summary>
    /// <param name="items">The items of unmanaged type to be written to the NexusPipeWriter.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write operation. The task result contains a boolean value that indicates whether the write operation was successful. Returns false if the operation is canceled or the pipe writer is completed.</returns>
    ValueTask<bool> WriteAsync(IEnumerable<T> items, CancellationToken cancellationToken = default);
}
