using System;
using System.Threading;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal class NexusListBaseCollectionMessageProcessor<T> : NexusCollectionMessageProcessor<T>
{
    private readonly SubscriptionEvent<NexusCollectionChangedEventArgs> _changedEvent;
    private readonly VersionedList<T> _list;
    private readonly bool _isServer;

    public NexusListBaseCollectionMessageProcessor(
        INexusLogger? logger,
        SubscriptionEvent<NexusCollectionChangedEventArgs> changedEvent,
        VersionedList<T> list,
        bool isServer)
        : base(logger)
    {
        _changedEvent = changedEvent;
        _list = list;
        _isServer = isServer;
    }
    
    protected (Operation<T>? Operation, int Version) GetRentedOperation(INexusCollectionMessage message)
    {
        Operation<T>? op;
        int version;
        switch (message)
        {
            case NexusListClearMessage msg:
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
            
            case NexusListReplaceMessage msg:
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
    
    protected override INexusCollectionMessage? GetRentedMessage(IOperation operation, int version)
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
                var message = NexusListReplaceMessage.Rent();
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

    protected override bool OnProcess(INexusCollectionMessage message, CancellationToken ct)
    {
        var (op, version) = GetRentedOperation(message);
        
        if(op == null)
            return false;
        
        var result = _list.ApplyOperation(op, version);
        
        op.Return();

        if (result == ListProcessResult.Successful || result == ListProcessResult.DiscardOperation)
        {
            using var eventArgsOwner = NexusCollectionChangedEventArgs.Rent();
            switch (message)
            {
                case NexusListInsertMessage:
                    eventArgsOwner.Value.ChangedAction = NexusCollectionChangedAction.Add;
                    _changedEvent.Raise(eventArgsOwner.Value);
                    break;

                case NexusListReplaceMessage:
                    eventArgsOwner.Value.ChangedAction = NexusCollectionChangedAction.Replace;
                    _changedEvent.Raise(eventArgsOwner.Value);
                    break;

                case NexusListMoveMessage:
                    eventArgsOwner.Value.ChangedAction = NexusCollectionChangedAction.Move;
                    _changedEvent.Raise(eventArgsOwner.Value);
                    break;

                case NexusListRemoveMessage:
                    eventArgsOwner.Value.ChangedAction = NexusCollectionChangedAction.Remove;
                    _changedEvent.Raise(eventArgsOwner.Value);
                    break;

                case NexusListClearMessage:
                    eventArgsOwner.Value.ChangedAction = NexusCollectionChangedAction.Reset;
                    _changedEvent.Raise(eventArgsOwner.Value);
                    break;
            }

            return true;
        }
        op.Return();

        Logger?.LogError($"Processing failed. Returned result {result}");
        return false;
    }
}
