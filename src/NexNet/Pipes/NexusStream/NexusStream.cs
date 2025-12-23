using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Implementation of <see cref="INexusStream"/> providing stream operations over a NexNet duplex pipe.
/// </summary>
internal sealed class NexusStream : INexusStream
{
    /// <summary>
    /// Timeout for draining pending frames on cancellation.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly NexusStreamTransport _transport;
    private readonly NexusStreamMetadata _metadata;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SequenceManager _readSequence = new();
    private readonly SequenceManager _writeSequence = new();

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
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading.");

        if (buffer.Length == 0)
            return 0;

        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadInternalAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Drain pending frames with timeout
            await DrainPendingDataFramesAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async ValueTask<int> ReadInternalAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // Send Read frame
        var readFrame = new ReadFrame(buffer.Length);
        await _transport.WriteFrameAsync(readFrame, ct).ConfigureAwait(false);

        // Receive Data frames until DataEnd
        var totalReceived = 0;
        var bufferOffset = 0;

        while (true)
        {
            var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frameResult == null)
            {
                throw new InvalidOperationException("Connection closed while waiting for data.");
            }

            var (header, payload) = frameResult.Value;

            switch (header.Type)
            {
                case FrameType.Data:
                    // Parse header to validate sequence first
                    NexusStreamFrameReader.ParseDataHeader(payload, out var sequence, out var dataLength);

                    // Validate sequence
                    _readSequence.ValidateReceivedOrThrow(sequence);

                    // Check if this would exceed requested bytes (protocol error)
                    if (totalReceived + dataLength > buffer.Length)
                    {
                        throw new NexusStreamException(
                            StreamErrorCode.ProtocolError,
                            $"Received more data than requested. Requested {buffer.Length}, received {totalReceived + dataLength}.",
                            isProtocolError: true);
                    }

                    // Copy data to buffer
                    var dataFrame = NexusStreamFrameReader.ParseData(payload, buffer.Slice(bufferOffset, dataLength));
                    totalReceived += dataLength;
                    bufferOffset += dataLength;
                    break;

                case FrameType.DataEnd:
                    var dataEndFrame = NexusStreamFrameReader.ParseDataEnd(payload);

                    // Validate total bytes matches
                    if (dataEndFrame.TotalBytes != totalReceived)
                    {
                        throw new NexusStreamException(
                            StreamErrorCode.ProtocolError,
                            $"DataEnd total bytes mismatch. Expected {dataEndFrame.TotalBytes}, received {totalReceived}.",
                            isProtocolError: true);
                    }

                    // Update position
                    _position += totalReceived;
                    return totalReceived;

                case FrameType.Error:
                    var errorFrame = NexusStreamFrameReader.ParseError(payload);
                    throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

                default:
                    throw new NexusStreamException(
                        StreamErrorCode.UnexpectedFrame,
                        $"Expected Data or DataEnd frame, got {header.Type}.",
                        isProtocolError: true);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing.");

        if (data.Length == 0)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteInternalAsync(data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Drain pending response with timeout
            await DrainPendingResponseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask WriteInternalAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // Send Write frame
        var writeFrame = new WriteFrame(data.Length);
        await _transport.WriteFrameAsync(writeFrame, ct).ConfigureAwait(false);

        // Send Data frames (chunked if needed)
        await _transport.WriteDataChunksAsync(data, _writeSequence, ct).ConfigureAwait(false);

        // Send DataEnd frame
        var finalSequence = _writeSequence.NextToSend > 0 ? _writeSequence.NextToSend - 1 : 0;
        var dataEndFrame = new DataEndFrame(data.Length, finalSequence);
        await _transport.WriteFrameAsync(dataEndFrame, ct).ConfigureAwait(false);

        // Wait for WriteResponse
        var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frameResult == null)
        {
            throw new InvalidOperationException("Connection closed while waiting for write response.");
        }

        var (header, payload) = frameResult.Value;

        switch (header.Type)
        {
            case FrameType.WriteResponse:
                var response = NexusStreamFrameReader.ParseWriteResponse(payload);

                if (!response.Success)
                {
                    throw new NexusStreamException(response.ErrorCode, $"Write failed: {response.ErrorCode}");
                }

                // Update position from response (authoritative)
                _position = response.Position;
                break;

            case FrameType.Error:
                var errorFrame = NexusStreamFrameReader.ParseError(payload);
                throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

            default:
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected WriteResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
        }
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
            _readLock.Dispose();
            _writeLock.Dispose();
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

    private async ValueTask DrainPendingDataFramesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(DrainTimeout);
            while (true)
            {
                var frameResult = await _transport.ReadFrameAsync(cts.Token).ConfigureAwait(false);
                if (frameResult == null)
                    return;

                var (header, _) = frameResult.Value;

                // Stop draining when we receive DataEnd
                if (header.Type == FrameType.DataEnd)
                    return;

                // Stop draining on Error
                if (header.Type == FrameType.Error)
                    return;

                // Continue draining Data frames
            }
        }
        catch (OperationCanceledException)
        {
            // Drain timeout expired - transport may be in inconsistent state
        }
        catch
        {
            // Ignore errors during drain
        }
    }

    private async ValueTask DrainPendingResponseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(DrainTimeout);
            var frameResult = await _transport.ReadFrameAsync(cts.Token).ConfigureAwait(false);
            // Just read one response frame to clear the pending state
        }
        catch
        {
            // Ignore errors during drain
        }
    }
}
