using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static NexNet.Internals.Pipes.NexusDuplexPipe;

namespace NexNet.Internals.Pipes;

internal class NexusPipeManager
{
    private class PipeAndState
    {
        public readonly NexusDuplexPipe Pipe;
        private readonly int _state;

        public PipeAndState(NexusDuplexPipe pipe)
        {
            Pipe = pipe;
            _state = pipe.StateId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ValidateState()
        {
            return _state == Pipe.StateId;
        }
    }

    private INexusSession _session = null!;
    private readonly ConcurrentDictionary<ushort, PipeAndState> _activePipes = new();
    private readonly ConcurrentDictionary<byte, PipeAndState> _initializingPipes = new();

    private readonly Stack<byte> _availableIds = new Stack<byte>();

    private int _currentId;

    private bool _isCanceled;

    public void Setup(INexusSession session)
    {
        _isCanceled = false;
        _session = session;
    }

    public INexusDuplexPipe? GetPipe()
    {
        if (_isCanceled)
            return null;

        var id = GetPartialId();
        var pipe = _session.CacheManager.NexusDuplexPipeCache.Rent(_session, id);
        pipe.InitiatingPipe = true;
        _initializingPipes.TryAdd(id, new PipeAndState(pipe));

        return pipe;
    }
    public async ValueTask<INexusDuplexPipe> RegisterPipe(byte otherId)
    {
        if (_session == null)
            throw new InvalidOperationException("Session if not available for usage.");

        _session.Logger?.LogTrace($"NexusPipeManager.RegisterPipe({otherId});");
        if (_isCanceled)
            throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");

        var id = GetCompleteId(otherId, out var thisId);

        var pipe = _session.CacheManager.NexusDuplexPipeCache.Rent(_session, thisId);


        if (!_activePipes.TryAdd(id, new PipeAndState(pipe)))
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");

        await pipe.PipeReady(id).ConfigureAwait(false);

        return pipe;
    }

    public async ValueTask DeregisterPipe(INexusDuplexPipe pipe)
    {
        _session.Logger?.LogTrace($"NexusPipeManager.DeregisterPipe({pipe.Id});");

        if (!_activePipes.TryRemove(pipe.Id, out var nexusPipe))
            return;

        var (clientId, serverId) = GetClientAndServerId(pipe.Id);

        await nexusPipe.Pipe.CompleteAsync();

        _availableIds.Push(_session.IsServer ? serverId : clientId);
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

        if (_activePipes.TryGetValue(id, out var pipeWrapper))
        {
            if (pipeWrapper.ValidateState())
            {
                _session.Logger?.LogTrace($"Ignored data due to pipe changing state form last .");
                return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);
            }

            // Check to see if we have exceeded the high water cutoff for the pipe.
            // If we have, then disconnect the connection.
            return pipeWrapper.Pipe.WriteFromUpstream(data);
        }

        _session.Logger?.LogError($"Received data on NexusDuplexPipe id: {id} but no stream is open on this id.");
        //throw new InvalidOperationException($"No pipe exists for id: {id}.");
        return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);
    }

    public void UpdateState(ushort id, NexusDuplexPipe.State state)
    {
        if (_isCanceled)
            return;

        if (_activePipes.TryGetValue(id, out var pipeWrapper))
        {
            if (!pipeWrapper.ValidateState())
            {
                _session.Logger?.LogTrace($"State update of {state} ignored due to state change.");
                return;
            }

            pipeWrapper.Pipe.UpdateState(state);
        }
        else
        {
            var (clientId, serverId) = GetClientAndServerId(id);
            var thisId = _session.IsServer ? serverId : clientId;
            if (!_initializingPipes.TryRemove(thisId, out var initialPipe))
                throw new Exception($"Could not find pipe with initial ID of {thisId}");

            // Move the pipe to the main active pipes.
            _activePipes.TryAdd(id, initialPipe);
            initialPipe.Pipe.Id = id;
            initialPipe.Pipe.UpdateState(state);
        }
    }

    public void CancelAll()
    {
        _session.Logger?.LogTrace($"NexusPipeManager.CancelAll();");
        _isCanceled = true;

        // Update all the states of the pipes to complete.
        foreach (var pipeWrapper in _initializingPipes)
        {
            if (pipeWrapper.Value.ValidateState())
            {
                _session.Logger?.LogTrace($"Did not cancel initializing pipe {pipeWrapper.Key} due to state change.");
                continue;
            }

            pipeWrapper.Value.Pipe.UpdateState(NexusDuplexPipe.State.Complete);
            _session.CacheManager.NexusDuplexPipeCache.Return(pipeWrapper.Value.Pipe);
        }

        _initializingPipes.Clear();

        foreach (var pipeWrapper in _activePipes)
        {
            if (pipeWrapper.Value.ValidateState())
            {
                _session.Logger?.LogTrace($"Did not cancel active pipe {pipeWrapper.Key} due to state change.");
                continue;
            }

            pipeWrapper.Value.Pipe.UpdateState(NexusDuplexPipe.State.Complete);
            // Todo: Return the pipe to the cache. Can't in this flow here since it will reset the 
            // result to not completed while a reading process is still ongoing.
            //_session.CacheManager.NexusDuplexPipeCache.Return(pipe.Value);
        }

        _activePipes.Clear();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
}
