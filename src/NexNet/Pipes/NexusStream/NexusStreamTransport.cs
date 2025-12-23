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
    public ValueTask ProvideFileAsync(string path, CancellationToken ct = default)
    {
        throw new NotImplementedException("ProvideFile not implemented until Phase 6.");
    }

    /// <inheritdoc />
    public ValueTask ProvideStreamAsync(Stream stream, CancellationToken ct = default)
    {
        throw new NotImplementedException("ProvideStream not implemented until Phase 6.");
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
