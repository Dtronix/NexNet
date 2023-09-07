﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Pipes;

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
        ClientWriterPause = 1 << 3,

        /// <summary>
        /// Server has reached it's buffer limit which notifies the client to stop sending data.
        /// </summary>
        ServerWriterPause = 1 << 4,
        
        /// <summary>
        /// Marks the state to indicate that the communication session has completed its operation.
        /// </summary>
        Complete = ClientWriterServerReaderComplete
                   | ClientReaderServerWriterComplete
                   | Ready
    }

    //private readonly Pipe _inputPipe = new Pipe();
    private readonly NexusPipeReader _inputPipeReader;
    private readonly NexusPipeWriter _outputPipeWriter;
    private TaskCompletionSource? _readyTcs;


    private State _currentState = State.Unset;

    private INexusSession? _session;

    // <summary>
    // Id which changes based upon completion of the pipe. Used to make sure the
    // Pipe is in the same state upon completion of writing/reading.
    // </summary>
    internal int StateId;

    /// <summary>
    /// True if this pipe is the pipe used for initiating the pipe connection.
    /// </summary>
    internal bool InitiatingPipe;

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
    public PipeReader Input => _inputPipeReader;

    /// <summary>
    /// Gets the pipe writer for this connection.
    /// </summary>
    public PipeWriter Output => _outputPipeWriter;

    /// <summary>
    /// Task which completes when the pipe is ready for usage on the invoking side.
    /// </summary>
    public Task ReadyTask => _readyTcs!.Task;

    /// <summary>
    /// State of the pipe.
    /// </summary>
    public State CurrentState => _currentState;

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
        _inputPipeReader = new NexusPipeReader(this);
        _outputPipeWriter = new NexusPipeWriter(this);
    }

    public ValueTask CompleteAsync()
    {
        if (_currentState == State.Complete)
            return default;

        var stateChanged = UpdateState(State.Complete);
        if (stateChanged)
            return NotifyState();

        return default;
    }

    NexusPipeWriter INexusDuplexPipe.WriterCore => _outputPipeWriter;
    NexusPipeReader INexusDuplexPipe.ReaderCore => _inputPipeReader;

    /// <summary>
    /// Sets up the duplex pipe with the given parameters.
    /// </summary>
    /// <param name="initialId">The partial initial identifier.</param>
    /// <param name="session">The Nexus session.</param>
    public void Setup(byte initialId, INexusSession session)
    {
        if (_currentState != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session = session;
        _logger = session.Logger?.CreateLogger<NexusDuplexPipe>();
        InitialId = initialId;
        _outputPipeWriter.Setup(
            _logger,
            _session,
            _session.IsServer,
            _session.Config.NexusPipeFlushChunkSize);

        _inputPipeReader.Setup(
            _logger,
            _session.IsServer,
            _session.Config.NexusPipeHighWaterMark,
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
        if(_currentState != State.Complete)
            UpdateState(State.Complete);

        InitialId = 0;
        _currentState = State.Unset;
        InitiatingPipe = false;

        // Set the task to canceled in case the pipe was reset before it was ready.
        _readyTcs?.TrySetCanceled();

        _inputPipeReader.Reset();
        _outputPipeWriter.Reset();

        Interlocked.Increment(ref StateId);
    }

    /// <summary>
    /// Writes data from upstream reader into the input pipe
    /// </summary>
    /// <param name="data">The data to write from upstream reader</param>
    /// <returns>Result of the buffering.</returns>
    public ValueTask<NexusPipeBufferResult> WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);

        var currentState = _currentState;
        // See if this pipe is accepting data.
        if ((_session.IsServer && currentState.HasFlag(State.ClientWriterServerReaderComplete))
            || (!_session.IsServer && currentState.HasFlag(State.ClientReaderServerWriterComplete)))
            return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);

        return _inputPipeReader.BufferData(data);
    }


    /// <summary>
    /// Notifies the current state of the duplex pipe to the associated session.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask NotifyState()
    {
        if(_session == null)
            return;

        //_logger?.LogInfo($"Notifying state: {_currentState}");
        var currentState = _currentState;
        using var message = _session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.State = currentState;
        try
        {
            await _session.SendMessage(message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Possible that the session was closed before we could send the message.
            _logger?.LogInfo(e, $"Exception occurred while updating state to: {currentState}. Closing pipe.");

            // Close the pipe.
            _currentState = State.Complete;
        }
    }

    /// <summary>
    /// Updates the state of the NexusDuplexPipe.
    /// </summary>
    /// <param name="updatedState">The state to update to.</param>
    /// <param name="remove">True to remove the state, false to add the state.</param>
    /// <returns>True if the state changed, false if it did not change.</returns>
    public bool UpdateState(State updatedState, bool remove = false)
    {
        // Get a copy of the current state.
        var currentState = _currentState;
        if (_session == null
            || (!remove && currentState.HasFlag(updatedState))
            || (remove && !currentState.HasFlag(updatedState)))
        {
            _logger?.LogTrace($"Current State: {currentState}; Canceled state update of: {updatedState} because it is already set.");
            return false;
        }

        _logger?.LogTrace($"Current State: {currentState}; Update State: {updatedState}");

        if (currentState == State.Unset)
        {
            if (updatedState == State.Ready)
            {
                _currentState = State.Ready;

                // Set the ready task.
                _readyTcs?.TrySetResult();

                return true;
            }
            else
            {
                // If we are not ready, then we can't update the state.
                // This normally happens when the pipe is reset before it is ready or after it has been reset.
                // Honestly, we shouldn't reach here.
                _logger?.LogTrace($"Ignored update state of : {updatedState} because the pipe was never readied.");
                _readyTcs?.TrySetCanceled();
                return false;
            }
        }

        if (HasState(updatedState, currentState, State.ClientReaderServerWriterComplete))
        {
            if (_session.IsServer)
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
            else
            {
                // Close input pipe.
                _inputPipeReader.Complete();
            }
        }

        if (HasState(updatedState, currentState, State.ClientWriterServerReaderComplete))
        {
            if (_session.IsServer)
            {
                // Close input pipe.
                _inputPipeReader.Complete();
            }
            else
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
        }

        // Back pressure
        if (HasState(updatedState, currentState, State.ClientWriterPause) && _session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && currentState.HasFlag(State.ClientWriterPause)
                        && updatedState.HasFlag(State.ClientWriterPause)
                        && _session.IsServer)
        {
            // Back pressure was removed.
            _outputPipeWriter.PauseWriting = false;
        }

        if (HasState(updatedState, currentState, State.ServerWriterPause) && !_session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && currentState.HasFlag(State.ServerWriterPause)
                        && updatedState.HasFlag(State.ServerWriterPause)
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

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasState(State has, State notHave, State flag)
    {
        return has.HasFlag(flag) && !notHave.HasFlag(flag);
    }
}