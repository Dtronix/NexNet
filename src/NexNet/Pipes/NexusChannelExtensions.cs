﻿using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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
        int chunkSize = 1,
        CancellationToken cancellationToken = default)
    {
        var writer = await channel.GetWriterAsync();

        // If the chunk size is greater than 1, split the data into chunks and write them to the channel
        // otherwise write the data to the channel as is.
        if (chunkSize > 1)
        {
            var dataChunks = enumerableData.Chunk(chunkSize);

            foreach (var chunk in dataChunks)
            {
                await writer.WriteAsync(chunk, cancellationToken);
            }
        }
        else
        {
            foreach (var data in enumerableData)
            {
                await writer.WriteAsync(data, cancellationToken);
            }

        }

        await channel.CompleteAsync();
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
    public static async ValueTask<List<T>> ReadUntilComplete<T>(
        this INexusDuplexChannel<T> channel,
        int estimatedSize = 0,
        CancellationToken cancellationToken = default)
    {
        using var reader = await channel.GetReaderAsync();
        var list = new List<T>(estimatedSize);
        while (reader.IsComplete == false)
        {
            list.AddRange(await reader.ReadAsync(cancellationToken))
        }

        return list;
    }

}
