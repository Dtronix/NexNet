using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Internals.Pipes;

/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
internal class NexusDuplexPipe : INexusDuplexPipe, IPipeStateManager
{
    /// <summary>
    /// Represents the state of a duplex pipe.
    /// </summary>
    [Flags]
    internal enum State : byte
    {
        /// <summary>
        /// Unset state.
        /// </summary>
        /// 
        Unset = 0,

        /// <summary>
        /// Client writer has completed its operation.
        /// </summary>
        ClientWriterServerReaderComplete = 1 << 0,

        /// <summary>
        /// Client reader has completed its operation.
        /// </summary>
        ClientReaderServerWriterComplete = 1 << 1,

        /// <summary>
        /// The connection is ready.
        /// </summary>
        Ready = 1 << 2,

        /// <summary>
        /// Client has reached it's buffer limit which notifies the server to stop sending data.
        /// </summary>
        ClientReaderBackPressure = 1 << 3,

        /// <summary>
        /// Server has reached it's buffer limit which notifies the client to stop sending data.
        /// </summary>
        ServerReaderBackPressure = 1 << 4,
        
        /// <summary>
        /// Marks the state to indicate that the communication session has completed its operation.
        /// </summary>
        Complete = ClientWriterServerReaderComplete
                   | ClientReaderServerWriterComplete
                   | Ready
    }

    //private readonly Pipe _inputPipe = new Pipe();
    private readonly NexusPipeReader _inputNexusPipeReader;
    private readonly NexusPipeWriter _outputPipeWriter;
    private TaskCompletionSource? _readyTcs;

    private State _currentState = State.Unset;

    private INexusSession? _session;

    // <summary>
    // Id which changes based upon completion of the pipe. Used to make sure the
    // Pipe is in the same state upon completion of writing/reading.
    // </summary>
    //internal int StateId;

    /// <summary>
    /// Complete compiled ID containing the client bit and the server bit.
    /// </summary>
    public ushort Id { get; set; }

    /// <summary>
    /// Initial ID of this side of the connection.
    /// </summary>
    public byte InitialId { get; set; }

    /// <summary>
    /// Gets the pipe reader for this connection.
    /// </summary>
    public PipeReader Input => _inputNexusPipeReader;

    /// <summary>
    /// Gets the pipe writer for this connection.
    /// </summary>
    public PipeWriter Output => _outputPipeWriter;

    /// <summary>
    /// Method invoked on a LongRunning task when the pipe is ready for usage on the invoking side.
    /// </summary>
    private Func<INexusDuplexPipe, ValueTask>? _onReady;

    /// <summary>
    /// Task which completes when the pipe is ready for usage on the invoking side.
    /// </summary>
    public Task ReadyTask => _readyTcs!.Task;

    /// <summary>
    /// State of the pipe.
    /// </summary>
    public State CurrentState
    {
        get => _currentState;
        set => _currentState = value;
    }

    /// <summary>
    /// Logger.
    /// </summary>
    private INexusLogger? _logger;

    /// <summary>
    /// True if the pipe is in the cache.
    /// </summary>
    internal bool IsInCached = false;

    internal NexusDuplexPipe()
    {
        _inputNexusPipeReader = new NexusPipeReader(this);
        _outputPipeWriter = new NexusPipeWriter(this);
    }

    public ValueTask CompleteAsync()
    {
        var stateChanged = UpdateState(State.Complete);
        if (stateChanged)
            return NotifyState();

        _session?.CacheManager.NexusDuplexPipeCache.Return(this);

        return default;
    }

    /// <summary>
    /// Sets up the duplex pipe with the given parameters.
    /// </summary>
    /// <param name="initialId">The partial initial identifier.</param>
    /// <param name="session">The Nexus session.</param>
    /// <param name="onReady">The callback when the pipe is ready.</param>

    public void Setup(byte initialId, INexusSession session, Func<INexusDuplexPipe, ValueTask>? onReady)
    {
        if (_currentState != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _readyTcs = new TaskCompletionSource();
        _onReady = onReady;
        _session = session;
        _logger = session.Logger;
        InitialId = initialId;
        _outputPipeWriter.Setup(_session);
        _inputNexusPipeReader.Setup(
            _session.IsServer, 
            _session.Config.NexusPipeHighWaterCutoff,
            _session.Config.NexusPipeLowWaterMark);
    }


    /// <summary>
    /// Signals that the pipe is ready to receive and send messages.
    /// </summary>
    /// <param name="id">The ID of the pipe.</param>
    public ValueTask PipeReady(ushort id)
    {
        if (_session == null)
            return default;

        Id = id;

        if (UpdateState(State.Ready))
            return NotifyState();

        return default;
    }
    
    /// <summary>
    /// Resets the pipe to its initial state while incrementing the StateId to indicate that the pipe has been reset.
    /// </summary>
    public void Reset()
    {
        // Close everything.
        UpdateState(State.Complete);

        InitialId = 0;
        _currentState = State.Unset;
        _onReady = null;

        // Set the task to canceled in case the pipe was reset before it was ready.
        _readyTcs?.TrySetCanceled();
        _readyTcs = null;

        _inputNexusPipeReader.Reset();
        _outputPipeWriter.Reset();
        // No need to reset anything with the writer as it is use once and dispose.
    }

    /// <summary>
    /// Writes data from upstream reader into the input pipe
    /// </summary>
    /// <param name="data">The data to write from upstream reader</param>
    /// <returns>True if the data was written successfully, false if the pipe has reached its high water cutoff.</returns></returns>
    public NexusPipeBufferResult WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return NexusPipeBufferResult.DataIgnored;

        // See if this pipe is accepting data.
        if ((_session.IsServer && _currentState.HasFlag(State.ClientWriterServerReaderComplete))
            || (!_session.IsServer && _currentState.HasFlag(State.ClientReaderServerWriterComplete)))
            return NexusPipeBufferResult.DataIgnored;

        var bufferedLength = _inputNexusPipeReader.BufferData(data);

        if (bufferedLength >= _session.Config.NexusPipeHighWaterCutoff)
        {
            _session.Logger?.LogWarning(
                $"Pipe {Id} has buffered {bufferedLength} bytes of data and exceed the high water cutoff of {_session.Config.NexusPipeHighWaterCutoff}");

            return NexusPipeBufferResult.HighCutoffReached;
        }
        else if (bufferedLength >= _session.Config.NexusPipeHighWaterMark)
        {
            _session.Logger?.LogInfo(
                $"Pipe {Id} has buffered {bufferedLength} bytes of data and exceed the high water of {_session.Config.NexusPipeHighWaterMark}");

            return NexusPipeBufferResult.HighWatermarkReached;
        }

        return NexusPipeBufferResult.Success;
    }

    public async ValueTask NotifyState()
    {
        if(_session == null)
            return;

        var message = _session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.State = _currentState;
        await _session.SendMessage(message).ConfigureAwait(false);
        _session.CacheManager.Return(message);

        if (_currentState == State.Complete)
            _session.CacheManager.NexusDuplexPipeCache.Return(this);
    }

    /// <summary>
    /// Updates the state of the NexusDuplexPipe .
    /// </summary>
    /// <param name="updatedState">The state to update to.</param>
    /// <returns>True if the state changed, false if it did not change.</returns>
    public bool UpdateState(State updatedState, bool remove = false)
    {
        _logger?.LogTrace($"Current State: {_currentState}; Update State: {updatedState}");
        if (_session == null || _currentState.HasFlag(updatedState))
            return false;


        if (updatedState == State.Ready)
        {
            _currentState = State.Ready;
            if (_onReady != null)
                TaskUtilities.StartTask<NexusDuplexPipe>(new(_onReady, this));

            // Set the ready task.
            _readyTcs?.TrySetResult();

            return true;
        }

        bool changed = false;

        if (HasState(updatedState, _currentState, State.ClientReaderServerWriterComplete))
        {
            if (_session.IsServer)
            {
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
            else
            {
                // Close input pipe.
                _inputNexusPipeReader.Complete();
            }

            changed = true;
        }

        if (HasState(updatedState, _currentState, State.ClientWriterServerReaderComplete))
        {
            if (_session.IsServer)
            {
                // Close input pipe.
                _inputNexusPipeReader.Complete();

            }
            else
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }

            changed = true;
        }

        // Back pressure
        if (HasState(updatedState, _currentState, State.ClientReaderBackPressure) && _session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && _currentState.HasFlag(State.ClientReaderBackPressure)
                        && updatedState.HasFlag(State.ClientReaderBackPressure)
                        && _session.IsServer)
        {
            // Back pressure was removed.
            _outputPipeWriter.PauseWriting = false;
        }

        if (HasState(updatedState, _currentState, State.ServerReaderBackPressure) && !_session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && _currentState.HasFlag(State.ServerReaderBackPressure)
                        && updatedState.HasFlag(State.ServerReaderBackPressure)
                        && !_session.IsServer)
        {
            // Back pressure was removed.
            _outputPipeWriter.PauseWriting = false;
        }

        if (remove)
        {          
            // Remove the state from the current state.
            _currentState &= ~updatedState;
        }
        else
        {
            // Add the state to the current state.
            _currentState |= updatedState;
        }

        return changed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasState(State has, State notHave, State flag)
    {
        return has.HasFlag(flag) && !notHave.HasFlag(flag);
    }
}
