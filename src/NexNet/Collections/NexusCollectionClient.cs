using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Pipes;

namespace NexNet.Collections;

internal interface INexusCollectionClient
{
    public long Id { get; }
    
    public CancellationToken CompletionToken { get; }
    
    public INexusLogger? Logger { get; }

    public ValueTask CompletePipe();

    public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default);
    public bool BufferTryWrite(INexusBroadcastMessageWrapper message);
    public IAsyncEnumerable<INexusBroadcastMessageWrapper> BufferRead(CancellationToken ct = default);
}

internal class NexusCollectionClient : INexusCollectionClient
{
    public INexusDuplexPipe Pipe { get; }
    
    //public readonly INexusChannelReader<INexusCollectionMessage>? Reader;

    public readonly INexusSession Session;
    public INexusChannelWriter<INexusCollectionMessage> Writer { get; }
    public Channel<INexusBroadcastMessageWrapper> MessageBuffer { get; }

    public StateType State;
    
    public CancellationToken CompletionToken { get; }
    public INexusLogger? Logger { get; }

    public long Id => Session.Id;

    public ValueTask CompletePipe() => Pipe.CompleteAsync();

    public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default)
    {
        return Writer.WriteAsync(message, ct);
    }

    public bool BufferTryWrite(INexusBroadcastMessageWrapper message)
    {
        return MessageBuffer.Writer.TryWrite(message);
    }
    
    public IAsyncEnumerable<INexusBroadcastMessageWrapper> BufferRead(CancellationToken ct = default)
    {
        return MessageBuffer.Reader.ReadAllAsync(ct);
    }

    public NexusCollectionClient(INexusDuplexPipe pipe, 
        INexusChannelWriter<INexusCollectionMessage> writer,
        INexusSession session)
    {
        
        Pipe = pipe;
        State = StateType.Initializing;
        Logger = session.Logger?.CreateLogger($"COL{pipe.Id}");
        Writer = writer;
        Session = session;
        MessageBuffer = Channel.CreateUnbounded<INexusBroadcastMessageWrapper>(new  UnboundedChannelOptions()
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
