using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.Collections;

internal class NexusCollectionClient
{
    public readonly INexusDuplexPipe Pipe;
    //public readonly INexusChannelReader<INexusCollectionMessage>? Reader;
    public readonly INexusChannelWriter<INexusCollectionMessage>? Writer;
    public readonly INexusSession Session;
    public readonly Channel<NexusBroadcastMessageWrapper> MessageSender;

    public StateType State;
    
    public CancellationToken CompletionToken;
    

    public NexusCollectionClient(INexusDuplexPipe pipe, 
        INexusChannelWriter<INexusCollectionMessage>? writer,
        INexusSession session)
    {
        Pipe = pipe;
        State = StateType.Initializing;
        Writer = writer;
        Session = session;
        MessageSender = Channel.CreateUnbounded<NexusBroadcastMessageWrapper>(new  UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = true, 
        });
        
        var cts = new CancellationTokenSource();
        CompletionToken = cts.Token;
        
        Pipe.CompleteTask.ContinueWith(
            _ => cts.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    }
        
    public enum StateType
    {
        Unset,
        AcceptingUpdates,
        Initializing,
        Disconnected
    }
}
