using System;
using MemoryPack;
using NexNet.Internals.Collections.Versioned;

namespace NexNet.Collections.Lists;

internal class NexusListTransformers<T>
{
    
    protected (Operation<T>? Operation, int Version) RentOperation(INexusCollectionMessage message)
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
    
    protected override INexusCollectionMessage? GetRentedMessage(IOperation operation, int version)
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

}
