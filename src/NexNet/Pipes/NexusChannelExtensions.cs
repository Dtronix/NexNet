using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;

namespace NexNet.Pipes;

/// <summary>
/// Provides extension methods for the INexusDuplexChannel interface.
/// These methods provide functionality for writing to and reading from duplex channels.
/// </summary>
public static class NexusChannelExtensions
{
    /// <summary>
    /// Writes the provided data to the given duplex channel in optional chunks and completes the channel.
    /// </summary>
    /// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
    /// <param name="channel">The duplex channel to which the data will be written.</param>
    /// <param name="enumerableData">The data to be written to the channel.</param>
    /// <param name="chunkSize">The size of the chunks to be written to the channel.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write and complete operation.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask WriteAndComplete<T>(
        this INexusDuplexChannel<T> channel,
        IEnumerable<T> enumerableData,
        int chunkSize = 10,
        CancellationToken cancellationToken = default)
    {
        var writer = await channel.GetWriterAsync();
        await WriteAndComplete<T>(writer, enumerableData, chunkSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the provided data to the given channel writer in optional chunks and completes the channel.
    /// </summary>
    /// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
    /// <param name="writer">The channel writer to which the data will be written.</param>
    /// <param name="enumerableData">The data to be written to the channel.</param>
    /// <param name="chunkSize">The size of the chunks to be written to the channel.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous write and complete operation.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask WriteAndComplete<T>(
        this INexusChannelWriter<T> writer,
        IEnumerable<T> enumerableData,
        int chunkSize = 10,
        CancellationToken cancellationToken = default)
    {
        // If the chunk size is greater than 1, split the data into chunks and write them to the channel
        // otherwise write the data to the channel as is.
        if (chunkSize > 1)
        {
            var dataChunks = enumerableData.Chunk(chunkSize);

            foreach (var chunk in dataChunks)
            {
                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var data in enumerableData)
            {
                await writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads data from the given duplex channel until the channel is complete and returns a list with read data.
    /// </summary>
    /// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
    /// <param name="channel">The duplex channel from which the data will be read.</param>
    /// <param name="estimatedSize">An optional parameter to set the initial capacity of the list that will store the read data.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous read operation. The task result contains a List of type T with the data read from the channel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<List<T>> ReadUntilComplete<T>(
        this INexusDuplexChannel<T> channel,
        int estimatedSize = 0,
        CancellationToken cancellationToken = default)
    {
        return ReadUntilComplete<T, T>(channel, static (in T t) => t, estimatedSize, cancellationToken);
    }

    /// <summary>
    /// Reads data from the given duplex channel until the channel is complete and returns a list with read data.
    /// </summary>
    /// <typeparam name="T">The source type of the data that can be transmitted through the channel.</typeparam>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="channel">The duplex channel from which the data will be read.</param>
    /// <param name="converter">A function that converts each item of type T to type TTo.</param>
    /// <param name="estimatedSize">An optional parameter to set the initial capacity of the list that will store the read data.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous read operation. The task result contains a List of type T with the data read from the channel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<List<TTo>> ReadUntilComplete<T, TTo>(
        this INexusDuplexChannel<T> channel,
        Converter<T, TTo> converter,
        int estimatedSize = 0,
        CancellationToken cancellationToken = default)
    { 
        var reader = await channel.GetReaderAsync().ConfigureAwait(false);
        return await ReadUntilComplete<T, TTo>(reader, converter, estimatedSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads data from the given channel reader until the reader is complete and returns a list with read data.
    /// </summary>
    /// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
    /// <param name="reader">The channel reader from which the data will be read.</param>
    /// <param name="estimatedSize">An optional parameter to set the initial capacity of the list that will store the read data.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous read operation. The task result contains a List of type T with the data read from the channel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<List<T>> ReadUntilComplete<T>(
        this INexusChannelReader<T> reader,
        int estimatedSize = 0,
        CancellationToken cancellationToken = default)
    {
        return ReadUntilComplete<T, T>(reader, static (in T t) => t, estimatedSize, cancellationToken);
    }

    /// <summary>
    /// Reads data from the given channel reader until the reader is complete and returns a list with read data.
    /// </summary>
    /// <typeparam name="T">The source type of the data that can be transmitted through the channel.</typeparam>
    /// <typeparam name="TTo">The type of the items that will be returned after conversion.</typeparam>
    /// <param name="reader">The channel reader from which the data will be read.</param>
    /// <param name="converter">A function that converts each item of type T to type TTo.</param>
    /// <param name="estimatedSize">An optional parameter to set the initial capacity of the list that will store the read data.</param>
    /// <param name="cancellationToken">An optional CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A ValueTask that represents the asynchronous read operation. The task result contains a List of type T with the data read from the channel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<List<TTo>> ReadUntilComplete<T, TTo>(
        this INexusChannelReader<T> reader,
        Converter<T, TTo> converter,
        int estimatedSize = 0,
        CancellationToken cancellationToken = default)
    {
        var list = new List<TTo>(estimatedSize);
        while (true)
        {
            var previousBufferLength = reader.BufferedLength;

            if (reader.IsComplete && previousBufferLength == 0)
                break;

            var readResult = await reader.ReadAsync(converter, cancellationToken)
                .ConfigureAwait(false);
            var resultCount = readResult.Count;
            if (reader.IsComplete
                && resultCount == 0
                && previousBufferLength == reader.BufferedLength)
                return list;

            if (resultCount > 0)
            {
                list.AddRange(readResult);

                // Return the list to the pool.
                ListPool<TTo>.Return(Unsafe.As<List<TTo>>(readResult));
            }
        }

        return list;
    }

}
