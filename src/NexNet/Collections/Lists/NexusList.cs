using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusList<T> : NexusCollection, INexusList<T>
{
    private readonly VersionedList<T> _itemList;
    private List<T>? _clientInitialization;
    private int _clientInitializationVersion = -1;

    /// <inheritdoc />
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;
    
    public int Count => _itemList.Count;

    public NexusList(ushort id, NexusCollectionMode mode, INexusLogger? logger, bool isServer)
        : base(id, mode, logger, isServer)
    {
        _itemList = new(1024, logger);
    }

    protected override int GetVersion() => _itemList.Version;

    private (Operation<T>? Operation, int Version) GetRentedOperation(INexusCollectionMessage message)
    {
        Operation<T>? op;
        int version;
        switch (message)
        {
            case NexusCollectionListClearMessage msg:
                op = ClearOperation<T>.Rent();
                version = msg.Version;
                break;
            case NexusCollectionListInsertMessage msg:
                var insOp = InsertOperation<T>.Rent();
                insOp.Index = msg.Index;
                insOp.Item = msg.DeserializeValue<T>()!;
                op = insOp;
                version = msg.Version;
                break;
            
            case NexusCollectionListReplaceMessage msg:
                var modOp = ModifyOperation<T>.Rent();
                modOp.Index = msg.Index;
                modOp.Value = msg.DeserializeValue<T>()!;
                op = modOp;
                version = msg.Version;
                break;
            
            case NexusCollectionListMoveMessage msg:
                var mvOp = MoveOperation<T>.Rent();
                mvOp.FromIndex = msg.FromIndex;
                mvOp.ToIndex = msg.ToIndex;
                op = mvOp;
                version = msg.Version;
                break;
            
            case NexusCollectionListRemoveMessage msg:
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
                var message = NexusCollectionListClearMessage.Rent();
                message.Version = version;
                return message;
            }
            case InsertOperation<T> insert:
            {
                var message = NexusCollectionListInsertMessage.Rent();
                message.Version = version;
                message.Index = insert.Index;
                message.Value = MemoryPackSerializer.Serialize(insert.Item);
                return message;
            }
            case ModifyOperation<T> modify:
            {
                var message = NexusCollectionListReplaceMessage.Rent();
                message.Version = version;
                message.Index = modify.Index;
                message.Value = MemoryPackSerializer.Serialize(modify.Value);
                return message;
            }
            case MoveOperation<T> move:
            {
                var message = NexusCollectionListMoveMessage.Rent();
                message.Version = version;
                message.FromIndex = move.FromIndex;
                message.ToIndex = move.ToIndex;
                return message;
            }      
            case RemoveOperation<T> remove:
            {
                var message = NexusCollectionListRemoveMessage.Rent();
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
    public int IndexOf(T item) => _itemList.IndexOf(item);
    public async Task<bool> RemoveAsync(T item)
    {
        EnsureAllowedModificationState();
        var message = NexusCollectionListRemoveMessage.Rent();
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
    
    public async Task<bool> InsertAsync(int index, T item)
    {
        EnsureAllowedModificationState();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var message = NexusCollectionListInsertMessage.Rent();

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
        var message = NexusCollectionListMoveMessage.Rent();

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
        var message = NexusCollectionListReplaceMessage.Rent();

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
        var message = NexusCollectionListRemoveMessage.Rent();
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
        var message = NexusCollectionListInsertMessage.Rent();

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
    }
}
