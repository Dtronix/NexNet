using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Interface representing a broadcast session for bidirectional communication with a client.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be sent.</typeparam>
internal interface INexusBroadcastSession<TUnion>
    where TUnion : INexusCollectionUnion<TUnion>
{
    /// <summary>
    /// Gets the unique session identifier.
    /// </summary>
    public long Id { get; }

    /// <summary>
    /// Gets the cancellation token that is canceled when the session completes.
    /// </summary>
    public CancellationToken CompletionToken { get; }

    /// <summary>
    /// Gets the logger for this session.
    /// </summary>
    public INexusLogger? Logger { get; }

    /// <summary>
    /// Completes the underlying pipe, ending the session.
    /// </summary>
    /// <returns>A task that completes when the pipe is closed.</returns>
    public ValueTask CompletePipe();

    /// <summary>
    /// Sends a message over the wire to the client.
    /// </summary>
    /// <param name="message">Message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True upon success, false otherwise.</returns>
    public ValueTask<bool> SendAsync(TUnion message, CancellationToken ct = default);

    /// <summary>
    /// Attempts to write a message to the session's message buffer.
    /// </summary>
    /// <param name="message">The message wrapper to write.</param>
    /// <returns>True if the message was written; false if the buffer is full.</returns>
    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper<TUnion> message);

    /// <summary>
    /// Reads messages from the session's message buffer asynchronously.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of message wrappers.</returns>
    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper<TUnion>> BufferRead(CancellationToken ct = default);
}

/// <summary>
/// Represents a broadcast session managing the connection and message flow between server and client.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be broadcast.</typeparam>
internal class NexusBroadcastSession<TUnion> : INexusBroadcastSession<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly INexusChannelWriter<TUnion>? _writer;
    public INexusDuplexPipe Pipe { get; }

    public readonly INexusSession Session;
    public Channel<INexusCollectionBroadcasterMessageWrapper<TUnion>> MessageBuffer { get; }
    
    public CancellationToken CompletionToken { get; }
    public INexusLogger? Logger { get; }

    public long Id => Session.Id;

    public NexusBroadcastSession(INexusDuplexPipe pipe, 
        INexusChannelWriter<TUnion>? writer,
        INexusSession session)
    {
        
        Pipe = pipe;
        Logger = session.Logger?.CreateLogger($"COL{pipe.Id}");
        _writer = writer;
        Session = session;
        // Use bounded channel to prevent memory exhaustion from slow consumers
        MessageBuffer = Channel.CreateBounded<INexusCollectionBroadcasterMessageWrapper<TUnion>>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                SingleWriter = false, // May have multiple writers in broadcast scenarios
                FullMode = BoundedChannelFullMode.Wait
            });
        
        var cts = new CancellationTokenSource();
        CompletionToken = cts.Token;
        
        Pipe.CompleteTask.ContinueWith(
            _ => cts.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    }

    public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper<TUnion> message)
    {
        return MessageBuffer.Writer.TryWrite(message);
    }

    public IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper<TUnion>> BufferRead(CancellationToken ct = default)
    {
        return MessageBuffer.Reader.ReadAllAsync(ct);
    }    public ValueTask CompletePipe() => Pipe.CompleteAsync();

    public ValueTask<bool> SendAsync(TUnion message, CancellationToken ct = default)
    {
        if (_writer == null)
            throw new InvalidOperationException("Can't send when writer is not set.");
        return _writer.WriteAsync(message, ct);
    }
    
    private class NexusCollectionBroadcasterMessageWrapper : INexusCollectionBroadcasterMessageWrapper<TUnion>
    {
        private int _completedCount;
        public int ClientCount { get; set; }
        public INexusBroadcastSession<TUnion>? SourceClient { get; }
    
        /// <summary>
        /// Message for the source client. Usually includes as Ack.
        /// </summary>
        public TUnion? MessageToSource { get; }
        public TUnion Message { get; }

        public NexusCollectionBroadcasterMessageWrapper(INexusBroadcastSession<TUnion>? sourceClient, TUnion message)
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
