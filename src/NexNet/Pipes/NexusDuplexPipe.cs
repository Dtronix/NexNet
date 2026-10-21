using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes;

/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
internal class NexusDuplexPipe : INexusDuplexPipe, IPipeStateManager, IDisposable
{
    /// <summary>
    /// Represents the state of a duplex pipe.
    /// </summary>
    [Flags]
    internal enum State : byte
    {
        /// <summary>
        /// Unset state. This is the default state and indicates that the pipe is ready for setup
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
        /// The open was refused because a session limit was reached (budget or pipe count). Sent instead of Ready.
        /// </summary>
        Refused = 1 << 3,
        
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
    private readonly TaskCompletionSource _readyTcs;
    private readonly TaskCompletionSource _completeTcs;
    private readonly INexusSession? _session;
    private volatile State _currentState = State.Unset;
    private readonly object _stateLock = new();

    // Two-sided close (docs/internals/flow-control.md, section 4.6). The pipe is freed once this side sent its
    // Complete, received the peer's, and the application released it. Between sending and receiving it is Closing.
    private int _sentClose;
    private int _receivedClose;
    private int _released;
    private int _freed;
    private long _closingSince;

    // Control state drained by the session's control sender. Coalesced: one pending grant counter, one pending close
    // acknowledgement, and at most one queued entry per pipe.
    private int _pendingGrant;
    private int _pendingCloseAck;
    private int _controlQueued;
    private uint _readyWindow;
    private readonly byte[] _windowUpdateBody = new byte[sizeof(uint)];
    private readonly byte[] _pipeIdHeader = new byte[sizeof(ushort)];

    // <summary>
    // Id which changes based upon completion of the pipe. Used to make sure the
    // Pipe is in the same state upon completion of writing/reading.
    // </summary>
    //internal int StateId;

    /// <summary>
    /// True if this pipe is the pipe used for initiating the pipe connection.
    /// </summary>
    internal readonly bool InitiatingPipe;

    /// <summary>
    /// Complete compiled ID containing the client bit and the server bit.
    /// </summary>
    public ushort Id
    {
        get => _id;
        set
        {
            _id = value;
            if (Logger != null)
                Logger.PathSegment = $"P{_id:00000}";
        }
    }

    /// <summary>
    /// Initial ID of this side of the connection.
    /// </summary>
    public byte LocalId { get; }

    public PipeReader Input => _inputPipeReader;

    public PipeWriter Output => _outputPipeWriter;

    public Task ReadyTask => _readyTcs.Task;

    public Task CompleteTask => _completeTcs.Task;

    public State CurrentState => _currentState;

    /// <summary>
    /// Session associated with this pipe.
    /// </summary>
    public INexusSession? Session => _session;

    /// <summary>
    /// Logger.
    /// </summary>
    protected INexusLogger? Logger;

    private ushort _id;

    NexusPipeWriter INexusDuplexPipe.WriterCore => _outputPipeWriter;
    NexusPipeReader INexusDuplexPipe.ReaderCore => _inputPipeReader;

    internal NexusDuplexPipe(ushort fullId, byte localId, INexusSession session)
    {
        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session = session;

        var id = fullId == 0
            ? $"{localId:000}"
            : $"{fullId:00000}";
        Logger = session.Logger?.CreateLogger($"P{id}");

        LocalId = localId;
        _id = fullId;

        if(fullId == 0)
            InitiatingPipe = true;

        _inputPipeReader = new NexusPipeReader(
            this,
            Logger,
            _session.IsServer,
            _session.Config.PipeWindow,
            _session.Config.Time);

        _outputPipeWriter = new NexusPipeWriter(
            this,
            Logger,
            _session,
            _session.IsServer,
            NexusPipeWriter.MaxChunkSize,
            _session.Config.Time);
    }

    /// <summary>
    /// Snapshot of this pipe's state; <paramref name="nowTicks"/> is the session's current tick count.
    /// </summary>
    internal NexusPipeSnapshot GetSnapshot(long nowTicks)
    {
        var reader = _inputPipeReader.GetState();
        var (waitSince, waitingBytes) = _outputPipeWriter.CreditWait;
        var closing = Volatile.Read(ref _sentClose) == 1 && Volatile.Read(ref _receivedClose) == 0;
        return new NexusPipeSnapshot
        {
            Id = Id != 0 ? Id : LocalId,
            IsReady = (_currentState & State.Ready) != 0,
            ReaderCompleted = reader.IsCompleted,
            WriterCompleted = _outputPipeWriter.IsCompleted,
            Closing = closing ? Since(nowTicks, Volatile.Read(ref _closingSince)) : null,
            Window = _inputPipeReader.Window,
            ReceiveCredit = reader.Credit,
            Buffered = reader.Buffered,
            ConsumedNotGranted = reader.ConsumedSinceGrant,
            ExtraOutstanding = reader.ExtraOutstanding,
            SinceLastData = reader.LastDataTicks == 0 ? null : Since(nowTicks, reader.LastDataTicks),
            PartialItem = reader.PendingItemLength == 0
                ? null
                : new NexusPartialItemSnapshot { Length = reader.PendingItemLength, Received = reader.PendingItemReceived },
            SendCredit = _outputPipeWriter.Credit,
            WaitingForCredit = waitSince == 0 ? null : Since(nowTicks, waitSince),
            WaitingBytes = waitingBytes,
        };
    }

    private static TimeSpan Since(long nowTicks, long ticks) => TimeSpan.FromMilliseconds(Math.Max(0, nowTicks - ticks));

    /// <summary>
    /// Bytes reserved from the session budget for this pipe's receive window.
    /// </summary>
    internal int ReservedWindow => _inputPipeReader.Window;

    /// <summary>
    /// True once this side sent its Complete, received the peer's, and the application released the pipe.
    /// </summary>
    internal bool IsFullyClosed =>
        Volatile.Read(ref _sentClose) == 1 && Volatile.Read(ref _receivedClose) == 1 && Volatile.Read(ref _released) == 1;

    /// <summary>
    /// Marks the pipe freed. Returns true only for the first call.
    /// </summary>
    internal bool TryMarkFreed() => Interlocked.Exchange(ref _freed, 1) == 0;

    /// <summary>
    /// Records that the application released the pipe.
    /// </summary>
    internal void MarkReleased() => Volatile.Write(ref _released, 1);

    /// <summary>
    /// True if this side sent its Complete more than <paramref name="timeoutTicks"/> ago and the peer has not
    /// answered.
    /// </summary>
    internal bool IsCloseStalled(long nowTicks, long timeoutTicks) =>
        Volatile.Read(ref _sentClose) == 1
        && Volatile.Read(ref _receivedClose) == 0
        && nowTicks - Volatile.Read(ref _closingSince) > timeoutTicks;

    /// <summary>
    /// True if an item larger than the window got extra credit and its data stopped arriving.
    /// </summary>
    internal bool IsItemStalled(long nowTicks, long timeoutTicks) => _inputPipeReader.IsItemStalled(nowTicks, timeoutTicks);

    /// <summary>
    /// Registering side: opens the receive window and sends Ready with it.
    /// </summary>
    internal ValueTask SendReady()
    {
        _inputPipeReader.OpenWindow();
        _readyWindow = (uint)_inputPipeReader.Window;
        UpdateState(State.Ready);
        return NotifyState();
    }

    /// <summary>
    /// Handles a state message from the peer. Returns a disconnect reason if the message breaks the protocol.
    /// </summary>
    internal DisconnectReason OnRemoteState(State state, uint window)
    {
        if ((state & State.Refused) != 0)
        {
            // Only meaningful for a pipe this side opened and the peer has not acknowledged.
            if (_currentState == State.Unset)
                OnRefused();
            return DisconnectReason.None;
        }

        if (window > 0 && !_outputPipeWriter.AddCredit(window))
            return DisconnectReason.FlowControl;

        var wasUnset = _currentState == State.Unset;
        UpdateState(state);

        // Initiating side: the peer's Ready carried its window; grant ours in return.
        if (wasUnset && (state & State.Ready) != 0 && _session != null)
        {
            _inputPipeReader.OpenWindow();
            GrantCredit(_inputPipeReader.Window);
        }

        if (state == State.Complete && Interlocked.Exchange(ref _receivedClose, 1) == 0
                                    && Volatile.Read(ref _sentClose) == 0)
        {
            // Answer the peer's close with ours so both sides can free the pipe.
            Volatile.Write(ref _pendingCloseAck, 1);
            RequestControl();
        }

        return DisconnectReason.None;
    }

    /// <summary>
    /// Initiating side: the peer refused the open.
    /// </summary>
    internal void OnRefused()
    {
        _session?.MetricsTags.Metrics.RecordPipeRefusal(_session.MetricsTags, local: false);
        _readyTcs.TrySetException(new NexusBusyException("The peer refused the pipe: a session limit is reached."));
        _completeTcs.TrySetResult();
        MarkClosedWithoutExchange();
    }

    /// <summary>
    /// The peer never opened the pipe, so there is no close to exchange.
    /// </summary>
    internal void MarkClosedWithoutExchange()
    {
        Volatile.Write(ref _sentClose, 1);
        Volatile.Write(ref _receivedClose, 1);
    }

    /// <summary>
    /// Adds send credit granted by the peer. Returns false if the total would exceed the protocol's maximum.
    /// </summary>
    internal bool AddCredit(uint bytes) => _outputPipeWriter.AddCredit(bytes);

    /// <summary>
    /// Sends this side's Complete if it has not been sent.
    /// </summary>
    internal ValueTask CloseAsync()
    {
        if (_currentState == State.Unset || Volatile.Read(ref _sentClose) == 1)
            return default;

        UpdateState(State.Complete);
        return NotifyState();
    }

    // Budget reserved for items larger than the window; released as the items are consumed or when the pipe is freed.
    private int _extraReserved;

    public async ValueTask<bool> ReserveExtraAsync(int bytes, CancellationToken cancellationToken)
    {
        if (_session == null || !await _session.Budget.ReserveAsync(bytes, cancellationToken).ConfigureAwait(false))
            return false;

        Interlocked.Add(ref _extraReserved, bytes);
        return true;
    }

    public void ReleaseExtra(int bytes)
    {
        Interlocked.Add(ref _extraReserved, -bytes);
        _session?.Budget.Release(bytes);
    }

    /// <summary>
    /// Releases budget still reserved for oversized items. Called when the pipe is freed.
    /// </summary>
    internal void ReleaseAllExtra()
    {
        var extra = Interlocked.Exchange(ref _extraReserved, 0);
        if (extra > 0)
            _session?.Budget.Release(extra);
    }

    public void Reset(PipeResetReason reason)
    {
        Logger?.LogWarning("Resetting the pipe: the peer sent an item that breaks the channel framing or exceeds MaxMessageSize.");
        _session?.MetricsTags.Metrics.RecordPipeReset(_session.MetricsTags, reason);
        _ = ResetAsync();

        async Task ResetAsync()
        {
            try
            {
                _inputPipeReader.CompleteNoNotify();
                await CloseAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger?.LogDebug(e, "Pipe reset failed.");
            }
        }
    }

    public void GrantCredit(int bytes)
    {
        Interlocked.Add(ref _pendingGrant, bytes);
        RequestControl();
    }

    private void RequestControl()
    {
        if (_session != null && Interlocked.Exchange(ref _controlQueued, 1) == 0)
            _session.ControlSender.RequestPipeControl(this);
    }

    /// <summary>
    /// Sends the pending window grant and close acknowledgement. Called by the session's control sender.
    /// </summary>
    internal async ValueTask SendPendingControl()
    {
        // Cleared first so a request made while sending queues the pipe again.
        Volatile.Write(ref _controlQueued, 0);
        var session = _session;
        if (session == null)
            return;

        var grant = Interlocked.Exchange(ref _pendingGrant, 0);
        if (grant > 0 && Volatile.Read(ref _sentClose) == 0)
        {
            var id = Id;
            _pipeIdHeader[0] = (byte)id;
            _pipeIdHeader[1] = (byte)(id >> 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_windowUpdateBody, (uint)grant);
            await session.SendHeaderWithBody(
                MessageType.PipeWindowUpdate,
                _pipeIdHeader,
                new ReadOnlySequence<byte>(_windowUpdateBody)).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _pendingCloseAck, 0) == 1)
            await CloseAsync().ConfigureAwait(false);
    }

    public ValueTask CompleteAsync()
    {
        if (_currentState == State.Complete 
            || _currentState == State.Unset)
            return default;

        var stateChanged = UpdateState(State.Complete);
        if (stateChanged)
            return NotifyState();

        return default;
    }

    /// <summary>
    /// Writes data from upstream reader into the input pipe
    /// </summary>
    /// <param name="data">The data to write from upstream reader</param>
    /// <returns>Result of the buffering.</returns>
    public NexusPipeBufferResult WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return NexusPipeBufferResult.DataIgnored;

        // The peer said it was done; it may not send more.
        if (Volatile.Read(ref _receivedClose) == 1)
            return NexusPipeBufferResult.CreditExceeded;

        // A completed reader (this side closing, or the direction complete) discards the data but still counts it
        // against the credit granted.
        return _inputPipeReader.BufferData(data);
    }


    /// <summary>
    /// Notifies the current state of the duplex pipe to the associated session.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask NotifyState()
    {
        var session = _session;
        if(session == null)
            return;

        //_logger?.LogInfo($"Notifying state: {_currentState}");
        var currentState = _currentState;

        // This side's Complete is sent once; after it the pipe is Closing until the peer's arrives.
        if (currentState == State.Complete)
        {
            if (Interlocked.Exchange(ref _sentClose, 1) == 1)
                return;
            Volatile.Write(ref _closingSince, session.Config.Time.GetTickCount64());
        }

        using var message = session.PoolManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.State = currentState;
        message.Window = Interlocked.Exchange(ref _readyWindow, 0);
        try
        {
            await session.SendMessage(message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Possible that the session was closed before we could send the message.
            Logger?.LogInfo(e, $"Exception occurred while updating state to: {currentState}. Closing pipe.");

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
            || (!remove && (currentState & updatedState) == updatedState)
            || (remove && (currentState & updatedState) != updatedState))
        {
            Logger?.LogTrace($"Current State: {currentState}; Canceled state update of: {updatedState} because it is already set.");
            return false;
        }

        var isServer = _session.IsServer;

        Logger?.LogTrace($"Current State: {currentState}; Update State: {updatedState};");

        if (currentState == State.Unset)
        {
            if (updatedState == State.Ready)
            {
                _currentState = State.Ready;

                // Set the ready task.
                _readyTcs.TrySetResult();

                return true;
            }
            else
            {
                // If we are not ready, then we can't update the state.
                // This normally happens when the pipe is reset before it is ready or after it has been reset.
                // Honestly, we shouldn't reach here.
                Logger?.LogTrace($"Ignored update state of : {updatedState} because the pipe was never readied.");
                _readyTcs.TrySetCanceled();
                _completeTcs.TrySetCanceled();
                return false;
            }
        }

        if (HasState(updatedState, currentState, State.ClientReaderServerWriterComplete))
        {
            if (isServer)
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
            else
            {
                // Close input pipe.
                _inputPipeReader.CompleteNoNotify();
            }
        }

        if (HasState(updatedState, currentState, State.ClientWriterServerReaderComplete))
        {
            if (isServer)
            {
                // Close input pipe.
                _inputPipeReader.CompleteNoNotify();
            }
            else
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
        }

        // Use lock to ensure atomic read-modify-write operations on state
        lock (_stateLock)
        {
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
        }

        // Check if the pipe is complete.
        if ((_currentState & State.Complete) == State.Complete)
        {
            // Set the complete task.
            _completeTcs.TrySetResult();
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasState(State has, State notHave, State flag)
    {
        return (has & flag) == flag && (notHave & flag) != flag;
    }

    public void Dispose()
    {
        // Set the task to canceled in case the pipe was reset before it was ready.
        _readyTcs.TrySetCanceled();
        _completeTcs.TrySetCanceled();

        _inputPipeReader.Dispose();
        _outputPipeWriter.Dispose();

        // Close everything.
        if (_currentState != State.Complete)
        {
            Logger?.LogError($"Resetting and Sending pipe: {Id} {_currentState}");
            UpdateState(State.Complete);
        }

        return;
    }
}
