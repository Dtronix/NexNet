using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using static NexNet.Pipes.NexusDuplexPipe;

namespace NexNet.Pipes;

internal class NexusPipeManager
{
    private INexusLogger? _logger;
    private INexusSession _session = null!;
    private readonly ConcurrentDictionary<ushort, NexusDuplexPipe> _activePipes = new();
    private readonly BitArray _usedIds = new(256, false);
    private int _currentId = 0;
    private bool _isCanceled;

    public void Setup(INexusSession session)
    {
        _currentId = 0;
        _usedIds.SetAll(false);
        _isCanceled = false;
        _session = session;
        _logger = session.Logger?.CreateLogger<NexusPipeManager>();
    }

    public IRentedNexusDuplexPipe? RentPipe()
    {
        if (_isCanceled)
            return null;

        var localId = GetNewLocalId();
        var partialId = GetPartialIdFromLocalId(localId);
        var pipe = new RentedNexusDuplexPipe(localId, _session)
        {
            InitiatingPipe = true,
            Manager = this
        };

        _activePipes.TryAdd(partialId, pipe);

        return pipe;
    }

    /// <summary>
    /// Asynchronously returns the specified rented duplex pipe back to the pipe manager.
    /// </summary>
    /// <param name="pipe">The rented duplex pipe to be returned.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask ReturnPipe(IRentedNexusDuplexPipe pipe)
    {
        _logger?.LogTrace($"ReturnPipe({pipe.Id});");
        if (!_activePipes.TryRemove(pipe.Id, out var nexusPipe))
            return;
        
        if(nexusPipe.CurrentState != State.Complete)
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
        }

        nexusPipe.Dispose();

        lock (_usedIds)
        {
            // Return the local ID to the available IDs list.
            _usedIds.Set(nexusPipe.LocalId, false);
        }
    }


    public async ValueTask<INexusDuplexPipe> RegisterPipe(byte otherId)
    {
        if (_session == null)
            throw new InvalidOperationException("Session if not available for usage.");

        _logger?.LogTrace($"RegisterPipe({otherId});");
        if (_isCanceled)
            throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");

        var id = GetCompleteId(otherId, out var localId);

        var pipe = new NexusDuplexPipe(localId, _session)
        {
            InitiatingPipe = false,
            Id = id
        };

        if (!_activePipes.TryAdd(id, pipe))
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");

        // Signal that the pipe is ready to receive and send messages.
        pipe.UpdateState(State.Ready);
        await pipe.NotifyState().ConfigureAwait(false);
        _logger?.LogError($"Sending Ready Notification");
        return pipe;
    }

    public async ValueTask DeregisterPipe(INexusDuplexPipe pipe)
    {
        _logger?.LogTrace($"DeregisterPipe({pipe.Id});");

        if (!_activePipes.TryRemove(pipe.Id, out var nexusPipe))
        {
            _logger?.LogError($"Cant Remove ({pipe.Id});");
            return;
        }

        var localId = ExtractLocalId(pipe.Id, _session.IsServer);

        if (nexusPipe.CurrentState != State.Complete)
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
        }

        //_session.CacheManager.NexusDuplexPipeCache.Return(nexusPipe.Pipe);

        lock (_usedIds)
        {
            // Return the local ID to the available IDs list.
            _usedIds.Set(localId, false);
        }

    }

    /// <summary>
    /// Buffers incoming data from the other side of the pipe.
    /// </summary>
    /// <param name="id">Full ID of the pipe.</param>
    /// <param name="data">Data to buffer.</param>
    /// <returns>Result of the buffering.</returns>
    public ValueTask<NexusPipeBufferResult> BufferIncomingData(ushort id, ReadOnlySequence<byte> data)
    {
        if (_isCanceled)
            return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);

        if (_activePipes.TryGetValue(id, out var pipe))
        {
            // Check to see if we have exceeded the high water cutoff for the pipe.
            // If we have, then disconnect the connection.
            return pipe.WriteFromUpstream(data);
        }

        _logger?.LogError($"Received data on NexusDuplexPipe id: {id} but no stream is open on this id.");
        //throw new InvalidOperationException($"No pipe exists for id: {id}.");
        return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);
    }

    public DisconnectReason UpdateState(ushort id, NexusDuplexPipe.State state)
    {
        _logger?.LogError($"Pipe {id} update state {state}");
        if (_isCanceled)
            return DisconnectReason.None;

        if (_activePipes.TryGetValue(id, out var pipe))
        {
            pipe.UpdateState(state);
        }
        else
        {
            if (state == State.Ready)
            {
                var localIdByte = ExtractLocalId(id, _session.IsServer);
                var partialId = GetPartialIdFromLocalId(localIdByte);
                if (!_activePipes.TryRemove(partialId, out pipe))
                {
                    _logger?.LogTrace($"Could not find pipe with Full ID of {id} and initial ID of {localIdByte}");
                    return DisconnectReason.ProtocolError;
                }

                // Move the pipe to the main active pipes.
                _activePipes.TryAdd(id, pipe);
                pipe.Id = id;
                pipe.UpdateState(state);
                _logger?.LogError($"Readies pipe {id}");
                return DisconnectReason.None;
            }
            else if (state == State.Complete)
            {
                _logger?.LogTrace($"Pipe is already complete and ignored state update of {state} with Full ID of {id}");
                return DisconnectReason.None;
            }

            _logger?.LogTrace($"Ignored state update of {state} with Full ID of {id}");
        }

        return DisconnectReason.None;
    }

    public void CancelAll()
    {
        _logger?.LogTrace($"CancelAll();");
        _isCanceled = true;

        foreach (var pipe in _activePipes)
        {
            pipe.Value.UpdateState(NexusDuplexPipe.State.Complete);
        }

        _activePipes.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetCompleteId(byte otherId, out byte thisId)
    {
        Span<byte> idBytes = stackalloc byte[sizeof(ushort)];
        if (_session.IsServer)
        {
            idBytes[0] = otherId; // Client
            thisId = idBytes[1] = GetNewLocalId(); // Server
        }
        else
        {
            thisId = idBytes[0] = GetNewLocalId(); // Client
            idBytes[1] = otherId; // Server
        }

        return BitConverter.ToUInt16(idBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetPartialIdFromLocalId(byte localId)
    {
        Span<byte> idBytes = stackalloc byte[sizeof(ushort)];
        if (_session.IsServer)
        {
            idBytes[0] = 0; // Client
            idBytes[1] = localId; // Server
        }
        else
        {
            idBytes[0] = localId; // Client
            idBytes[1] = 0; // Server
        }

        return BitConverter.ToUInt16(idBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (byte ClientId, byte ServerId) ExtractClientAndServerId(ushort id)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Unsafe.As<byte, ushort>(ref bytes[0]) = id;
        return (bytes[0], bytes[1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ExtractLocalId(ushort id, bool isServer)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Unsafe.As<byte, ushort>(ref bytes[0]) = id;
        return isServer ? bytes[1] : bytes[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdLocalIdOnly(ushort id, bool isServer, out byte localId)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Unsafe.As<byte, ushort>(ref bytes[0]) = id;
        localId = isServer ? bytes[1] : bytes[0];

        // If the other ID is 0, then this is a local ID only.
        return (!isServer ? bytes[1] : bytes[0]) == 0;
    }


    private byte GetNewLocalId()
    {
        lock (_usedIds)
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
