using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections.Lists;

internal class NexusList<T> : NexusCollection<T, INexusListMessage>, INexusList<T>
{
    private readonly VersionedList<T> _itemList = new();
    private List<T>? _clientInitialization;
    private int _clientInitializationVersion = -1;
    
    public int Count => _itemList.Count;

    public NexusList(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
        : base(id, mode, config, isServer)
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
            await writer.WriteAsync(op).ConfigureAwait(false);
        }
        
        NexusListAddItemMessage.Cache.Add(op);
    }

    private bool RequireValidState()
    {
        if (_clientInitialization != null || _clientInitializationVersion != -1)
            return false;

        // If this is the server, and we are not in bidirectional mode, then the client
        // is sending messages when they are not supposed to.
        return !IsServer || Mode == NexusCollectionMode.BiDrirectional;
    }

    private (INexusListMessage? message, bool disconnect, INexusListMessage? ackMessage) ProcessMessage(Operation<T> operation, int version, int id)
    {
        ListProcessResult processResult;
        if (IsServer)
        {
            var opResult = _itemList.ProcessOperation(
                operation,
                version,
                out processResult);
            
            // If the operational result is null, but the result is success, this is an unknown state.
            // Ensure this is not just a noop.
            if ((processResult != ListProcessResult.Successful && processResult != ListProcessResult.DiscardOperation)
                || opResult == null)
                return (null, true, null);

            var message = NexusCollectionAckMessage.Rent();
            message.Remaining = 1;
            message.Id = id;
            return (ConvertOperationToMessage(opResult, _itemList.Version), false, message);
        }

        // Client processing.  No broadcasting of changes.
        processResult = _itemList.ApplyOperation(operation, version);
        
        if (processResult != ListProcessResult.Successful)
            return (null, true, null);
        
        return (null, false, null);
    }
    
    protected override (INexusListMessage? message, bool disconnect, INexusListMessage? ackMessage) ProcessOperation(INexusListMessage operation)
    {
        switch (operation)
        {
            case NexusListClearMessage op:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(new ClearOperation<T>(), op.Version, op.Id);

            case NexusListInsertMessage op:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new InsertOperation<T>(op.Index, op.DeserializeValue<T>()!),
                        op.Version,
                        op.Id);

            case NexusListModifyMessage op:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new ModifyOperation<T>(op.Index, op.DeserializeValue<T>()!),
                        op.Version,
                        op.Id);

            case NexusListMoveMessage op:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new MoveOperation<T>(op.FromIndex, op.ToIndex),
                        op.Version,
                        op.Id);

            case NexusListRemoveMessage op:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new RemoveOperation<T>(op.Index),
                        op.Version,
                        op.Id);

            // No broadcasting on the reset operations as they are only client operations.
            case NexusListStartResetMessage resetOperation:
                if (IsServer)
                    return (null, true, null);

                _clientInitialization = new List<T>(resetOperation.Count);
                _clientInitializationVersion = resetOperation.Version;
                break;

            case NexusListCompleteResetMessage:
                if (IsServer || _clientInitialization == null || _clientInitializationVersion == -1)
                    return (null, true, null);

                var list = ImmutableList<T>.Empty.AddRange(_clientInitialization);

                // Reset the state manually.
                _itemList.ResetTo(list, _clientInitializationVersion);

                _clientInitialization.Clear();
                _clientInitialization = null;
                _clientInitializationVersion = -1;
                break;

            case NexusListAddItemMessage addOperation:
                if (IsServer || _clientInitialization == null || _clientInitializationVersion == -1)
                    return (null, true, null);
                
                _clientInitialization.Add(addOperation.DeserializeValue<T>()!);
                
                break;
            
            case NexusCollectionAckMessage ackOperation:
                return (null, false, null);
        }

        return (null, true, null);
    }

    private INexusListMessage? ConvertOperationToMessage(IOperation operation, int version)
    {
        switch (operation)
        {
            case NoopOperation<T>:
                return null;
            
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

    protected override async ValueTask SendClientInitData(NexusChannelWriter<INexusListMessage> operation)
    {
        var state = _itemList.State;

        if (state.List.Count == 0)
            return;
        
        var reset = NexusListStartResetMessage.Rent();
        var resetComplete = NexusListCompleteResetMessage.Rent();
        await operation.WriteAsync(reset);
        
        


        foreach (var item in state.List)
        {
            
        }
        
        await operation.WriteAsync(resetComplete);
        reset.ReturnToCache();
        resetComplete.ReturnToCache();
        return default;
    }

    public void Reset()
    {
        _clientInitialization = null;
        _clientInitializationVersion = -1;
        _itemList.Reset();
    }

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);
    
    public Task<bool> ClearAsync()
    {
        var message = NexusListClearMessage.Rent();
        message.Version = _itemList.Version;
        return UpdateServerAsync(message);
    }

    public async Task<bool> RemoveAsync(T item)
    {
        var index = _itemList.IndexOf(item);
        
        if (index == -1)
            return false;
        
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        await UpdateServerAsync(message).ConfigureAwait(false);
        return true;
    }
    public int IndexOf(T item) => _itemList.IndexOf(item);

    public Task<bool> InsertAsync(int index, T item)
    {
        var message = NexusListInsertMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = MemoryPackSerializer.Serialize(item);
        return UpdateServerAsync(message);
    }

    public Task<bool> RemoveAtAsync(int index)
    {
        var message = NexusListRemoveMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        return UpdateServerAsync(message);
    }
    
    public Task<bool> AddAsync(T item)
    {
        return InsertAsync(_itemList.Count, item);
    }

    public T this[int index] => _itemList[index];
}
