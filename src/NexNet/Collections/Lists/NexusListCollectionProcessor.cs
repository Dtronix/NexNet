using System.Threading;
using NexNet.Logging;

namespace NexNet.Collections.Lists;

internal class NexusListCollectionProcessor : NexusCollectionProcessor
{
    public NexusListCollectionProcessor(INexusLogger? logger) : base(logger)
    {
    }

    protected override bool OnProcess(INexusCollectionMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref _clientEventCounter);
        if(!RequireValidProcessState())
            return false;

        var (op, version) = GetRentedOperation(serverMessage);
        
        if(op == null)
            return false;
        
        var result = _itemList.ApplyOperation(op, version);
        
        op.Return();

        if (result == ListProcessResult.Successful || result == ListProcessResult.DiscardOperation)
        {
            switch (serverMessage)
            {
                case NexusListInsertMessage:
                    Interlocked.Increment(ref _clientInsertEventCounter);
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

            return true;
        }
        op.Return();

        Logger?.LogError($"Processing failed. Returned result {result}");
        return false;
    }
}
