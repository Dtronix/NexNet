using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.Collections.Lists;

internal partial class NexusList<T> : NexusCollection, INexusList<T>
{
    private readonly VersionedList<T> _itemList = new();
    private List<T>? _clientInitialization;
    private int _clientInitializationVersion = -1;

    /// <inheritdoc />
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;
    
    public int Count => _itemList.Count;

    public NexusList(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
        : base(id, mode, config, isServer)
    {

    }

    protected override int GetVersion() => _itemList.Version;

    private (Operation<T>? Operation, int Version) GetRentedOperation(INexusCollectionMessage message)
    {
        Operation<T>? op;
        int version;
        switch (message)
        {
            case NexusCollectionClearMessage msg:
                op = ClearOperation<T>.Rent();
                version = msg.Version;
                break;
            case NexusListInsertMessage msg:
                var insOp = InsertOperation<T>.Rent();
                insOp.Index = msg.Index;
                insOp.Item = msg.DeserializeValue<T>()!;
                op = insOp;
                version = msg.Version;
                break;
            
            case NexusListModifyMessage msg:
                var modOp = ModifyOperation<T>.Rent();
                modOp.Index = msg.Index;
                modOp.Value = msg.DeserializeValue<T>()!;
                op = modOp;
                version = msg.Version;
                break;
            
            case NexusListMoveMessage msg:
                var mvOp = MoveOperation<T>.Rent();
                mvOp.FromIndex = msg.FromIndex;
                mvOp.ToIndex = msg.ToIndex;
                op = mvOp;
                version = msg.Version;
                break;
            
            case NexusListRemoveMessage msg:
                var rmOp = RemoveOperation<T>.Rent();
                rmOp.Index = msg.Index;
                op = rmOp;
                version = msg.Version;
                break;
            
            default:
                Logger?.LogError("Could not determine what type of message server sent to client.");
                return (null, -1);
        }
        
        return (op, version);
    }
    
    private INexusCollectionMessage? GetRentedMessage(IOperation operation, int version)
    {
        switch (operation)
        {
            case NoopOperation<T>:
                return null;
            
            case ClearOperation<T>:
            {
                var message = NexusCollectionClearMessage.Rent();
                message.Version = version;
                return message;
            }
            case InsertOperation<T> insert:
            {
                var message = NexusListInsertMessage.Rent();
                message.Version = version;
                message.Index = insert.Index;
                message.Value = MemoryPackSerializer.Serialize(insert.Item);
                return message;
            }
            case ModifyOperation<T> modify:
            {
                var message = NexusListModifyMessage.Rent();
                message.Version = version;
                message.Index = modify.Index;
                message.Value = MemoryPackSerializer.Serialize(modify.Value);
                return message;
            }
            case MoveOperation<T> move:
            {
                var message = NexusListMoveMessage.Rent();
                message.Version = version;
                message.FromIndex = move.FromIndex;
                message.ToIndex = move.ToIndex;
                return message;
            }      
            case RemoveOperation<T> remove:
            {
                var message = NexusListRemoveMessage.Rent();
                message.Version = version;
                message.Index = remove.Index;
                return message;
            }
            default:
                throw new InvalidOperationException($"Could not convert operation of type {operation.GetType()} to message");
        }
    }

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);

    public async Task<bool> RemoveAsync(T item)
    {
        var index = _itemList.IndexOf(item);
        
        if (index == -1)
            return false;
        
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        await UpdateAndWaitAsync(message).ConfigureAwait(false);
        return true;
    }
    public int IndexOf(T item) => _itemList.IndexOf(item);

    public Task<bool> InsertAsync(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusListInsertMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = MemoryPackSerializer.Serialize(item);
        return UpdateAndWaitAsync(message);
    }
    
    public Task<bool> MoveAsync(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        var message = NexusListMoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.FromIndex = fromIndex;
        message.ToIndex = toIndex;
        return UpdateAndWaitAsync(message);
    }

    public Task<bool> RemoveAtAsync(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        return UpdateAndWaitAsync(message);
    }
    
    public Task<bool> AddAsync(T item)
    {
        return InsertAsync(_itemList.Count, item);
    }

    public T this[int index] => _itemList[index];
    
    public IEnumerator<T> GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }
}
