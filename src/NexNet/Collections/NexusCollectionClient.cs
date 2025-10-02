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

    /// <summary>
    /// Sends the message over the wire to the client.
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True upon success, false otherwise.</returns>
    public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default);
    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper message);
    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper> BufferRead(CancellationToken ct = default);
}

internal class NexusCollectionClient : INexusCollectionClient
{
    public INexusDuplexPipe Pipe { get; }
    
    //public readonly INexusChannelReader<INexusCollectionMessage>? Reader;

    public readonly INexusSession Session;
    public INexusChannelWriter<INexusCollectionMessage> Writer { get; }
    public Channel<INexusCollectionBroadcasterMessageWrapper> MessageBuffer { get; }
    
    public CancellationToken CompletionToken { get; }
    public INexusLogger? Logger { get; }

    public long Id => Session.Id;

    public NexusCollectionClient(INexusDuplexPipe pipe, 
        INexusChannelWriter<INexusCollectionMessage> writer,
        INexusSession session)
    {
        
        Pipe = pipe;
        Logger = session.Logger?.CreateLogger($"COL{pipe.Id}");
        Writer = writer;
        Session = session;
        MessageBuffer = Channel.CreateUnbounded<INexusCollectionBroadcasterMessageWrapper>(new  UnboundedChannelOptions()
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
    
    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper message)
    {
        return MessageBuffer.Writer.TryWrite(message);
    }
    
    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper> BufferRead(CancellationToken ct = default)
    {
        return MessageBuffer.Reader.ReadAllAsync(ct);
    }    public ValueTask CompletePipe() => Pipe.CompleteAsync();

    public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default)
    {
        return Writer.WriteAsync(message, ct);
    }
    
    private class NexusCollectionBroadcasterMessageWrapper : INexusCollectionBroadcasterMessageWrapper
    {
        private int _completedCount;
        public int ClientCount { get; set; }
        public INexusCollectionClient? SourceClient { get; }
    
        /// <summary>
        /// Message for the source client. Usually includes as Ack.
        /// </summary>
        public INexusCollectionMessage? MessageToSource { get; }
        public INexusCollectionMessage Message { get; }

        public NexusCollectionBroadcasterMessageWrapper(INexusCollectionClient? sourceClient, INexusCollectionMessage message)
        {
            SourceClient = sourceClient;
            Message = message;
        
            if (sourceClient != null)
            {
                MessageToSource = message.Clone();
                MessageToSource.Flags |= NexusCollectionMessageFlags.Ack;
            }
        }

        public void SignalSent()
        {
            if (Interlocked.Increment(ref _completedCount) == ClientCount)
            {
                MessageToSource?.ReturnToCache();
                Message.ReturnToCache();
            }
        }
    }


}
