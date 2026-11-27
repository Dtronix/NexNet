using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

            // The request was made obsolete by a concurrent change: answer the originator only, with Ack set, so
            // its pending request completes with false. Nothing changed, so nothing is broadcast.
            // The answer goes through the broadcast channel, so the originator sees the winning change first.
            if (sourceClient != null)
            {
                var noop = NexusCollectionListNoopMessage.Rent();
                noop.Flags = NexusCollectionMessageFlags.Ack;
                return new ProcessResult(null, false, noop);
            }

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

    protected override IEnumerable<INexusCollectionListMessage> CaptureSnapshot()
    {
        // Captured here, on the processing loop; the messages are built as they are written.
        var state = _itemList.State;
        return SnapshotMessages(state, state.List.Count);
    }

    private static IEnumerable<INexusCollectionListMessage> SnapshotMessages(VersionedList<T>.ListState state, int totalValues)
    {
        var reset = NexusCollectionListResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = totalValues;
        yield return reset;

        if (state.List.Count > 0)
        {
            var bufferSize = Math.Min(state.List.Count, 40);
            foreach (var item in state.List.MemoryChunk(bufferSize))
            {
                var message = NexusCollectionListResetValuesMessage.Rent();
                message.Values = PayloadSerializer.SerializeValues(item);
                yield return message;
            }
        }

        yield return NexusCollectionListResetCompleteMessage.Rent();
    }
}
