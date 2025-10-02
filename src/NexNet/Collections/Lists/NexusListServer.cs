using System;
using System.Collections.Generic;
using System.Threading;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusListServer<T> : NexusCollectionServer
{
    private readonly VersionedList<T> _itemList;
    
    public int Count => _itemList.Count;

    public NexusListServer(ushort id, NexusCollectionMode mode, INexusLogger? logger) 
        :base(id, mode, logger)
    {
        _itemList = new(1024, logger);
    }
    
    protected override IEnumerable<INexusCollectionMessage> ResetValuesEnumerator()
    {
        var state = _itemList.State;
        
        // Send the reset start message even if we don't have any data.
        var reset = NexusCollectionListResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = state.List.Count;
        
        yield return reset;

        if (state.List.Count == 0)
            yield break;

        var bufferSize = Math.Min(state.List.Count, 40);
        
        reset.ReturnToCache();
        
        foreach (var item in state.List.MemoryChunk(bufferSize))
        {
            var message = NexusCollectionListResetValuesMessage.Rent();
            message.Values = MemoryPackSerializer.Serialize(item);
            yield return message;
        }
        
        var resetComplete = NexusCollectionListResetCompleteMessage.Rent();
        yield return resetComplete;
    }

    protected override INexusCollectionMessage? OnProcess(INexusCollectionMessage process, CancellationToken ct)
    {
        var op = GetRentedOperation(message);

        if (op.Operation == null)
            return new ServerProcessMessageResult(null, true, false);
        
        var opResult = _itemList.ProcessOperation(
            op.Operation,
            op.Version,
            out var processResult);
            
        // If the operational result is null, but the result is success, this is an unknown state.
        // Ensure this is not just a noop.
        if ((processResult != ListProcessResult.Successful && processResult != ListProcessResult.DiscardOperation)
            || opResult == null)
        {
            op.Operation.Return();
            return new ServerProcessMessageResult(null, true, false);
        }

        // Operation was valid, but now it has been noop'd
        if (opResult is NoopOperation<T>)
        {
            op.Operation.Return();
            base.Logger?.LogTrace("Nooped");
            return new ServerProcessMessageResult(null, true, true);
        }

        var resultMessage = GetRentedMessage(opResult, _itemList.Version);
        
        op.Operation.Return();
        
        switch (message)
        {
            case NexusCollectionListInsertMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Add));
                break;
            
            case NexusCollectionListReplaceMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Replace));
                break;
            
            case NexusCollectionListMoveMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Move));
                break;
            
            case NexusCollectionListRemoveMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Remove));
                break;
            
            case NexusCollectionListClearMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                break;
        }
        
        return new ServerProcessMessageResult(resultMessage, false, true);
    
    }
    
    /*

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _itemList.IndexOf(item);
    public async Task<bool> RemoveAsync(T item)
    {
        EnsureAllowedModificationState();
        var message = NexusListRemoveMessage.Rent();
        using (_ = await OperationLock().ConfigureAwait(false))
        {
            var index = _itemList.IndexOf(item, out var version);

            if (index == -1)
                return false;

            message.Version = version;
            message.Index = index;
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }
    
    public async Task<bool> ClearAsync()
    {
        EnsureAllowedModificationState();
        var message = NexusListClearMessage.Rent();
        using (_ = await OperationLock().ConfigureAwait(false))
        {
            message.Version = GetVersion();
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }
    
    public async Task<bool> InsertAsync(int index, T item)
    {
        EnsureAllowedModificationState();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusListInsertMessage.Rent();

        using (_ = await OperationLock().ConfigureAwait(false))
        {
            message.Version = _itemList.Version;
            message.Index = index;
            message.Value = MemoryPackSerializer.Serialize(item);
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }

    public async Task<bool> MoveAsync(int fromIndex, int toIndex)
    {
        EnsureAllowedModificationState();
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        var message = NexusListMoveMessage.Rent();

        using (_ = await OperationLock().ConfigureAwait(false))
        {
            message.Version = _itemList.Version;
            message.FromIndex = fromIndex;
            message.ToIndex = toIndex;
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }

    public async Task<bool> ReplaceAsync(int index, T value)
    {
        EnsureAllowedModificationState();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusListReplaceMessage.Rent();

        using (_ = await OperationLock().ConfigureAwait(false))
        {
            message.Version = _itemList.Version;
            message.Index = index;
            message.Value = MemoryPackSerializer.Serialize(value);
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }

    public async Task<bool> RemoveAtAsync(int index)
    {
        EnsureAllowedModificationState();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusListRemoveMessage.Rent();
        using (_ = await OperationLock().ConfigureAwait(false))
        {
            message.Version = _itemList.Version;
            message.Index = index;
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }
    
    public async Task<bool> AddAsync(T item)
    {
        EnsureAllowedModificationState();
        var message = NexusListInsertMessage.Rent();

        using (_ = await OperationLock().ConfigureAwait(false))
        {
            var state = _itemList.State;
            message.Version = state.Version;
            message.Index = state.List.Count;
            message.Value = MemoryPackSerializer.Serialize(item);
            return await UpdateAndWaitAsync(message).ConfigureAwait(false);
        }
    }

    public T this[int index] => _itemList[index];
    
    public IEnumerator<T> GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }*/
 
}
