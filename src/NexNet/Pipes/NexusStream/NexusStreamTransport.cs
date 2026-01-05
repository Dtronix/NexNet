using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Provides stream transport over a NexNet duplex pipe.
/// Implements the NexStream protocol state machine.
/// </summary>
internal sealed class NexusStreamTransport : INexusStreamTransport
{
    private readonly INexusDuplexPipe _pipe;
    private readonly NexusStreamFrameWriter _writer;
    private readonly NexusStreamFrameReader _reader;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private NexusStreamState _state = NexusStreamState.None;
    private NexusStream? _activeStream;
    private bool _disposed;

    /// <inheritdoc />
    public Task ReadyTask => _pipe.ReadyTask;

    /// <summary>
    /// Gets the current state of the transport.
    /// </summary>
    internal NexusStreamState State => _state;

    /// <summary>
    /// Creates a new stream transport over the specified duplex pipe.
    /// </summary>
    /// <param name="pipe">The duplex pipe to use for communication.</param>
    public NexusStreamTransport(INexusDuplexPipe pipe)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _writer = new NexusStreamFrameWriter(pipe.Output);
        _reader = new NexusStreamFrameReader(pipe.Input);
    }

    /// <inheritdoc />
    public async ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share = StreamShareMode.None,
        long resumePosition = -1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(resourceId))
            throw new ArgumentNullException(nameof(resourceId));

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check for concurrent open
            if (_state != NexusStreamState.None)
            {
                throw new InvalidOperationException(
                    $"Cannot open a new stream: transport is in {_state} state. " +
                    "Only one stream can be open at a time.");
            }

            // Transition to Opening
            TransitionTo(NexusStreamState.Opening);
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            // Send Open frame
            var openFrame = new OpenFrame(resourceId, access, share, resumePosition);
            await _writer.WriteOpenAsync(openFrame, ct).ConfigureAwait(false);

            // Wait for OpenResponse
            var frameResult = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frameResult == null)
            {
                throw new InvalidOperationException("Connection closed while waiting for open response.");
            }

            var (header, payload) = frameResult.Value;

            // Handle response
            if (header.Type == FrameType.OpenResponse)
            {
                var response = NexusStreamFrameReader.ParseOpenResponse(payload);

                if (!response.Success)
                {
                    // Transition back to None (transport is reusable after non-protocol error)
                    await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        TransitionTo(NexusStreamState.None);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }

                    throw new NexusStreamException(response.ErrorCode, response.ErrorMessage ?? "Open request failed.");
                }

                // Success - create stream and transition to Open
                await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    TransitionTo(NexusStreamState.Open);
                    var initialPosition = resumePosition >= 0 ? resumePosition : 0;
                    _activeStream = new NexusStream(this, response.Metadata, initialPosition);
                    return _activeStream;
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            else if (header.Type == FrameType.Error)
            {
                var error = NexusStreamFrameReader.ParseError(payload);

                // Check if protocol error (requires disconnect)
                if (error.IsProtocolError)
                {
                    await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        TransitionTo(NexusStreamState.Closed);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    throw new NexusStreamException(error.ErrorCode, error.Message, isProtocolError: true);
                }

                // Non-protocol error - transport is reusable
                await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    TransitionTo(NexusStreamState.None);
                }
                finally
                {
                    _stateLock.Release();
                }
                throw new NexusStreamException(error.ErrorCode, error.Message);
            }
            else
            {
                // Unexpected frame type - protocol error
                await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    TransitionTo(NexusStreamState.Closed);
                }
                finally
                {
                    _stateLock.Release();
                }
                throw new NexusStreamException(
                    StreamErrorCode.UnexpectedFrame,
                    $"Expected OpenResponse or Error frame, got {header.Type}.",
                    isProtocolError: true);
            }
        }
        catch (Exception) when (_state == NexusStreamState.Opening)
        {
            // Reset state on failure during opening
            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_state == NexusStreamState.Opening)
                    TransitionTo(NexusStreamState.None);
            }
            finally
            {
                _stateLock.Release();
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<INexusStreamRequest> ReceiveRequestsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state != NexusStreamState.None)
                {
                    // Wait for current stream to close before accepting new requests
                    // In a real implementation, we might want to queue or reject
                    continue;
                }
            }
            finally
            {
                _stateLock.Release();
            }

            var frameResult = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frameResult == null)
            {
                // Connection closed
                yield break;
            }

            var (header, payload) = frameResult.Value;

            if (header.Type == FrameType.Open)
            {
                var openFrame = NexusStreamFrameReader.ParseOpen(payload);
                var request = new NexusStreamRequest(openFrame);

                await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    TransitionTo(NexusStreamState.Opening);
                    _pendingRequest = request; // Store for ProvideFile/ProvideStream
                }
                finally
                {
                    _stateLock.Release();
                }

                yield return request;
            }
            else
            {
                // Unexpected frame - send error
                var error = new ErrorFrame(
                    StreamErrorCode.UnexpectedFrame,
                    0,
                    $"Expected Open frame, got {header.Type}.");
                await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask ProvideFileAsync(string path, FileShare fileShare = FileShare.Read, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        // Determine file access mode based on the pending request
        FileAccess fileAccess;
        if (_pendingRequest != null)
        {
            var access = _pendingRequest.Access;
            fileAccess = access switch
            {
                StreamAccessMode.Read => FileAccess.Read,
                StreamAccessMode.Write => FileAccess.Write,
                StreamAccessMode.ReadWrite => FileAccess.ReadWrite,
                _ => FileAccess.Read
            };
        }
        else
        {
            fileAccess = FileAccess.ReadWrite;
        }

        FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(
                path,
                FileMode.Open,
                fileAccess,
                fileShare,
                bufferSize: 4096,
                useAsync: true);

            // Get file info for metadata
            var fileInfo = new FileInfo(path);

            // Build metadata from file
            var metadata = new NexusStreamMetadata
            {
                Length = fileStream.Length,
                HasKnownLength = true,
                CanSeek = fileStream.CanSeek,
                CanRead = fileStream.CanRead,
                CanWrite = fileStream.CanWrite,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc
            };

            // Handle resume position if requested
            if (_pendingRequest != null && _pendingRequest.ResumePosition >= 0)
            {
                if (_pendingRequest.ResumePosition > fileStream.Length)
                {
                    await SendOpenErrorAsync(StreamErrorCode.InvalidPosition, "Resume position is beyond end of file.", ct).ConfigureAwait(false);
                    return;
                }
                fileStream.Position = _pendingRequest.ResumePosition;
            }

            await ProvideStreamInternalAsync(fileStream, metadata, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await SendOpenErrorAsync(StreamErrorCode.FileNotFound, $"File not found: {path}", ct).ConfigureAwait(false);
            fileStream?.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            await SendOpenErrorAsync(StreamErrorCode.AccessDenied, $"Access denied: {path}", ct).ConfigureAwait(false);
            fileStream?.Dispose();
        }
        catch (IOException ex)
        {
            await SendOpenErrorAsync(StreamErrorCode.IoError, $"IO error: {ex.Message}", ct).ConfigureAwait(false);
            fileStream?.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask ProvideStreamAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        // Build metadata from stream
        var metadata = new NexusStreamMetadata
        {
            Length = stream.CanSeek ? stream.Length : -1,
            HasKnownLength = stream.CanSeek,
            CanSeek = stream.CanSeek,
            CanRead = stream.CanRead,
            CanWrite = stream.CanWrite,
            Created = null,
            Modified = null
        };

        // Handle resume position if requested
        if (_pendingRequest != null && _pendingRequest.ResumePosition >= 0)
        {
            if (!stream.CanSeek)
            {
                await SendOpenErrorAsync(StreamErrorCode.InvalidOperation, "Stream does not support seeking for resume.", ct).ConfigureAwait(false);
                return;
            }
            if (_pendingRequest.ResumePosition > stream.Length)
            {
                await SendOpenErrorAsync(StreamErrorCode.InvalidPosition, "Resume position is beyond end of stream.", ct).ConfigureAwait(false);
                return;
            }
            stream.Position = _pendingRequest.ResumePosition;
        }

        await ProvideStreamInternalAsync(stream, metadata, ct).ConfigureAwait(false);
    }

    private NexusStreamRequest? _pendingRequest;

    /// <summary>
    /// Internal implementation that handles the stream protocol.
    /// </summary>
    private async ValueTask ProvideStreamInternalAsync(Stream stream, NexusStreamMetadata metadata, CancellationToken ct)
    {
        // Send success response with metadata
        await SendOpenResponseAsync(metadata, ct).ConfigureAwait(false);

        var sequenceManager = new SequenceManager();

        try
        {
            // Handle incoming frames until Close received
            while (!ct.IsCancellationRequested)
            {
                var frameResult = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
                if (frameResult == null)
                {
                    // Connection closed
                    break;
                }

                var (header, payload) = frameResult.Value;

                switch (header.Type)
                {
                    case FrameType.Read:
                        await HandleReadFrameAsync(stream, payload, sequenceManager, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Write:
                        await HandleWriteFrameAsync(stream, payload, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Seek:
                        await HandleSeekFrameAsync(stream, payload, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Flush:
                        await HandleFlushFrameAsync(stream, ct).ConfigureAwait(false);
                        break;

                    case FrameType.GetMetadata:
                        await HandleGetMetadataFrameAsync(stream, metadata, ct).ConfigureAwait(false);
                        break;

                    case FrameType.SetLength:
                        await HandleSetLengthFrameAsync(stream, payload, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Close:
                        // Graceful close - exit loop
                        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            TransitionTo(NexusStreamState.None);
                            _pendingRequest = null;
                        }
                        finally
                        {
                            _stateLock.Release();
                        }
                        return;

                    default:
                        // Send error for unexpected frame
                        var error = new ErrorFrame(
                            StreamErrorCode.UnexpectedFrame,
                            stream.CanSeek ? stream.Position : 0,
                            $"Unexpected frame type: {header.Type}");
                        await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            // If stream was provided by ProvideFile, dispose it
            if (stream is FileStream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_state == NexusStreamState.Open)
                {
                    TransitionTo(NexusStreamState.None);
                }
                _pendingRequest = null;
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    private async ValueTask HandleReadFrameAsync(Stream stream, System.Buffers.ReadOnlySequence<byte> payload, SequenceManager sequenceManager, CancellationToken ct)
    {
        var readFrame = NexusStreamFrameReader.ParseRead(payload);
        var bytesToRead = readFrame.Count;

        if (!stream.CanRead)
        {
            var error = new ErrorFrame(StreamErrorCode.InvalidOperation, stream.CanSeek ? stream.Position : 0, "Stream does not support reading.");
            await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
            return;
        }

        // Read data from stream
        var buffer = new byte[bytesToRead];
        var totalRead = 0;

        try
        {
            totalRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            var error = new ErrorFrame(StreamErrorCode.IoError, stream.CanSeek ? stream.Position : 0, $"Read error: {ex.Message}");
            await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
            return;
        }

        // Send Data frames for the read data
        if (totalRead > 0)
        {
            await _writer.WriteDataChunksUnlockedAsync(buffer.AsMemory(0, totalRead), sequenceManager, ct).ConfigureAwait(false);
        }

        // Send DataEnd frame
        var finalSequence = sequenceManager.NextToSend > 0 ? sequenceManager.NextToSend - 1 : 0;
        var dataEndFrame = new DataEndFrame(totalRead, finalSequence);
        await _writer.WriteDataEndAsync(dataEndFrame, ct).ConfigureAwait(false);
    }

    private async ValueTask HandleWriteFrameAsync(Stream stream, System.Buffers.ReadOnlySequence<byte> payload, CancellationToken ct)
    {
        var writeFrame = NexusStreamFrameReader.ParseWrite(payload);
        var bytesToWrite = writeFrame.Count;

        if (!stream.CanWrite)
        {
            var error = new ErrorFrame(StreamErrorCode.InvalidOperation, stream.CanSeek ? stream.Position : 0, "Stream does not support writing.");
            await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
            // Still need to drain the incoming data frames
            await DrainIncomingDataFramesAsync(bytesToWrite, ct).ConfigureAwait(false);
            return;
        }

        // Receive Data frames and write to stream
        var totalReceived = 0;
        var sequenceManager = new SequenceManager();

        while (totalReceived < bytesToWrite)
        {
            var frameResult = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frameResult == null)
            {
                throw new InvalidOperationException("Connection closed while waiting for data.");
            }

            var (header, dataPayload) = frameResult.Value;

            if (header.Type == FrameType.Data)
            {
                NexusStreamFrameReader.ParseDataHeader(dataPayload, out var sequence, out var dataLength);
                sequenceManager.ValidateReceivedOrThrow(sequence);

                // Write to stream
                var dataBuffer = new byte[dataLength];
                var dataFrame = NexusStreamFrameReader.ParseData(dataPayload, dataBuffer);

                try
                {
                    await stream.WriteAsync(dataBuffer.AsMemory(0, dataLength), ct).ConfigureAwait(false);
                    totalReceived += dataLength;
                }
                catch (IOException ex)
                {
                    var error = new ErrorFrame(StreamErrorCode.IoError, stream.CanSeek ? stream.Position : 0, $"Write error: {ex.Message}");
                    await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
                    return;
                }
            }
            else if (header.Type == FrameType.DataEnd)
            {
                var dataEndFrame = NexusStreamFrameReader.ParseDataEnd(dataPayload);
                if (dataEndFrame.TotalBytes != totalReceived)
                {
                    var error = new ErrorFrame(StreamErrorCode.ProtocolError, stream.CanSeek ? stream.Position : 0, "DataEnd total bytes mismatch.");
                    await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
                    return;
                }
                break;
            }
            else
            {
                var error = new ErrorFrame(StreamErrorCode.UnexpectedFrame, stream.CanSeek ? stream.Position : 0, $"Expected Data or DataEnd frame, got {header.Type}.");
                await _writer.WriteErrorAsync(error, ct).ConfigureAwait(false);
                return;
            }
        }

        // Send WriteResponse
        var response = new WriteResponseFrame(totalReceived, stream.CanSeek ? stream.Position : 0);
        await _writer.WriteWriteResponseAsync(response, ct).ConfigureAwait(false);
    }

    private async ValueTask HandleSeekFrameAsync(Stream stream, System.Buffers.ReadOnlySequence<byte> payload, CancellationToken ct)
    {
        var seekFrame = NexusStreamFrameReader.ParseSeek(payload);

        if (!stream.CanSeek)
        {
            var response = new SeekResponseFrame(StreamErrorCode.InvalidOperation, 0);
            await _writer.WriteSeekResponseAsync(response, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var newPosition = stream.Seek(seekFrame.Offset, seekFrame.Origin);
            var response = new SeekResponseFrame(newPosition);
            await _writer.WriteSeekResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            var response = new SeekResponseFrame(StreamErrorCode.IoError, stream.Position);
            await _writer.WriteSeekResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            var response = new SeekResponseFrame(StreamErrorCode.InvalidPosition, stream.Position);
            await _writer.WriteSeekResponseAsync(response, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleFlushFrameAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            await stream.FlushAsync(ct).ConfigureAwait(false);
            var response = new FlushResponseFrame(stream.CanSeek ? stream.Position : 0);
            await _writer.WriteFlushResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            var response = new FlushResponseFrame(StreamErrorCode.IoError, stream.CanSeek ? stream.Position : 0);
            await _writer.WriteFlushResponseAsync(response, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleGetMetadataFrameAsync(Stream stream, NexusStreamMetadata baseMetadata, CancellationToken ct)
    {
        // Update metadata with current stream state
        var metadata = new NexusStreamMetadata
        {
            Length = stream.CanSeek ? stream.Length : baseMetadata.Length,
            HasKnownLength = stream.CanSeek || baseMetadata.HasKnownLength,
            CanSeek = stream.CanSeek,
            CanRead = stream.CanRead,
            CanWrite = stream.CanWrite,
            Created = baseMetadata.Created,
            Modified = baseMetadata.Modified
        };

        var response = new MetadataResponseFrame(metadata);
        await _writer.WriteMetadataResponseAsync(response, ct).ConfigureAwait(false);
    }

    private async ValueTask HandleSetLengthFrameAsync(Stream stream, System.Buffers.ReadOnlySequence<byte> payload, CancellationToken ct)
    {
        var setLengthFrame = NexusStreamFrameReader.ParseSetLength(payload);

        if (!stream.CanWrite || !stream.CanSeek)
        {
            var response = new SetLengthResponseFrame(StreamErrorCode.InvalidOperation, stream.CanSeek ? stream.Length : 0, stream.CanSeek ? stream.Position : 0);
            await _writer.WriteSetLengthResponseAsync(response, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            stream.SetLength(setLengthFrame.Length);
            var response = new SetLengthResponseFrame(stream.Length, stream.Position);
            await _writer.WriteSetLengthResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            var response = new SetLengthResponseFrame(StreamErrorCode.IoError, stream.Length, stream.Position);
            await _writer.WriteSetLengthResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            var response = new SetLengthResponseFrame(StreamErrorCode.InvalidOperation, stream.Length, stream.Position);
            await _writer.WriteSetLengthResponseAsync(response, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask DrainIncomingDataFramesAsync(int expectedBytes, CancellationToken ct)
    {
        var totalReceived = 0;
        while (totalReceived < expectedBytes)
        {
            var frameResult = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frameResult == null) break;

            var (header, _) = frameResult.Value;
            if (header.Type == FrameType.DataEnd) break;
        }
    }

    /// <summary>
    /// Closes the active stream.
    /// </summary>
    /// <param name="graceful">Whether this is a graceful close.</param>
    internal async ValueTask CloseStreamAsync(bool graceful)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != NexusStreamState.Open && _state != NexusStreamState.Opening)
                return;

            try
            {
                var closeFrame = new CloseFrame(graceful);
                await _writer.WriteCloseAsync(closeFrame, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore write errors during close
            }

            TransitionTo(NexusStreamState.None);
            _activeStream = null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Sends an OpenResponse frame with success and metadata.
    /// </summary>
    internal async ValueTask SendOpenResponseAsync(NexusStreamMetadata metadata, CancellationToken ct = default)
    {
        var response = new OpenResponseFrame(metadata);
        await _writer.WriteOpenResponseAsync(response, ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TransitionTo(NexusStreamState.Open);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Sends an OpenResponse frame with an error.
    /// </summary>
    internal async ValueTask SendOpenErrorAsync(StreamErrorCode errorCode, string message, CancellationToken ct = default)
    {
        var response = new OpenResponseFrame(errorCode, message);
        await _writer.WriteOpenResponseAsync(response, ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TransitionTo(NexusStreamState.None);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Writes a Read frame.
    /// </summary>
    internal ValueTask WriteFrameAsync(ReadFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteReadAsync(frame, ct);
    }

    /// <summary>
    /// Writes a Write frame.
    /// </summary>
    internal ValueTask WriteFrameAsync(WriteFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteWriteAsync(frame, ct);
    }

    /// <summary>
    /// Writes a DataEnd frame.
    /// </summary>
    internal ValueTask WriteFrameAsync(DataEndFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteDataEndAsync(frame, ct);
    }

    /// <summary>
    /// Writes chunked data frames.
    /// </summary>
    internal ValueTask WriteDataChunksAsync(ReadOnlyMemory<byte> data, SequenceManager sequenceManager, CancellationToken ct = default)
    {
        return _writer.WriteDataChunksUnlockedAsync(data, sequenceManager, ct);
    }

    /// <summary>
    /// Reads the next frame from the transport.
    /// </summary>
    internal ValueTask<(FrameHeader Header, System.Buffers.ReadOnlySequence<byte> Payload)?> ReadFrameAsync(CancellationToken ct = default)
    {
        return _reader.ReadFrameAsync(ct);
    }

    /// <summary>
    /// Writes a Seek frame.
    /// </summary>
    internal ValueTask WriteFrameAsync(SeekFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteSeekAsync(frame, ct);
    }

    /// <summary>
    /// Writes a Flush frame.
    /// </summary>
    internal ValueTask WriteFlushAsync(CancellationToken ct = default)
    {
        return _writer.WriteFlushAsync(ct);
    }

    /// <summary>
    /// Writes a SetLength frame.
    /// </summary>
    internal ValueTask WriteFrameAsync(SetLengthFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteSetLengthAsync(frame, ct);
    }

    /// <summary>
    /// Writes a GetMetadata frame.
    /// </summary>
    internal ValueTask WriteGetMetadataAsync(CancellationToken ct = default)
    {
        return _writer.WriteGetMetadataAsync(ct);
    }

    /// <summary>
    /// Writes a MetadataResponse frame.
    /// </summary>
    internal ValueTask WriteMetadataResponseAsync(MetadataResponseFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteMetadataResponseAsync(frame, ct);
    }

    /// <summary>
    /// Writes a Progress frame.
    /// </summary>
    internal ValueTask WriteProgressAsync(ProgressFrame frame, CancellationToken ct = default)
    {
        return _writer.WriteProgressAsync(frame, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeStream != null)
            {
                try
                {
                    await CloseStreamAsync(graceful: false).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore errors during disposal
                }
            }

            TransitionTo(NexusStreamState.Closed);
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }

    /// <summary>
    /// Validates and performs a state transition.
    /// </summary>
    /// <param name="newState">The state to transition to.</param>
    /// <exception cref="InvalidOperationException">If the transition is not valid.</exception>
    private void TransitionTo(NexusStreamState newState)
    {
        ValidateTransition(_state, newState);
        _state = newState;
    }

    /// <summary>
    /// Validates that a state transition is allowed.
    /// </summary>
    private static void ValidateTransition(NexusStreamState from, NexusStreamState to)
    {
        var valid = (from, to) switch
        {
            // From None: can go to Opening or Closed
            (NexusStreamState.None, NexusStreamState.Opening) => true,
            (NexusStreamState.None, NexusStreamState.Closed) => true,

            // From Opening: can go to Open, None (on error), or Closed
            (NexusStreamState.Opening, NexusStreamState.Open) => true,
            (NexusStreamState.Opening, NexusStreamState.None) => true,
            (NexusStreamState.Opening, NexusStreamState.Closed) => true,

            // From Open: can go to None (stream closed) or Closed (transport closing)
            (NexusStreamState.Open, NexusStreamState.None) => true,
            (NexusStreamState.Open, NexusStreamState.Closed) => true,

            // From Closed: no transitions allowed
            (NexusStreamState.Closed, _) => false,

            // All other transitions are invalid
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Invalid state transition from {from} to {to}.");
        }
    }
}
