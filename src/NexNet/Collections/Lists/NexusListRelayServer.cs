using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections.Lists;

internal class NexusListRelayServer<T> : NexusBroadcastServer<INexusCollectionListMessage>, INexusListRelay<T>
{
    private readonly VersionedList<T> _itemList;

    public int Count => _itemList.Count;
    public bool IsReadOnly { get; } = false;
    
    public NexusCollectionState State { get; }
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;
    
    public NexusListRelayServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
        : base(id, mode, logger)
    {
        _itemList = new(1024, logger);
    }

    protected override ProcessResult OnProcess(INexusCollectionListMessage message,
        INexusBroadcastSession<INexusCollectionListMessage>? sourceClient,
        CancellationToken ct)
    {
        // These are not allowed to be sent by the client to the server.
        if (message is NexusCollectionListResetStartMessage
            or NexusCollectionListResetCompleteMessage
            or NexusCollectionListResetValuesMessage
            or NexusCollectionListNoopMessage)
            return new ProcessResult(null, true);

        var (op, version) = NexusListTransformers<T>.RentOperation(message);

        // Unknown operation
        if (op == null)
            return new ProcessResult(null, true);

        var opResult = _itemList.ProcessOperation(op, version, out var processResult);

        // Operation was valid, but now it has been noop'd
        if (opResult is NoopOperation<T>)
        {
            op.Return();
            sourceClient?.BufferTryWrite(NexusCollectionListNoopMessage.Rent().Wrap());
            return new ProcessResult(null, false);
        }

        // If the operational result is null, but the result is success, this is an unknown state.
        // Ensure this is not just a noop.
        if (processResult != ListProcessResult.Successful || opResult == null)
        {
            op.Return();
            return new ProcessResult(null, true);
        }

        var (resultMessage, action) = NexusListTransformers<T>.RentMessageAction(op, _itemList.Version);

        op.Return();

        using (var args = NexusCollectionChangedEventArgs.Rent(action))
        {
            CoreChangedEvent.Raise(args.Value);
        }

        return new ProcessResult(resultMessage, false);
    }



    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _itemList.IndexOf(item);
    
    public T this[int index] => _itemList[index];

    public IEnumerator<T> GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }
    
    public ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        // Empty operation as the server is the authoritative source
        return new ValueTask<bool>(true);
    }

    public ValueTask DisableAsync()
    {
        // No disabling of the connection.
        return default;
    }

    public Task DisabledTask => throw new InvalidOperationException("Server can't be disabled.");

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }

    protected override void OnConnected(NexusBroadcastSession<INexusCollectionListMessage> client)
    {
        var state = _itemList.State;
        
        var reset = NexusCollectionListResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = state.List.Count;
        client.BufferTryWrite(reset.Wrap());
        
        foreach (var values in ResetValuesEnumerator(state))
        {
            client.BufferTryWrite(values);
        }
        
        client.BufferTryWrite(NexusCollectionListResetCompleteMessage.Rent().Wrap());
    }
    private IEnumerable<INexusCollectionBroadcasterMessageWrapper<INexusCollectionListMessage>> ResetValuesEnumerator(
        VersionedList<T>.ListState state)
    {
        if (state.List.Count == 0)
            yield break;

        var bufferSize = Math.Min(state.List.Count, 40);
        
        foreach (var item in state.List.MemoryChunk(bufferSize))
        {
            var message = NexusCollectionListResetValuesMessage.Rent();
            message.Values = MemoryPackSerializer.Serialize(item);
            yield return message.Wrap();
        }
    }
    
}
