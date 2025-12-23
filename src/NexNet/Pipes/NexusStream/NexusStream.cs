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
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _seekLock = new(1, 1);
    private readonly SequenceManager _readSequence = new();
    private readonly SequenceManager _writeSequence = new();
    private readonly ProgressTracker _progressTracker;

    private NexusStreamMetadata _cachedMetadata;
    private NexusStreamState _state;
    private Exception? _error;
    private long _position;
    private long _logicalPosition; // Tracked for progress on non-seekable streams
    private long _length;
    private long _bytesRead;
    private long _bytesWritten;

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
    public long Length => _length;

    /// <summary>
    /// Gets the logical position for progress tracking (works for non-seekable streams).
    /// </summary>
    internal long LogicalPosition => _logicalPosition;

    /// <inheritdoc />
    public bool HasKnownLength => _cachedMetadata.HasKnownLength;

    /// <inheritdoc />
    public bool CanSeek => _cachedMetadata.CanSeek;

    /// <inheritdoc />
    public bool CanRead => _cachedMetadata.CanRead;

    /// <inheritdoc />
    public bool CanWrite => _cachedMetadata.CanWrite;

    /// <inheritdoc />
    public Action<NexusStreamProgress>? OnProgress { get; set; }

    /// <summary>
    /// Creates a new NexusStream instance.
    /// </summary>
    /// <param name="transport">The transport that owns this stream.</param>
    /// <param name="metadata">The stream metadata from the open response.</param>
    /// <param name="initialPosition">The initial position (for resumed streams).</param>
    /// <param name="progressByteThreshold">Minimum bytes transferred before emitting progress (default: 1 MB).</param>
    /// <param name="progressTimeInterval">Minimum time between progress reports (default: 5 seconds).</param>
    internal NexusStream(
        NexusStreamTransport transport,
        NexusStreamMetadata metadata,
        long initialPosition = 0,
        long progressByteThreshold = ProgressTracker.DefaultByteThreshold,
        TimeSpan? progressTimeInterval = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cachedMetadata = metadata;
        _position = initialPosition;
        _logicalPosition = initialPosition;
        _length = metadata.Length;
        _state = NexusStreamState.Open;
        _progressTracker = new ProgressTracker(progressByteThreshold, progressTimeInterval);
        _progressTracker.Start();
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading.");

        if (buffer.Length == 0)
            return 0;

        // Check if a seek is in progress
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _seekLock.Release();
        }

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

                    // Update positions and counters
                    _position += totalReceived;
                    _logicalPosition += totalReceived;
                    _bytesRead += totalReceived;

                    // Emit progress if needed
                    EmitProgressIfNeeded(TransferState.Active);

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

        // Check if a seek is in progress
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _seekLock.Release();
        }

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

                // Update positions and counters (server position is authoritative, but track logical for progress)
                _logicalPosition += response.BytesWritten;
                _position = response.Position;
                _bytesWritten += response.BytesWritten;

                // Emit progress if needed
                EmitProgressIfNeeded(TransferState.Active);
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
    public async ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default)
    {
        ThrowIfNotOpen();
        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking.");

        // Acquire seek lock to block new reads/writes
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Wait for any in-progress read/write to complete
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                return await SeekInternalAsync(offset, origin, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
                _readLock.Release();
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    private async ValueTask<long> SeekInternalAsync(long offset, SeekOrigin origin, CancellationToken ct)
    {
        // Send Seek frame
        var seekFrame = new SeekFrame(offset, origin);
        await _transport.WriteFrameAsync(seekFrame, ct).ConfigureAwait(false);

        // Wait for SeekResponse
        var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frameResult == null)
        {
            throw new InvalidOperationException("Connection closed while waiting for seek response.");
        }

        var (header, payload) = frameResult.Value;

        switch (header.Type)
        {
            case FrameType.SeekResponse:
                var response = NexusStreamFrameReader.ParseSeekResponse(payload);

                // Always update position from response (authoritative)
                _position = response.Position;

                if (!response.Success)
                {
                    throw new NexusStreamException(response.ErrorCode, $"Seek failed: {response.ErrorCode}");
                }

                return _position;

            case FrameType.Error:
                var errorFrame = NexusStreamFrameReader.ParseError(payload);
                throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

            default:
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected SeekResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        // Flush only requires write lock (pending writes)
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask FlushInternalAsync(CancellationToken ct)
    {
        // Send Flush frame
        await _transport.WriteFlushAsync(ct).ConfigureAwait(false);

        // Wait for FlushResponse (waits for in-flight data to be acknowledged)
        var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frameResult == null)
        {
            throw new InvalidOperationException("Connection closed while waiting for flush response.");
        }

        var (header, payload) = frameResult.Value;

        switch (header.Type)
        {
            case FrameType.FlushResponse:
                var response = NexusStreamFrameReader.ParseFlushResponse(payload);

                // Update position from response for resync
                _position = response.Position;

                if (!response.Success)
                {
                    throw new NexusStreamException(response.ErrorCode, $"Flush failed: {response.ErrorCode}");
                }
                break;

            case FrameType.Error:
                var errorFrame = NexusStreamFrameReader.ParseError(payload);
                throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

            default:
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected FlushResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask SetLengthAsync(long length, CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing.");
        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking (required for SetLength).");

        // SetLength requires exclusive access like Seek
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _readLock.WaitAsync(ct).ConfigureAwait(false);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await SetLengthInternalAsync(length, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
                _readLock.Release();
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    private async ValueTask SetLengthInternalAsync(long length, CancellationToken ct)
    {
        // Send SetLength frame
        var setLengthFrame = new SetLengthFrame(length);
        await _transport.WriteFrameAsync(setLengthFrame, ct).ConfigureAwait(false);

        // Wait for SetLengthResponse
        var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frameResult == null)
        {
            throw new InvalidOperationException("Connection closed while waiting for set length response.");
        }

        var (header, payload) = frameResult.Value;

        switch (header.Type)
        {
            case FrameType.SetLengthResponse:
                var response = NexusStreamFrameReader.ParseSetLengthResponse(payload);

                // Update length and position from response (authoritative)
                _length = response.NewLength;
                _position = response.Position;

                if (!response.Success)
                {
                    throw new NexusStreamException(response.ErrorCode, $"SetLength failed: {response.ErrorCode}");
                }
                break;

            case FrameType.Error:
                var errorFrame = NexusStreamFrameReader.ParseError(payload);
                throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

            default:
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected SetLengthResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask<NexusStreamMetadata> GetMetadataAsync(bool refresh = false, CancellationToken ct = default)
    {
        ThrowIfNotOpen();

        if (!refresh)
        {
            // Return cached metadata
            return _cachedMetadata;
        }

        // Request fresh metadata from server
        await _transport.WriteGetMetadataAsync(ct).ConfigureAwait(false);

        var frameResult = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frameResult == null)
        {
            throw new InvalidOperationException("Connection closed while waiting for metadata response.");
        }

        var (header, payload) = frameResult.Value;

        switch (header.Type)
        {
            case FrameType.MetadataResponse:
                var response = NexusStreamFrameReader.ParseMetadataResponse(payload);
                _cachedMetadata = response.Metadata;
                _length = response.Metadata.Length;
                return _cachedMetadata;

            case FrameType.Error:
                var errorFrame = NexusStreamFrameReader.ParseError(payload);
                throw new NexusStreamException(errorFrame.ErrorCode, errorFrame.Message, errorFrame.IsProtocolError);

            default:
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected MetadataResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
        }
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
            // Emit final progress on completion
            _progressTracker.Stop();
            EmitProgress(TransferState.Complete);

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
            _seekLock.Dispose();
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

    /// <summary>
    /// Emits a progress notification if the thresholds are met.
    /// </summary>
    /// <param name="state">The current transfer state.</param>
    private void EmitProgressIfNeeded(TransferState state)
    {
        if (OnProgress == null)
            return;

        if (!_progressTracker.ShouldReport(_bytesRead, _bytesWritten, state))
            return;

        var progress = new NexusStreamProgress
        {
            BytesRead = _bytesRead,
            BytesWritten = _bytesWritten,
            TotalReadBytes = HasKnownLength ? _length : -1,
            TotalWriteBytes = HasKnownLength ? _length : -1,
            Elapsed = _progressTracker.Elapsed,
            ReadBytesPerSecond = _progressTracker.CalculateRate(_bytesRead),
            WriteBytesPerSecond = _progressTracker.CalculateRate(_bytesWritten),
            State = state
        };

        OnProgress(progress);
    }

    /// <summary>
    /// Forces a progress notification regardless of thresholds.
    /// </summary>
    /// <param name="state">The current transfer state.</param>
    internal void EmitProgress(TransferState state)
    {
        if (OnProgress == null)
            return;

        _progressTracker.ForceNextReport();
        EmitProgressIfNeeded(state);
    }
}
