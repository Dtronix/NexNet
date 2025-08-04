using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal partial class NexusList<T>
{
    protected override ServerProcessMessageResult OnServerProcessMessage(INexusCollectionMessage message)
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
            case NexusListInsertMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Add));
                break;
            
            case NexusListReplaceMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Replace));
                break;
            
            case NexusListMoveMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Move));
                break;
            
            case NexusListRemoveMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Remove));
                break;
            
            case NexusCollectionClearMessage:
                CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                break;
        }
        
        return new ServerProcessMessageResult(resultMessage, false, true);
    }
}
