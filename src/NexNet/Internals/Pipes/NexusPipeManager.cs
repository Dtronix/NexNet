using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
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

        public bool IsStateValid => _state == Pipe.StateId;

        public PipeAndState(NexusDuplexPipe pipe)
        {
            Pipe = pipe;
            _state = pipe.StateId;
        }
    }

    private INexusLogger? _logger;
    private INexusSession _session = null!;
    private readonly ConcurrentDictionary<ushort, PipeAndState> _activePipes = new();
    private readonly ConcurrentDictionary<byte, PipeAndState> _initializingPipes = new();


    private readonly BitArray _usedIds = new(256, false);
    //private readonly Stack<byte> _availableIds = new Stack<byte>();

    private int _currentId;

    private bool _isCanceled;

    public void Setup(INexusSession session)
    {
        _usedIds.SetAll(false);
        _isCanceled = false;
        _session = session;
        _logger = session.Logger?.CreateLogger<NexusPipeManager>();
    }

    public IRentedNexusDuplexPipe? RentPipe()
    {
        if (_isCanceled)
            return null;

        var id = GetLocalId();
        var pipe = _session.CacheManager.NexusRentedDuplexPipeCache.Rent(_session, id);
        pipe.InitiatingPipe = true;
        pipe.Manager = this;
        _initializingPipes.TryAdd(id, new PipeAndState(pipe));

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
        _activePipes.TryRemove(pipe.Id, out _);

        var (clientId, serverId) = GetClientAndServerId(pipe.Id);
        var localId = _session.IsServer ? serverId : clientId;

        _activePipes.TryRemove(pipe.Id, out _);
        _initializingPipes.TryRemove(localId, out _);

        var nexusPipe = (RentedNexusDuplexPipe)pipe;
        
        if(nexusPipe.CurrentState != State.Complete)
        {
            await pipe.CompleteAsync();
            // Return the pipe to the cache.
            nexusPipe.Reset();
        }

        _session.CacheManager.NexusDuplexPipeCache.Return(nexusPipe);

        Console.WriteLine($"Pipe [{localId}] {pipe.Id} returned.");

        lock (_usedIds)
        {
            // Return the local ID to the available IDs list.
            _usedIds.Set(localId, false);
        }
        
    }

    public async ValueTask<INexusDuplexPipe> RegisterPipe(byte otherId)
    {
        if (_session == null)
            throw new InvalidOperationException("Session if not available for usage.");

        _logger?.LogTrace($"RegisterPipe({otherId});");
        if (_isCanceled)
            throw new InvalidOperationException("Can't register duplex pipe due to cancellation.");

        var id = GetCompleteId(otherId, out var thisId);

        var pipe = _session.CacheManager.NexusRentedDuplexPipeCache.Rent(_session, thisId);


        if (!_activePipes.TryAdd(id, new PipeAndState(pipe)))
            throw new Exception("Could not add NexusDuplexPipe to the list of current pipes.");

        await pipe.PipeReady(id).ConfigureAwait(false);

        return pipe;
    }

    public async ValueTask DeregisterPipe(INexusDuplexPipe pipe)
    {
        _logger?.LogTrace($"DeregisterPipe({pipe.Id});");

        if (!_activePipes.TryRemove(pipe.Id, out var nexusPipe))
            return;

        var (clientId, serverId) = GetClientAndServerId(pipe.Id);

        await nexusPipe.Pipe.CompleteAsync();

        // Return the pipe to the cache.
        nexusPipe.Pipe.Reset();

        _session.CacheManager.NexusDuplexPipeCache.Return(nexusPipe.Pipe);

        lock (_usedIds)
        {
            // Return the local ID to the available IDs list.
            _usedIds.Set(_session.IsServer ? serverId : clientId, false);
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

        if (_activePipes.TryGetValue(id, out var pipeWrapper))
        {
            if (!pipeWrapper.IsStateValid)
            {
                _logger?.LogTrace($"Ignored data due to pipe changing state form last .");
                return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);
            }

            // Check to see if we have exceeded the high water cutoff for the pipe.
            // If we have, then disconnect the connection.
            return pipeWrapper.Pipe.WriteFromUpstream(data);
        }

        _logger?.LogError($"Received data on NexusDuplexPipe id: {id} but no stream is open on this id.");
        //throw new InvalidOperationException($"No pipe exists for id: {id}.");
        return new ValueTask<NexusPipeBufferResult>(NexusPipeBufferResult.DataIgnored);
    }

    public void UpdateState(ushort id, NexusDuplexPipe.State state)
    {
        if (_isCanceled)
            return;

        if (_activePipes.TryGetValue(id, out var pipeWrapper))
        {
            if (!pipeWrapper.IsStateValid)
            {
                _logger?.LogTrace($"State update of {state} ignored due to state change.");
                return;
            }

            pipeWrapper.Pipe.UpdateState(state);
        }
        else
        {
            var (clientId, serverId) = GetClientAndServerId(id);
            var thisId = _session.IsServer ? serverId : clientId;
            if (!_initializingPipes.TryRemove(thisId, out var initialPipe))
            {
                _logger?.LogTrace($"Could not find pipe with initial ID of {thisId}");
                return;
            }

            // Move the pipe to the main active pipes.
            _activePipes.TryAdd(id, initialPipe);
            initialPipe.Pipe.Id = id;
            initialPipe.Pipe.UpdateState(state);
        }
    }

    public void CancelAll()
    {
        _logger?.LogTrace($"CancelAll();");
        _isCanceled = true;

        // Update all the states of the pipes to complete.
        foreach (var pipeWrapper in _initializingPipes)
        {
            if (!pipeWrapper.Value.IsStateValid)
            {
                _logger?.LogTrace($"Did not cancel initializing pipe {pipeWrapper.Key} due to state change.");
                continue;
            }

            pipeWrapper.Value.Pipe.UpdateState(NexusDuplexPipe.State.Complete);
        }

        _initializingPipes.Clear();

        foreach (var pipeWrapper in _activePipes)
        {
            if (!pipeWrapper.Value.IsStateValid)
            {
                _logger?.LogTrace($"Did not cancel active pipe {pipeWrapper.Key} due to state change.");
                continue;
            }

            pipeWrapper.Value.Pipe.UpdateState(NexusDuplexPipe.State.Complete);
            // Todo: Return the pipe to the cache. Can't in this flow here since it will reset the 
            // result to not completed while a reading process is still ongoing.
            //_session.CacheManager.NexusDuplexPipeCache.Return(pipe.Value);
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
            thisId = idBytes[1] = GetLocalId(); // Server
        }
        else
        {
            thisId = idBytes[0] = GetLocalId(); // Client
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

    private byte GetLocalId()
    {
        lock (_usedIds)
        {
            var incrementedId = _currentId;

            for (int i = 0; i < 255; i++)
            {
                // Loop around if we reach the end of the available IDs.
                if (++incrementedId == 256)
                    incrementedId = 0;

                // If the Id is not available, then try the next one.
                if (_usedIds.Get(incrementedId))
                    continue;

                _currentId = incrementedId;
                _usedIds.Set(incrementedId, true);
                if(_session.IsServer)
                    Console.WriteLine($"GetLocalId() = {incrementedId}");
                return (byte)incrementedId;
            }
        }

        // If we reach the end of the available IDs, then we are full and can throw an exception.
        throw new InvalidOperationException("Exceeded maximum number of concurrent streams.");
    }
}
