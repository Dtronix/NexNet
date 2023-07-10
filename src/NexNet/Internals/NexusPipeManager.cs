using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Internals;

internal class NexusPipeManager
{
    private readonly INexusSession _session;
    private ConcurrentDictionary<ushort, NexusDuplexPipe> _activePipes = new();
    private ConcurrentBag<NexusDuplexPipe> _pipeCache = new();

    private readonly Stack<byte> _availableIds = new Stack<byte>();

    private int _currentId;

    public NexusPipeManager(INexusSession session)
    {
        _session = session;
    }

    public async ValueTask<NexusDuplexPipe> RegisterPipe(byte otherId)
    {
        if (!_pipeCache.TryTake(out var pipe))
            pipe = new NexusDuplexPipe();

        var id = GetCompleteId(otherId);

        if (!_activePipes.TryAdd(id, pipe))
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");

        await pipe.PipeReady(_session, id)).ConfigureAwait(false);

        return pipe;
    }

    public async ValueTask<NexusDuplexPipe> DeregisterPipe(NexusDuplexPipe pipe)
    {
        pipe.UpstreamUpdateState(_session.IsServer
            ? NexusDuplexPipeState.ServerReaderComplete | NexusDuplexPipeState.ServerWriterComplete
            : NexusDuplexPipeState.ClientReaderComplete | NexusDuplexPipeState.ClientWriterComplete);

        await pipe.NotifyState();

        if (!_activePipes.TryRemove(pipe.Id, out _))
            throw new Exception("Could not remove NexusDuplexPipe to the list of current pipes.");
        

        _session.CacheManager.Return(message);

        return pipe;
    }

    private ushort GetCompleteId(byte otherId)
    {
        Span<byte> idBytes = stackalloc byte[sizeof(ushort)];
        if (_session.IsServer)
        {
            idBytes[0] = otherId; // Client
            idBytes[1] = GetPartialId(); // Server
        }
        else
        {
            idBytes[0] = GetPartialId(); // Client
            idBytes[1] = otherId; // Server
        }

        return BitConverter.ToUInt16(idBytes);
    }

    private byte GetPartialId()
    {
        if (_availableIds.TryPop(out var id))
            return id;

        if (_currentId == 255)
            throw new InvalidOperationException("Exceeded maximum number of concurrent streams.");

        return (byte)_currentId++;
    }

    private void ReturnPartialId(byte id)
    {
        _availableIds.Push(id);
    }



    public ValueTask BufferIncomingData(ushort id, ReadOnlySequence<byte> data)
    {
        if (_activePipes.TryGetValue(id, out var pipe))
            return pipe.UpstreamWrite(data);

        _session.Logger?.LogError($"Received data on NexusDuplexPipe id: {id} but no stream is open on this id.");
        throw new InvalidOperationException($"No pipe exists for id: {id}.");
    }

    public ValueTask UpdateState(DuplexPipeUpdateStateMessage message)
    {
        
    }

}
