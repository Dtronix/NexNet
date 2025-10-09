using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

internal interface INexusBroadcastSession<TMessage>
    where TMessage : INexusCollectionUnion<TMessage>
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
    public ValueTask<bool> SendAsync(TMessage message, CancellationToken ct = default);
    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper<TMessage> message);
    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper<TMessage>> BufferRead(CancellationToken ct = default);
}

internal class NexusBroadcastSession<TMessage> : INexusBroadcastSession<TMessage>
    where TMessage : INexusCollectionUnion<TMessage>
{
    private readonly INexusChannelWriter<INexusCollectionUnion<TMessage>>? _writer;
    public INexusDuplexPipe Pipe { get; }

    public readonly INexusSession Session;
    public Channel<INexusCollectionBroadcasterMessageWrapper<TMessage>> MessageBuffer { get; }
    
    public CancellationToken CompletionToken { get; }
    public INexusLogger? Logger { get; }

    public long Id => Session.Id;

    public NexusBroadcastSession(INexusDuplexPipe pipe, 
        INexusChannelWriter<INexusCollectionUnion<TMessage>>? writer,
        INexusSession session)
    {
        
        Pipe = pipe;
        Logger = session.Logger?.CreateLogger($"COL{pipe.Id}");
        _writer = writer;
        Session = session;
        MessageBuffer = Channel.CreateUnbounded<INexusCollectionBroadcasterMessageWrapper<TMessage>>(new  UnboundedChannelOptions()
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
    
    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper<TMessage> message)
    {
        return MessageBuffer.Writer.TryWrite(message);
    }
    
    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper<TMessage>> BufferRead(CancellationToken ct = default)
    {
        return MessageBuffer.Reader.ReadAllAsync(ct);
    }    public ValueTask CompletePipe() => Pipe.CompleteAsync();

    public ValueTask<bool> SendAsync(TMessage message, CancellationToken ct = default)
    {
        if (_writer == null)
            throw new InvalidOperationException("Can't send when writer is not set.");
        return _writer.WriteAsync(message, ct);
    }
    
    private class NexusCollectionBroadcasterMessageWrapper : INexusCollectionBroadcasterMessageWrapper<TMessage>
    {
        private int _completedCount;
        public int ClientCount { get; set; }
        public INexusBroadcastSession<TMessage>? SourceClient { get; }
    
        /// <summary>
        /// Message for the source client. Usually includes as Ack.
        /// </summary>
        public TMessage? MessageToSource { get; }
        public TMessage Message { get; }

        public NexusCollectionBroadcasterMessageWrapper(INexusBroadcastSession<TMessage>? sourceClient, TMessage message)
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
                MessageToSource?.Return();
                Message.Return();
            }
        }
    }


}
