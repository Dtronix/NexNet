using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
using static NexNet.Pipes.NexusDuplexPipe;

namespace NexNet.Pipes;

internal class NexusPipeManager
{
    private INexusLogger? _logger;
    private INexusSession _session = null!;
    private readonly ConcurrentDictionary<ushort, NexusDuplexPipe> _activePipes = new();
    private readonly BitArray _usedIds = new(256, false);
    private readonly Lock _usedIdsLock = new Lock();
    private int _currentId = 0;
    private bool _isCanceled;

    // Pipes that hold a receive window: open, or closing until both sides confirmed. Bounded by ConfigBase.MaxPipes.
    private int _openPipes;

    // Waiters for pipes to be freed (see WaitFreedAsync). Guards the removal of a pipe from _activePipes in TryFree,
    // so a waiter either sees the pipe gone or is completed by that TryFree.
    private readonly Lock _freedLock = new Lock();
    private Dictionary<NexusDuplexPipe, TaskCompletionSource>? _freedWaiters;

    /// <summary>
    /// Pipes holding a receive window.
    /// </summary>
    public int OpenPipes => Volatile.Read(ref _openPipes);

    public void Setup(INexusSession session)
    {
        lock (_usedIdsLock)
        {
            _currentId = 0;
            _usedIds.SetAll(false);
        }

        // Managers are pooled. A registration that raced with CancelAll on the previous session can leave a stale
        // entry behind, which would collide with this session's first pipe of the same ID.
        _activePipes.Clear();
        _openPipes = 0;
        _isCanceled = false;
        CompleteFreedWaiters();
        _session = session;
        _logger = session.Logger?.CreateLogger("PipeManager");
    }

    public IRentedNexusDuplexPipe? RentPipe()
    {
        if (_isCanceled)
            return null;

        if (!TryReserveWindow())
        {
            _session.MetricsTags.Metrics.RecordPipeRefusal(_session.MetricsTags, local: true);
            throw new NexusBusyException("Can't open a pipe: the session's pipe limit or budget is reached.");
        }

        byte localId;
        try
        {
            localId = GetNewLocalId();
        }
        catch
        {
            ReleaseWindow();
            throw;
        }

        var partialId = GetPartialIdFromLocalId(localId);
        var pipe = new RentedNexusDuplexPipe(localId, _session)
        {
            Manager = this
        };

        _activePipes.TryAdd(partialId, pipe);

        // CancelAll may have run between the check above and the add; don't leave the pipe behind.
        if (_isCanceled)
        {
            _activePipes.TryRemove(partialId, out _);
            return null;
        }

        return pipe;
    }

    /// <summary>
    /// Takes a pipe slot (<see cref="Transports.ConfigBase.MaxPipes"/>) and reserves one receive window from the
    /// session budget, all or nothing.
    /// </summary>
    private bool TryReserveWindow()
    {
        if (Interlocked.Increment(ref _openPipes) > _session.Config.MaxPipes)
        {
            Interlocked.Decrement(ref _openPipes);
            return false;
        }

        if (_session.Budget.TryReserve(_session.Config.PipeWindow))
            return true;

        Interlocked.Decrement(ref _openPipes);
        return false;
    }

    private void ReleaseWindow()
    {
        Interlocked.Decrement(ref _openPipes);
        _session.Budget.Release(_session.Config.PipeWindow);
    }

    /// <summary>
    /// Frees a pipe once both sides closed it and the application released it: removes it, releases its window
    /// and returns its ID.
    /// </summary>
    private void TryFree(NexusDuplexPipe pipe)
    {
        if (!pipe.IsFullyClosed || !pipe.TryMarkFreed())
            return;

        ReleaseWindow();
        pipe.ReleaseAllExtra();

        TaskCompletionSource? waiter = null;
        lock (_freedLock)
        {
            _activePipes.TryRemove(new KeyValuePair<ushort, NexusDuplexPipe>(KeyOf(pipe), pipe));

            lock (_usedIdsLock)
            {
                _usedIds.Set(pipe.LocalId, false);
            }

            _freedWaiters?.Remove(pipe, out waiter);
        }

        waiter?.TrySetResult();
    }

    /// <summary>
    /// Returns a task that completes once the pipe is freed: its slot, receive window and ID are released after both
    /// sides closed it and the application released it. Also completes when the session's pipes are canceled.
    /// Completed at once for a pipe that is already freed or that this manager does not hold.
    /// </summary>
    /// <param name="pipe">Pipe rented from or registered with this manager.</param>
    public Task WaitFreedAsync(INexusDuplexPipe pipe)
    {
        if (pipe is not NexusDuplexPipe nexusPipe)
            return Task.CompletedTask;

        lock (_freedLock)
        {
            if (_isCanceled || !IsActive(nexusPipe))
                return Task.CompletedTask;

            _freedWaiters ??= new Dictionary<NexusDuplexPipe, TaskCompletionSource>();
            if (!_freedWaiters.TryGetValue(nexusPipe, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _freedWaiters.Add(nexusPipe, waiter);
            }

            return waiter.Task;
        }
    }

    /// <summary>
    /// True if the pipe is still held under its full or its partial ID. A pipe only leaves the active pipes when it
    /// is freed or the session's pipes are canceled, apart from its move from the partial to the full ID.
    /// </summary>
    private bool IsActive(NexusDuplexPipe pipe)
    {
        return (pipe.Id != 0 && _activePipes.TryGetValue(pipe.Id, out var full) && ReferenceEquals(full, pipe))
               || (_activePipes.TryGetValue(GetPartialIdFromLocalId(pipe.LocalId), out var partial)
                   && ReferenceEquals(partial, pipe));
    }

    /// <summary>
    /// Completes every pending <see cref="WaitFreedAsync"/> task.
    /// </summary>
    private void CompleteFreedWaiters()
    {
        List<TaskCompletionSource>? waiters = null;
        lock (_freedLock)
        {
            if (_freedWaiters is { Count: > 0 })
            {
                waiters = new List<TaskCompletionSource>(_freedWaiters.Values);
                _freedWaiters.Clear();
            }
        }

        if (waiters == null)
            return;

        foreach (var waiter in waiters)
            waiter.TrySetResult();
    }

    /// <summary>
    /// The pipe's key in the active pipes: its full ID, or until the peer's Ready arrives on the initiating side,
    /// its partial ID.
    /// </summary>
    private ushort KeyOf(NexusDuplexPipe pipe) => pipe.Id != 0 ? pipe.Id : GetPartialIdFromLocalId(pipe.LocalId);

    /// <summary>
    /// Describes a pipe that made no progress for longer than <paramref name="timeoutTicks"/>: it has been closing
    /// (this side's Complete sent, the peer's not received), or the data of an item given extra credit stopped
    /// arriving. Null if no pipe stalled.
    /// </summary>
    public string? FindStalledPipe(long nowTicks, long timeoutTicks)
    {
        foreach (var pipe in _activePipes)
        {
            var stall = pipe.Value.DescribeStall(nowTicks, timeoutTicks);
            if (stall != null)
                return stall;
        }

        return null;
    }

    /// <summary>
    /// Adds a snapshot of every pipe holding a receive window to <paramref name="snapshots"/>.
    /// </summary>
    public void AddSnapshots(List<Diagnostics.NexusPipeSnapshot> snapshots, long nowTicks)
    {
        foreach (var pipe in _activePipes)
        {
            snapshots.Add(pipe.Value.GetSnapshot(nowTicks));
        }
    }

    /// <summary>
    /// Asynchronously returns the specified rented duplex pipe back to the pipe manager.
    /// </summary>
    /// <param name="pipe">The rented duplex pipe to be returned.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask ReturnPipe(IRentedNexusDuplexPipe pipe)
    {
        _logger?.LogTrace($"ReturnPipe(P{pipe.Id});");
        var nexusPipe = (NexusDuplexPipe)pipe;
        if (!_activePipes.TryGetValue(KeyOf(nexusPipe), out var active) || !ReferenceEquals(active, nexusPipe))
            return;

        await ReleasePipe(nexusPipe).ConfigureAwait(false);
    }

    /// <summary>
    /// The application is done with a pipe: send this side's Complete if needed and free the pipe once the peer has
    /// closed it too. A pipe the peer never opened is freed at once.
    /// </summary>
    private async ValueTask ReleasePipe(NexusDuplexPipe pipe)
    {
        var neverOpened = pipe.CurrentState == State.Unset;
        if (!neverOpened)
            await pipe.CloseAsync().ConfigureAwait(false);

        pipe.Dispose();
        pipe.MarkReleased();

        if (neverOpened)
            pipe.MarkClosedWithoutExchange();

        TryFree(pipe);
    }

    public async ValueTask<INexusDuplexPipe> RegisterPipe(byte otherId)
    {
        if (_session == null)
            throw new InvalidOperationException("Session if not available for usage.");

        _logger?.LogTrace($"RegisterPipe({otherId});");
        if (_isCanceled)
            throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");

        if (!TryReserveWindow() && !await TryReserveAfterPendingFreesAsync().ConfigureAwait(false))
        {
            if (_isCanceled)
                throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");

            await RefusePipe(otherId).ConfigureAwait(false);
            throw new NexusBusyException("Refused a pipe: the session's pipe limit or budget is reached.");
        }

        ushort id;
        byte localId;
        try
        {
            id = GetCompleteId(otherId, out localId);
        }
        catch
        {
            ReleaseWindow();
            throw;
        }

        var pipe = new NexusDuplexPipe(id, localId, _session);

        if (!_activePipes.TryAdd(id, pipe))
        {
            ReleaseWindow();
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");
        }

        // CancelAll may have run between the check above and the add; don't leave the pipe behind.
        if (_isCanceled)
        {
            _activePipes.TryRemove(id, out _);
            pipe.Dispose();
            throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");
        }

        // Signal that the pipe is ready to receive and send messages, granting the receive window.
        await pipe.SendReady().ConfigureAwait(false);
        _logger?.LogTrace($"Sending Ready Notification");
        return pipe;
    }

    /// <summary>
    /// Waits for completed pipes to be freed, then reserves a slot and window (see <see cref="TryReserveWindow"/>).
    /// A completed pipe keeps its slot until both sides confirmed the close and the application released it, and
    /// the peer frees its own side as soon as this side's close confirmation arrives. The peer can therefore open its
    /// next pipe before this side's handler (a collection connection, for example) has returned and released the old
    /// one. Waiting for such a pipe, bounded by <see cref="MaxPendingFreeWait"/> and
    /// <see cref="Transports.ConfigBase.ProgressTimeout"/>, avoids refusing an open that is only racing the release.
    /// Returns false at once when no completed pipe is waiting to be freed.
    /// </summary>
    private async ValueTask<bool> TryReserveAfterPendingFreesAsync()
    {
        var config = _session.Config;
        var time = config.Time;
        var waitMs = Math.Min(config.ProgressTimeout, (long)MaxPendingFreeWait.TotalMilliseconds);
        var deadline = time.GetTickCount64() + waitMs;
        List<Task>? pending = null;

        while (!_isCanceled)
        {
            pending?.Clear();
            foreach (var entry in _activePipes)
            {
                // Completed by either side and not freed yet: it is freed once the close exchange and the
                // application's release are done.
                if (entry.Value.CurrentState != State.Complete)
                    continue;

                var freed = WaitFreedAsync(entry.Value);
                if (freed.IsCompleted)
                    continue;

                pending ??= new List<Task>();
                pending.Add(freed);
            }

            if (pending is not { Count: > 0 })
                return TryReserveWindow();

            var remaining = deadline - time.GetTickCount64();
            if (remaining <= 0)
                return false;

            _logger?.LogTrace($"Pipe limit reached; waiting up to {remaining} ms for {pending.Count} completed pipe(s) to be freed.");
            try
            {
                await Task.WhenAny(pending).WaitAsync(TimeSpan.FromMilliseconds(remaining), time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return TryReserveWindow();
            }

            if (TryReserveWindow())
                return true;
        }

        return false;
    }

    /// <summary>
    /// Longest wait in <see cref="RegisterPipe"/> for completed pipes to be freed before refusing a new one.
    /// </summary>
    internal static readonly TimeSpan MaxPendingFreeWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Refuses a pipe the peer opened without registering it: sends Refused for the opener's partial ID, so the
    /// initiating side fails its pipe and frees its ID.
    /// </summary>
    /// <param name="otherId">The opener's local pipe ID.</param>
    public async ValueTask RefusePipe(byte otherId)
    {
        _session.MetricsTags.Metrics.RecordPipeRefusal(_session.MetricsTags, local: true);
        using var refusal = _session.PoolManager.Rent<DuplexPipeUpdateStateMessage>();
        refusal.PipeId = _session.IsServer ? ComposeId(otherId, 0) : ComposeId(0, otherId);
        refusal.State = State.Refused;
        await _session.SendMessage(refusal).ConfigureAwait(false);
    }

    public async ValueTask DeregisterPipe(INexusDuplexPipe pipe)
    {
        _logger?.LogTrace($"DeregisterPipe(P{pipe.Id});");

        if (!_activePipes.TryGetValue(pipe.Id, out var nexusPipe))
        {
            _logger?.LogError($"Can't Remove (P{pipe.Id});");
            return;
        }

        await ReleasePipe(nexusPipe).ConfigureAwait(false);

    }

    /// <summary>
    /// Buffers incoming data from the other side of the pipe. Never waits.
    /// </summary>
    /// <param name="id">Full ID of the pipe.</param>
    /// <param name="data">Data to buffer.</param>
    /// <returns>A disconnect reason if the data breaks the protocol, otherwise <see cref="DisconnectReason.None"/>.</returns>
    public DisconnectReason BufferIncomingData(ushort id, ReadOnlySequence<byte> data)
    {
        if (_isCanceled)
            return DisconnectReason.None;

        // Pipes stay here while closing, so data the peer sent before learning of a local close is accepted (and
        // discarded) up to its credit.
        if (!_activePipes.TryGetValue(id, out var pipe))
        {
            _logger?.LogDebug($"Received data on NexusDuplexPipe id: P{id} but no pipe is open or closing on this id.");
            return DisconnectReason.ProtocolError;
        }

        if (pipe.WriteFromUpstream(data) == NexusPipeBufferResult.CreditExceeded)
        {
            _logger?.LogDebug($"Pipe P{id} received data beyond its credit or after the peer's Complete.");
            return DisconnectReason.FlowControl;
        }

        return DisconnectReason.None;
    }

    /// <summary>
    /// Adds send credit granted by the peer.
    /// </summary>
    /// <returns>A disconnect reason if the grant breaks the protocol, otherwise <see cref="DisconnectReason.None"/>.</returns>
    public DisconnectReason AddCredit(ushort id, uint bytes)
    {
        // A grant can race with this side freeing the pipe; it no longer matters.
        if (_isCanceled || !_activePipes.TryGetValue(id, out var pipe))
            return DisconnectReason.None;

        return pipe.AddCredit(bytes) ? DisconnectReason.None : DisconnectReason.FlowControl;
    }

    public DisconnectReason UpdateState(ushort id, State state, uint window = 0)
    {
        _logger?.LogTrace($"Pipe P{id} update state {state}");
        if (_isCanceled)
            return DisconnectReason.None;

        // A refusal carries the opener's partial ID, so it is found here too.
        if (_activePipes.TryGetValue(id, out var pipe))
        {
            var result = pipe.OnRemoteState(state, window);
            TryFree(pipe);
            return result;
        }
        else
        {
            switch (state)
            {
                case State.Ready:
                {
                    var localIdByte = ExtractLocalId(id, _session.IsServer);
                    var partialId = GetPartialIdFromLocalId(localIdByte);
                    if (!_activePipes.TryRemove(partialId, out pipe))
                    {
                        _logger?.LogTrace($"Could not find pipe with Full ID of P{id} and initial ID of {localIdByte}");
                        return DisconnectReason.ProtocolError;
                    }

                    // Move the pipe to the main active pipes.
                    _activePipes.TryAdd(id, pipe);

                    // Set the full ID of the pipe.
                    pipe.Id = id;

                    return pipe.OnRemoteState(state, window);
                }
                case State.Complete:
                    _logger?.LogTrace($"Pipe is already complete and ignored state update of {state} with Full ID of P{id}");
                    return DisconnectReason.None;
                default:
                    _logger?.LogTrace($"Ignored state update of {state} with Full ID of P{id}");
                    break;
            }
        }

        return DisconnectReason.None;
    }

    public void CancelAll()
    {
        //_logger?.LogTrace($"CancelAll();");
        _isCanceled = true;

        foreach (var pipe in _activePipes)
        {
            pipe.Value.UpdateState(NexusDuplexPipe.State.Complete);
        }

        _activePipes.Clear();

        // The session ended: nothing is freed one by one any more, so release everyone waiting for a pipe.
        CompleteFreedWaiters();
    }

    // Pipe IDs are a byte pair: the client's local ID and the server's local ID.
    // The composed ushort is defined as (clientId | serverId << 8) regardless of host endianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ComposeId(byte clientId, byte serverId) => (ushort)(clientId | (serverId << 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetCompleteId(byte otherId, out byte thisId)
    {
        thisId = GetNewLocalId();
        return _session.IsServer
            ? ComposeId(otherId, thisId)
            : ComposeId(thisId, otherId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetPartialIdFromLocalId(byte localId)
    {
        return _session.IsServer
            ? ComposeId(0, localId)
            : ComposeId(localId, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (byte ClientId, byte ServerId) ExtractClientAndServerId(ushort id)
    {
        return ((byte)id, (byte)(id >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ExtractLocalId(ushort id, bool isServer)
    {
        var (clientId, serverId) = ExtractClientAndServerId(id);
        return isServer ? serverId : clientId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdLocalIdOnly(ushort id, bool isServer, out byte localId)
    {
        var (clientId, serverId) = ExtractClientAndServerId(id);
        localId = isServer ? serverId : clientId;

        // If the other ID is 0, then this is a local ID only.
        return (!isServer ? serverId : clientId) == 0;
    }


    private byte GetNewLocalId()
    {
        lock (_usedIdsLock)
        {
            var incrementedId = _currentId;

            for (int i = 0; i < 255; i++)
            {
                // Loop around if we reach the end of the available IDs.
                if (++incrementedId == 256)
                    incrementedId = 1;

                // If the Id is not available, then try the next one.
                if (_usedIds.Get(incrementedId))
                    continue;

                _currentId = incrementedId;
                _usedIds.Set(incrementedId, true);

                return (byte)incrementedId;
            }
        }

        // If we reach the end of the available IDs, then we are full and can throw an exception.
        throw new InvalidOperationException("Exceeded maximum number of concurrent streams.");
    }
}
