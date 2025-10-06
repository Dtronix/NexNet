using System;
using System.Threading.Tasks;
using NexNet.Pipes.Channels;

namespace NexNet.Pipes;

/// <summary>
/// Base interface for duplex channels.
/// </summary>
public interface INexusDuplexChannel : IAsyncDisposable
{
    /// <summary>
    /// Gets the underlying <see cref="INexusDuplexPipe"/> that is used by the channel.
    /// </summary>
    INexusDuplexPipe? BasePipe { get; }
}

/// <summary>
/// Represents a duplex channel utilizing a <see cref="INexusDuplexPipe"/>. This channel allows for bidirectional communication, 
/// meaning that data can be both sent and received. The type of data that can be transmitted is defined by the generic parameter T.
/// </summary>
/// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
public interface INexusDuplexChannel<T> : INexusDuplexChannel
{
    /// <summary>
    /// Sets the pipe to the complete state and closes the other end of the connection.
    /// Do not use the pipe after calling this method.
    /// </summary>
    /// <returns>Task which completes when the pipe is closed.</returns>
    ValueTask CompleteAsync();

    /// <summary>
    /// Asynchronously gets an instance of <see cref="INexusChannelWriter{T}"/> which can be used to write data to the duplex channel.
    /// </summary>
    /// <returns>A ValueTask that represents the asynchronous operation. The task result contains the <see cref="INexusChannelWriter{T}"/> instance.</returns>
    ValueTask<INexusChannelWriter<T>> GetWriterAsync();

    /// <summary>
    /// Asynchronously gets an instance of <see cref="INexusChannelReader{T}"/> which can be used to read data from the duplex channel.
    /// </summary>
    /// <returns>A ValueTask that represents the asynchronous operation. The task result contains the <see cref="INexusChannelReader{T}"/> instance.</returns>
    ValueTask<INexusChannelReader<T>> GetReaderAsync();
}
