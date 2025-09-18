using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Threading;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusList<T>
{
    protected override void OnClientDisconnected()
    {
        _itemList.Reset();
    }
    protected override bool OnClientResetStarted(int version, int totalValues)
    {
        _clientInitialization = new List<T>(totalValues);
        _clientInitializationVersion = version;
        return true;
    }
    
    protected override bool OnClientResetValues(ReadOnlySpan<byte> data)
    {
        var values = MemoryPackSerializer.Deserialize<T[]>(data);
        if(values != null)
            _clientInitialization!.AddRange(values);
        return true;
    }


    protected override bool OnClientResetCompleted()
    {
        if (_clientInitialization == null)
            return false;
        
        var list = ImmutableList<T>.Empty.AddRange(_clientInitialization);

        // Reset the state manually.
        _itemList.ResetTo(list, _clientInitializationVersion);

        _clientInitialization.Clear();
        _clientInitialization = null;
        _clientInitializationVersion = -1;
        return true;
    }
    
    
    protected override IEnumerable<INexusCollectionMessage> ResetValuesEnumerator(NexusCollectionResetValuesMessage message)
    {
        var state = _itemList.State;
        
        // Send the reset start message even if we don't have any data.
        var reset = NexusCollectionResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = state.List.Count;
        
        yield return reset;

        if (state.List.Count == 0)
            yield break;

        var bufferSize = Math.Min(state.List.Count, 40);
        
        reset.ReturnToCache();
        
        foreach (var item in state.List.MemoryChunk(bufferSize))
        {
            message.Values = MemoryPackSerializer.Serialize(item);
            yield return message;
        }
        
    }

    protected override bool OnClientClear(int version)
    {
        var op = ClearOperation<T>.Rent();
        var result = _itemList.ApplyOperation(op, version);
        op.Return();
        return result is ListProcessResult.Successful or ListProcessResult.DiscardOperation;
    }

    private int _clientEventCounter = 0;

    protected override bool OnClientProcessMessage(INexusCollectionMessage serverMessage)
    {
        Interlocked.Increment(ref _clientEventCounter);
        if(!RequireValidProcessState())
            return false;

        var (op, version) = GetRentedOperation(serverMessage);
        
        if(op == null)
            return false;
        
        var result = _itemList.ApplyOperation(op, version);
        
        op.Return();

        if (result == ListProcessResult.Successful || result == ListProcessResult.DiscardOperation)
        {
            switch (serverMessage)
            {
                case NexusListInsertMessage:
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Add));
                    break;

                case NexusListReplaceMessage:
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Replace));
                    break;

                case NexusListMoveMessage:
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Move));
                    break;

                case NexusListRemoveMessage:
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Remove));
                    break;

                case NexusCollectionClearMessage:
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    break;
            }

            return true;
        }
        op.Return();

        Logger?.LogError($"Processing failed. Returned result {result}");
        return false;
    }
}
