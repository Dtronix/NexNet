using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

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

/// <summary>
/// Represents a stream of objects that can be asynchronously enumerated.
/// </summary>
/// <typeparam name="T">The type of objects in the stream.</typeparam>
public class NexusEnumerableStream<T> :IAsyncEnumerable<T>
{
    private readonly IRentedNexusDuplexPipe _duplexPipe;

    /// <summary>
    /// Initializes a new instance of the NexusEnumerableStream class.
    /// </summary>
    /// <param name="duplexPipe">The IRentedNexusDuplexPipe to use for the stream.</param>
    public NexusEnumerableStream(IRentedNexusDuplexPipe duplexPipe)
    {
        _duplexPipe = duplexPipe;
    }
    internal async ValueTask WriteAndComplete(IEnumerable<T> enumerable)
    {
        var writer = await _duplexPipe.GetChannelWriter<T>();
        await writer.WriteAndComplete(enumerable);
    }
    
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        return new Enumerator(_duplexPipe);
    }

    private class Enumerator : IAsyncEnumerator<T>
    {
        private readonly NexusChannelReader<T> _reader;
        private readonly INexusDuplexPipe _duplexPipe;
        private readonly List<T>

        /// <summary>
        /// Creates an instance of the Enumerator class with the specified IRentedNexusDuplexPipe object.
        /// </summary>
        /// <param name="duplexPipe">The IRentedNexusDuplexPipe object to use for reading data.</param>
        public Enumerator(IRentedNexusDuplexPipe duplexPipe)
        {
            _duplexPipe = duplexPipe;
            _reader = new NexusChannelReader<T>(_duplexPipe.ReaderCore);
        }

        /// <summary>
        /// Gets or sets the current value of the property.
        /// </summary>
        /// <value>The current value of the property.</value>
        public T Current { get; set; }

        public async ValueTask<bool> MoveNextAsync()
        {
            await _duplexPipe.ReadyTask;
            _reader.ReadAsync()
            INexusChannelReader<T> reader = await _duplexPipe.GetChannelReader<T>();
            throw new NotImplementedException();
        }
        public ValueTask DisposeAsync()
        {
            return _duplexPipe.Input.CompleteAsync();
        }
    }
}
