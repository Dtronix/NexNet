using System;
using System.Threading;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusList<T>
{
    private int _serverEventCounter = 0;
    protected override ServerProcessMessageResult OnServerProcessMessage(INexusCollectionMessage message)
    {
        Interlocked.Increment(ref _serverEventCounter);
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
        using (var args = NexusCollectionChangedEventArgs.Rent(message switch
               {
                   NexusCollectionListInsertMessage => NexusCollectionChangedAction.Add,
                   NexusCollectionListRemoveMessage => NexusCollectionChangedAction.Remove,
                   NexusCollectionListReplaceMessage => NexusCollectionChangedAction.Replace,
                   NexusCollectionListMoveMessage => NexusCollectionChangedAction.Move,
                   NexusCollectionListClearMessage => NexusCollectionChangedAction.Reset,
                   _ => throw new ArgumentOutOfRangeException(nameof(message), message, null)
               }))
        {
            CoreChangedEvent.Raise(args.Value);
        }
        
        return new ServerProcessMessageResult(resultMessage, false, true);
    }
}
