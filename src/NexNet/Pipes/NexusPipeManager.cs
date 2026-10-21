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

        _activePipes.TryRemove(new KeyValuePair<ushort, NexusDuplexPipe>(KeyOf(pipe), pipe));
        ReleaseWindow();
        pipe.ReleaseAllExtra();

        lock (_usedIdsLock)
        {
            _usedIds.Set(pipe.LocalId, false);
        }
    }

    /// <summary>
    /// The pipe's key in the active pipes: its full ID, or until the peer's Ready arrives on the initiating side,
    /// its partial ID.
    /// </summary>
    private ushort KeyOf(NexusDuplexPipe pipe) => pipe.Id != 0 ? pipe.Id : GetPartialIdFromLocalId(pipe.LocalId);

    /// <summary>
    /// True if a pipe made no progress for longer than <paramref name="timeoutTicks"/>: it has been closing (this
    /// side's Complete sent, the peer's not received), or the data of an item given extra credit stopped arriving.
    /// </summary>
    public bool HasStalledPipe(long nowTicks, long timeoutTicks)
    {
        foreach (var pipe in _activePipes)
        {
            if (pipe.Value.IsCloseStalled(nowTicks, timeoutTicks) || pipe.Value.IsItemStalled(nowTicks, timeoutTicks))
                return true;
        }

        return false;
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

        if (!TryReserveWindow())
        {
            // Refuse the open; the initiating side fails its pipe and frees its ID.
            _session.MetricsTags.Metrics.RecordPipeRefusal(_session.MetricsTags, local: true);
            using var refusal = _session.PoolManager.Rent<DuplexPipeUpdateStateMessage>();
            refusal.PipeId = _session.IsServer ? ComposeId(otherId, 0) : ComposeId(0, otherId);
            refusal.State = State.Refused;
            await _session.SendMessage(refusal).ConfigureAwait(false);
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
