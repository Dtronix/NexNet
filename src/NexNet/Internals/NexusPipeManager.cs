using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using NexNet.Messages;
using static System.Collections.Specialized.BitVector32;

namespace NexNet.Internals;

internal class NexusPipeManager
{
    private readonly INexusSession _session;
    private ConcurrentDictionary<ushort, NexusDuplexPipe> _activePipes = new();
    private ConcurrentDictionary<byte, NexusDuplexPipe> _initializingPipes = new();

    private readonly Stack<byte> _availableIds = new Stack<byte>();

    private int _currentId;

    public NexusPipeManager(INexusSession session)
    {
        _session = session;
    }

    public NexusDuplexPipe GetPipe()
    {
        var id = GetPartialId();
        var pipe = _session.CacheManager.NexusDuplexPipeCache.Rent(_session, id);
        _initializingPipes.TryAdd(id, pipe);

        return pipe;
    }

    public async ValueTask<NexusDuplexPipe> RegisterPipe(byte otherId)
    {
        var id = GetCompleteId(otherId, out var thisId);

        var pipe = _session.CacheManager.NexusDuplexPipeCache.Rent(_session, thisId);


        if (!_activePipes.TryAdd(id, pipe))
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");

        await pipe.PipeReady(_session, id).ConfigureAwait(false);

        return pipe;
    }

    public async ValueTask DeregisterPipe(NexusDuplexPipe pipe)
    {
        pipe.UpdateState(_session.IsServer
            ? NexusDuplexPipe.State.ServerReaderComplete | NexusDuplexPipe.State.ServerWriterComplete
            : NexusDuplexPipe.State.ClientReaderComplete | NexusDuplexPipe.State.ClientWriterComplete);

        await pipe.NotifyState();

        if (!_activePipes.TryRemove(pipe.Id, out _))
            throw new Exception("Could not remove NexusDuplexPipe to the list of current pipes.");

        var (clientId, serverId) = GetClientAndServerId(pipe.Id);
        _availableIds.Push(_session.IsServer ? serverId : clientId);

        _session.CacheManager.NexusDuplexPipeCache.Return(pipe);
    }

    public ValueTask BufferIncomingData(ushort id, ReadOnlySequence<byte> data)
    {
        if (_activePipes.TryGetValue(id, out var pipe))
            return pipe.WriteFromUpstream(data);

        _session.Logger?.LogError($"Received data on NexusDuplexPipe id: {id} but no stream is open on this id.");
        throw new InvalidOperationException($"No pipe exists for id: {id}.");
    }

    public void UpdateState(ushort id, NexusDuplexPipe.State state)
    {
        NexusDuplexPipe? initialPipe = null;
        if (_session.IsServer && state == NexusDuplexPipe.State.ClientReady)
        {
            var (_, serverId) = GetClientAndServerId(id);
            if (!_initializingPipes.TryRemove(serverId, out initialPipe))
                throw new Exception($"Could not find pipe with initial ID of {serverId}");
        }
        else if(!_session.IsServer && state == NexusDuplexPipe.State.ServerReady)
        {
            var (clientId, _) = GetClientAndServerId(id);
            if (!_initializingPipes.TryRemove(clientId, out initialPipe))
                throw new Exception($"Could not find pipe with initial ID of {clientId}");
        }

        if (initialPipe != null)
        {
            // Move the pipe to the main active pipes.
            _activePipes.TryAdd(id, initialPipe);
            initialPipe.Id = id;
            initialPipe.UpdateState(state);

            return;
        }

        if (_activePipes.TryGetValue(id, out var pipe))
            pipe.UpdateState(state);
    }

    private ushort GetCompleteId(byte otherId, out byte thisId)
    {
        Span<byte> idBytes = stackalloc byte[sizeof(ushort)];
        if (_session.IsServer)
        {
            idBytes[0] = otherId; // Client
            thisId = idBytes[1] = GetPartialId(); // Server
        }
        else
        {
            thisId = idBytes[0] = GetPartialId(); // Client
            idBytes[1] = otherId; // Server
        }

        return BitConverter.ToUInt16(idBytes);
    }

    private static (byte ClientId, byte ServerId) GetClientAndServerId(ushort id)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Unsafe.As<byte, ushort>(ref bytes[0]) = id;
        return (bytes[0], bytes[1]);
    }

    private byte GetPartialId()
    {
        if (_availableIds.TryPop(out var id))
            return id;

        if (_currentId == 255)
            throw new InvalidOperationException("Exceeded maximum number of concurrent streams.");

        return (byte)++_currentId;
    }

    private void ReturnPartialId(byte id)
    {
        _availableIds.Push(id);
    }
}
