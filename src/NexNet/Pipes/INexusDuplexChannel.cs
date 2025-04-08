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
public class NexusEnumerableChannel<T> :IAsyncEnumerable<T>
{
    private readonly IEnumerable<T>? _writingEnumerable;
    internal IRentedNexusDuplexPipe DuplexPipe;

    /// <summary>
    /// Initializes a new instance of the NexusEnumerableStream class.
    /// </summary>
    public NexusEnumerableChannel(IEnumerable<T> writingEnumerable)
    {
        _writingEnumerable = writingEnumerable;
    }

    internal NexusEnumerableChannel(IRentedNexusDuplexPipe duplexPipe)
    {
        _writingEnumerable = null;
        DuplexPipe = duplexPipe;
    }
    
    internal async ValueTask WriteAndComplete()
    {
        var writer = await DuplexPipe.GetChannelWriter<T>();
        await writer.WriteAndComplete(_writingEnumerable);
    }
    
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new CancellationToken())
    {
        return new Enumerator(DuplexPipe);
    }

    private class Enumerator : IAsyncEnumerator<T>
    {
        private NexusChannelReader<T>? _reader;
        private readonly INexusDuplexPipe _duplexPipe;
        private List<T>? _list = null;
        private int _readIndex = 0;

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
            // If the reader is null, the channel has been read to completion.
            if (_reader == null)
            {
                // If the read index is -1, then the pipe reading has completed.
                if (_readIndex == -1)
                    return false;

                if (_readIndex < _list!.Count)
                {
                    Current = _list[_readIndex++];
                    return true;
                }

                // If we got here, we are at the end of the list and the channel has completed.
                Current = default!;
                _readIndex = -1;
                _list.Clear();
                _list.TrimExcess();
                _list = null;
                
                return false;
            }
            
            if (_list == null)
            {
                await _duplexPipe.ReadyTask;
                _list = new List<T>();
            }

            if (_readIndex < _list.Count)
            {
                Current = _list[_readIndex++];
                return true;
            }

            var result = await _reader.ReadAsync(_list, null);

            // If false, then the reader has completed.
            if (result == false)
            {
                _readIndex = -1;
                return false;
            }
            
            _readIndex = 1;
            Current = _list[_readIndex];
            return true;
        }
        public ValueTask DisposeAsync()
        {
            return _duplexPipe.Input.CompleteAsync();
        }
    }
}
