using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections.Lists;

internal class NexusList<T> : NexusCollection, INexusList<T>
{
    private readonly VersionedList<T> _itemList = new();
    private List<T>? _clientInitialization;
    private int _clientInitializationVersion = -1;
    private static readonly Type TType = typeof(T);
    
    public int Count => _itemList.Count;

    public NexusList(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
        : base(id, mode, config, isServer)
    {
        
    }

    protected override void DisconnectedFromServer()
    {
        _itemList.Reset();
    }

    private bool RequireValidState()
    {
        if (_clientInitialization != null || _clientInitializationVersion != -1)
            return false;

        // If this is the server, and we are not in bidirectional mode, then the client
        // is sending messages when they are not supposed to.
        return !IsServer || Mode == NexusCollectionMode.BiDrirectional;
    }

    private (INexusCollectionMessage? message, bool disconnect, INexusCollectionMessage? ackMessage) ProcessMessage(Operation<T> operation, int version, int id)
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
    
    protected override (INexusCollectionMessage? message, bool disconnect, INexusCollectionMessage? ackMessage) ProcessOperation(INexusCollectionMessage operation)
    {
        switch (operation)
        {
            case NexusListClearMessage message:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(new ClearOperation<T>(), message.Version, message.Id);

            case NexusListInsertMessage message:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new InsertOperation<T>(message.Index, message.DeserializeValue<T>()!),
                        message.Version,
                        message.Id);

            case NexusListModifyMessage message:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new ModifyOperation<T>(message.Index, message.DeserializeValue<T>()!),
                        message.Version,
                        message.Id);

            case NexusListMoveMessage message:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new MoveOperation<T>(message.FromIndex, message.ToIndex),
                        message.Version,
                        message.Id);

            case NexusListRemoveMessage message:
                return !RequireValidState()
                    ? (null, true, null)
                    : ProcessMessage(
                        new RemoveOperation<T>(message.Index),
                        message.Version,
                        message.Id);

            

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

    protected override bool OnProcessClientMessage(INexusCollectionMessage serverMessage)
    {
        switch (serverMessage)
        {
            case NexusCollectionResetStartMessage:
                _clientInitialization = new List<T>();
                return true;
            
            case NexusCollectionResetValuesMessage message:
                _clientInitialization!.AddRange(MemoryPackSerializer.Deserialize<T>(message.Values.Span));
                return true;

            case NexusCollectionResetCompleteMessage:
                var list = ImmutableList<T>.Empty.AddRange(_clientInitialization);

                // Reset the state manually.
                _itemList.ResetTo(list, _clientInitializationVersion);

                _clientInitialization.Clear();
                _clientInitialization = null;
                _clientInitializationVersion = -1;
                return true;
        }

        return false;
    }

    private INexusCollectionMessage? ConvertOperationToMessage(IOperation operation, int version)
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

    protected override IEnumerable<NexusCollectionResetValuesMessage> ResetValuesEnumerator(NexusCollectionResetValuesMessage message)
    {
        var state = _itemList.State;

        if (state.List.Count == 0)
            yield break;
        
        var bufferSize = Math.Min(state.List.Count, 40);
        
        foreach (var item in state.List.MemoryChunk(bufferSize))
        {
            message.Values = MemoryPackSerializer.Serialize(item);
            yield return message;
        }
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
        return UpdateAndWaitAsync(message);
    }

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
        var message = NexusListInsertMessage.Rent();
        
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = MemoryPackSerializer.Serialize(item);
        return UpdateAndWaitAsync(message);
    }

    public Task<bool> RemoveAtAsync(int index)
    {
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
    public override IEnumerator<T> GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }
}
