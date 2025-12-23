using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Implementation of <see cref="INexusStream"/> providing stream operations over a NexNet duplex pipe.
/// </summary>
internal sealed class NexusStream : INexusStream
{
    private readonly NexusStreamTransport _transport;
    private readonly NexusStreamMetadata _metadata;
    private NexusStreamState _state;
    private Exception? _error;
    private long _position;

    /// <inheritdoc />
    public NexusStreamState State => _state;

    /// <inheritdoc />
    public Exception? Error => _error;

    /// <inheritdoc />
    public long Position
    {
        get
        {
            if (!CanSeek)
                throw new NotSupportedException("Stream does not support seeking.");
            return _position;
        }
    }

    /// <inheritdoc />
    public long Length => _metadata.Length;

    /// <inheritdoc />
    public bool HasKnownLength => _metadata.HasKnownLength;

    /// <inheritdoc />
    public bool CanSeek => _metadata.CanSeek;

    /// <inheritdoc />
    public bool CanRead => _metadata.CanRead;

    /// <inheritdoc />
    public bool CanWrite => _metadata.CanWrite;

    /// <inheritdoc />
    public IObservable<NexusStreamProgress> Progress => throw new NotImplementedException("Progress tracking not implemented until Phase 5.");

    /// <summary>
    /// Creates a new NexusStream instance.
    /// </summary>
    /// <param name="transport">The transport that owns this stream.</param>
    /// <param name="metadata">The stream metadata from the open response.</param>
    /// <param name="initialPosition">The initial position (for resumed streams).</param>
    internal NexusStream(NexusStreamTransport transport, NexusStreamMetadata metadata, long initialPosition = 0)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _metadata = metadata;
        _position = initialPosition;
        _state = NexusStreamState.Open;
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        throw new NotImplementedException("Read operations not implemented until Phase 3.");
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        throw new NotImplementedException("Write operations not implemented until Phase 3.");
    }

    /// <inheritdoc />
    public ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking.");
        throw new NotImplementedException("Seek operations not implemented until Phase 4.");
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        throw new NotImplementedException("Flush operations not implemented until Phase 4.");
    }

    /// <inheritdoc />
    public ValueTask SetLengthAsync(long length, CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing.");
        throw new NotImplementedException("SetLength operations not implemented until Phase 4.");
    }

    /// <inheritdoc />
    public ValueTask<NexusStreamMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        throw new NotImplementedException("GetMetadata operations not implemented until Phase 5.");
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        ThrowIfNotOpen();
        throw new NotImplementedException("Stream wrapper not implemented until Phase 6.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_state == NexusStreamState.Closed)
            return;

        try
        {
            await _transport.CloseStreamAsync(graceful: true).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors during close
        }
        finally
        {
            _state = NexusStreamState.Closed;
        }
    }

    /// <summary>
    /// Sets the stream to a failed state with the given error.
    /// </summary>
    internal void SetError(Exception error)
    {
        _error = error;
        _state = NexusStreamState.Closed;
    }

    /// <summary>
    /// Updates the stream position (called after successful read/write/seek).
    /// </summary>
    internal void UpdatePosition(long newPosition)
    {
        _position = newPosition;
    }

    private void ThrowIfNotOpen()
    {
        if (_state != NexusStreamState.Open)
        {
            if (_error != null)
                throw new InvalidOperationException($"Stream is in {_state} state.", _error);
            throw new InvalidOperationException($"Stream is in {_state} state.");
        }
    }
}
