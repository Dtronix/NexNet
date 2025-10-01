using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusList2<T> : NexusCollectionServer
{
    private readonly VersionedList<T> _itemList;

    /// <inheritdoc />
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;
    
    public int Count => _itemList.Count;

    public NexusList2(ushort id, NexusCollectionMode mode, INexusLogger? logger, bool isServer) 
        : base(id, mode, logger, isServer)
    {
        _itemList = new(1024, logger);
    }

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
    }
}
