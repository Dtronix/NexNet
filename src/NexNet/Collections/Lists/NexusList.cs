using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Pipes;

namespace NexNet.Collections.Lists;

internal class NexusList<T> : NexusCollection<T, INexusListMessage>, INexusList<T>
{
    private readonly VersionedList<T> _itemList = new();
    public int Count => _itemList.Count;
    public bool IsReadOnly => !IsServer && Mode != NexusCollectionMode.BiDrirectional;
    
    public NexusList(ushort id, NexusCollectionMode mode, bool isServer)
        : base(id, mode, isServer)
    {
        
    }

    protected override void DisconnectedFromServer()
    {
        _itemList.Reset();
    }

    protected override async ValueTask InitializeNewClient(NexusChannelWriter<INexusListMessage> writer)
    {
              
        var op = NexusListAddItemMessage.Rent();
        var state = _itemList.CurrentState;
        foreach (var item in state.List)
        {
            op.Value = MemoryPackSerializer.Serialize(TType, item);
            await writer.WriteAsync(op);
        }
        
        NexusListAddItemMessage.Cache.Add(op);
    }

    private List<T>? _clientInitialization;
    private int _clientInitializationVersion;

    private bool RequireValidState()
    {
        if (_clientInitialization != null || _clientInitializationVersion != -1)
            return false;

        // If this is the server and we are not in bidirectional mode, then the client
        // is sending messages when they are not supposed to.
        if (IsServer && Mode != NexusCollectionMode.BiDrirectional)
            return false;

        return true;
    }

    private async ValueTask<bool> ProcessAndBroadcast(Operation<T> operation, int version)
    {
        var opResult = _itemList.ProcessOperation(
            operation,
            version,
            out var processResult);

        if (processResult != ListProcessResult.Successful)
            return false;

        if (IsServer && opResult != null)
            await BroadcastAsync(opResult, _itemList.Version);

        return true;
    }
    protected override ValueTask<bool> ProcessOperation(INexusListMessage operation)
    {
        switch (operation)
        {
            case NexusListClearMessage op:
                return !RequireValidState() 
                    ? new ValueTask<bool>(false) 
                    : ProcessAndBroadcast(new ClearOperation<T>(), op.Version);

            case NexusListInsertMessage op:
                return !RequireValidState()
                    ? new ValueTask<bool>(false)
                    : ProcessAndBroadcast(
                        new InsertOperation<T>(op.Index, op.DeserializeValue<T>()!),
                        op.Version);

            case NexusListModifyMessage op:
                return !RequireValidState()
                    ? new ValueTask<bool>(false)
                    : ProcessAndBroadcast(
                        new ModifyOperation<T>(op.Index, op.DeserializeValue<T>()!),
                        op.Version);

            case NexusListMoveMessage op:
                return !RequireValidState()
                    ? new ValueTask<bool>(false)
                    : ProcessAndBroadcast(
                        new MoveOperation<T>(op.FromIndex, op.ToIndex),
                        op.Version);

            case NexusListRemoveMessage op:
                return !RequireValidState()
                    ? new ValueTask<bool>(false)
                    : ProcessAndBroadcast(
                        new RemoveOperation<T>(op.Index),
                        op.Version);

            // No broadcasting on the reset operations as they are only client operations.
            case NexusListStartResetMessage resetOperation:
                if (IsServer)
                    return new ValueTask<bool>(false);

                _clientInitialization = new List<T>(resetOperation.Count);
                _clientInitializationVersion = resetOperation.Version;
                break;

            case NexusListCompleteResetMessage:
                if (IsServer || _clientInitialization == null || _clientInitializationVersion == -1)
                    return new ValueTask<bool>(false);

                var list = ImmutableList<T>.Empty.AddRange(_clientInitialization);

                // Reset the state manually.
                _itemList.ResetTo(list, _clientInitializationVersion);

                _clientInitialization.Clear();
                _clientInitialization = null;
                _clientInitializationVersion = -1;
                break;

            case NexusListAddItemMessage addOperation:
                if (IsServer || _clientInitialization == null || _clientInitializationVersion == -1)
                    return new ValueTask<bool>(false);
                
                _clientInitialization.Add(addOperation.DeserializeValue<T>()!);
                
                break;
        }

        return new ValueTask<bool>(false);
    }

    protected override INexusListMessage ConvertToListOperation(IOperation operation, int version)
    {
        switch (operation)
        {
            case ClearOperation<T>:
            {
                var message = NexusListClearMessage.Rent();
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

    public ValueTask Clear()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot perform operations when collection is read-only");
        
        var message = NexusListClearMessage.Rent();
        message.Version = _itemList.Version;
        return UpdateServerAsync(message);
    }

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);

    public async ValueTask<bool> Remove(T item)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot perform operations when collection is read-only");
        var index = _itemList.IndexOf(item);
        
        if (index == -1)
            return false;
        
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        await UpdateServerAsync(message);
        return true;
    }
    public int IndexOf(T item) => _itemList.IndexOf(item);

    public ValueTask Insert(int index, T item)
    {
        var message = NexusListInsertMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = _itemList.IndexOf(item);
        message.Value = MemoryPackSerializer.Serialize(item);
        return UpdateServerAsync(message);
    }

    public ValueTask RemoveAt(int index)
    {
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        return UpdateServerAsync(message);
    }


}
