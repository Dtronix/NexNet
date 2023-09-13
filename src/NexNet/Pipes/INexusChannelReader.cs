using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// The channel reader is responsible for reading items of a specified type from the underlying INexusPipeWriter.
/// </summary>
/// <typeparam name="T">The type of the items that will be written to the INexusPipeWriter.</typeparam>
public interface INexusChannelReader<T>
{
    /// <summary>
    /// Gets a value indicating whether the reading operation from the duplex pipe is complete.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>
    /// Gets a value indicating the number of bytes that are currently buffered in the underlying INexusPipeWriter.
    /// </summary>
    long BufferedLength { get; }

    /// <summary>
    /// Asynchronously reads data from the duplex pipe.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A task that represents the asynchronous read operation. The value of the TResult parameter contains an enumerable collection of type T.
    /// If the read operation is completed or canceled, the returned task will contain an empty collection.
    /// </returns>
    ValueTask<IReadOnlyList<T>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously reads data from the duplex pipe and converts it using the provided converter function.
    /// </summary>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="converter">A function that converts each item of type T to type TTo.</param>
    /// <param name="cancellationToken">An optional token to cancel the read operation.</param>
    /// <returns>
    /// A task that represents the asynchronous read operation. The value of the TResult parameter contains an enumerable collection of type TTo.
    /// If the read operation is completed or canceled, the returned task will contain an empty collection.
    /// </returns>
    ValueTask<IReadOnlyList<TTo>> ReadAsync<TTo>(Converter<T, TTo> converter, CancellationToken cancellationToken = default);
}
