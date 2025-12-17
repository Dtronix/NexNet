using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Collections.Lists;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Manages connected clients and broadcasts messages to all active sessions.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be broadcast.</typeparam>
internal class NexusBroadcastConnectionManager<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly SnapshotList<INexusBroadcastSession<TUnion>> _connectedClients;
    private readonly INexusLogger? _logger;
    private readonly Channel<INexusCollectionBroadcasterMessageWrapper<TUnion>> _messageBroadcastChannel;
    
    private bool _isRunning;
    
    internal bool DoNotSendAck = false; 
    public NexusBroadcastConnectionManager(INexusLogger? logger)
    {
        _logger = logger?.CreateLogger("Broadcast");
        _connectedClients = new SnapshotList<INexusBroadcastSession<TUnion>>(64);
        _messageBroadcastChannel = Channel.CreateBounded<INexusCollectionBroadcasterMessageWrapper<TUnion>>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsRunning => _isRunning;

    public void AddClientAsync(INexusBroadcastSession<TUnion> client)
    {
        if(!_isRunning)
            throw new InvalidOperationException("Broadcaster is not running.");
        
        // Add in the completion removal for execution later.
        _logger?.LogTrace($"S{client.Id} Starting collection message writer.");
        
        // Start the internal broadcast listener for this client to handle sending updates to the client.
        Task.Factory.StartNew(static async state =>
        {
            var client = (INexusBroadcastSession<TUnion>)(state!);
            INexusCollectionBroadcasterMessageWrapper<TUnion>? wrapper = null;
            await foreach (var messageWrapper in client.BufferRead(client.CompletionToken).ConfigureAwait(false))
            {
                try
                {
                    wrapper = messageWrapper;
                    var message = messageWrapper.SourceClient == client
                        ? messageWrapper.MessageToSource ?? throw new Exception("Message to source client is null.")
                        : messageWrapper.Message;

                    if (message == null)
                        throw new Exception("Message is null.");
                    
                    client.Logger?.LogTrace($"Sending {message.GetType().Name} to client.");

                    if (!await client.SendAsync(message, client.CompletionToken).ConfigureAwait(false))
                    {
                        client.Logger?.LogInfo("Cound not send client message.");
                        await client.CompletePipe().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception e)
                {
                    client.Logger?.LogInfo(e, "Could not send collection broadcast message to session.");
                    // Ignore and disconnect.
                    try
                    {
                        await client.CompletePipe().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        client.Logger?.LogInfo(ex, "Exception while completing pipe.");
                    }

                    return;

                }
                finally
                {
                    wrapper?.SignalSent();
                }
            }

        }, client, TaskCreationOptions.DenyChildAttach);
        
        _connectedClients.Add(client);
    }

    public async ValueTask BroadcastAsync(TUnion message, INexusBroadcastSession<TUnion>? sourceClient, CancellationToken ct = default)
    {
        // DoNotSendAck is for testing logic only and not used in any production.
        // Using WriteAsync provides backpressure - if the channel is full, we wait instead of dropping messages.
        await _messageBroadcastChannel.Writer.WriteAsync(message.Wrap(DoNotSendAck ? null : sourceClient), ct).ConfigureAwait(false);
    }

    public void Run(CancellationToken token)
    {
        if(_isRunning)
            throw new InvalidOperationException("Broadcaster is already running.");
        
        _isRunning = true;
        
        Task.Factory.StartNew(async static args =>
        {
            var (broadcaster, ct) = ((NexusBroadcastConnectionManager<TUnion>, CancellationToken))args!;
            
            broadcaster._logger?.LogTrace("Started broadcast loop.");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var broadcastMessage in broadcaster._messageBroadcastChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        // Update the count.
                        broadcastMessage.ClientCount = broadcaster._connectedClients.Count;
                        
                        // Ensure we actually have any clients.
                        if (broadcastMessage.ClientCount == 0)
                        {
                            // Force the client count to 1 and signal a return. 
                            broadcastMessage.ClientCount = 1;
                            broadcastMessage.SignalSent();
                            broadcaster._logger?.LogTrace($"Broadcasting skipping {broadcastMessage.Message?.GetType()} due to no client connections.");
                            continue;
                        }
                        
                        broadcaster._logger?.LogTrace($"Broadcasting {broadcastMessage.Message?.GetType()}");

                        foreach (var client in broadcaster._connectedClients)
                        {
                            try
                            {
                                if (!client.BufferTryWrite(broadcastMessage))
                                {
                                    broadcaster._logger?.LogTrace(
                                        $"S{client.Id} Could not send to client collection");
                                    // Complete the pipe as it is full and not writing to the client at a decent
                                    // rate.
                                    await client.CompletePipe().ConfigureAwait(false);
                                    // Signal completion for this client since message won't be processed
                                    broadcastMessage.SignalSent();
                                }
                                else
                                {
                                    broadcaster._logger?.LogTrace(
                                        $"S{client.Id} Sent {broadcastMessage.Message?.GetType()} to client collection");
                                }
                            }
                            catch (Exception e)
                            {
                                broadcaster._logger?.LogInfo(e,
                                    $"S{client.Id} Exception while sending to collection. Removing from broadcast.");
                                
                                // If we threw, the client is disconnected. Remove the client and signal completion.
                                broadcaster._connectedClients.Remove(client);
                                broadcastMessage.SignalSent();
                            }

                        }
                    }
                    broadcaster._logger?.LogDebug("Exited broadcast reading loop.");
                }
                catch (Exception e)
                {
                    broadcaster._logger?.LogError(e, "Exception in broadcast loop");
                }
            }
            
            broadcaster._isRunning = false;

            // Close the client connections.
            foreach (var client in broadcaster._connectedClients)
            {
                try
                {
                    await client.CompletePipe().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    broadcaster._logger?.LogWarning(e, $"S{client.Id} Could not complete pipe.");
                }
            }
            
            broadcaster._connectedClients.Clear();
            
        }, (this, token), TaskCreationOptions.DenyChildAttach);
    }
}

/// <summary>
/// Wraps a broadcast message with metadata for tracking delivery to multiple clients.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be wrapped.</typeparam>
internal class NexusCollectionBroadcasterMessageWrapper<TUnion> : INexusCollectionBroadcasterMessageWrapper<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{    
    private static readonly ConcurrentBag<NexusCollectionBroadcasterMessageWrapper<TUnion>> _pool = new ();
    private int _completedCount;
    public int ClientCount { get; set; }
    public INexusBroadcastSession<TUnion>? SourceClient { get; set; }

    /// <summary>
    /// Message for the source client. Usually includes as Ack.
    /// </summary>
    public TUnion? MessageToSource { get; private set; }

    public TUnion? Message { get; private set; }

    private NexusCollectionBroadcasterMessageWrapper()
    {
        
    }

    public void SignalSent()
    {
        if (Interlocked.Increment(ref _completedCount) != ClientCount)
            return;

        MessageToSource?.Return();
        MessageToSource = default;
        
        Message!.Return();
        Message = default;
        
        _pool.Add(this);
    }

    public static INexusCollectionBroadcasterMessageWrapper<TUnion> Rent<TMessage>(
        TMessage message,
        INexusBroadcastSession<TUnion>? sourceClient) 
        where TMessage : TUnion, INexusCollectionUnion<TUnion>, new()
    {
        if (!_pool.TryTake(out var wrapper))
            wrapper = new NexusCollectionBroadcasterMessageWrapper<TUnion>();

        wrapper.Message = message;
        wrapper.SourceClient = sourceClient;
        wrapper._completedCount = 0;
        wrapper.ClientCount = 1;
        if (sourceClient != null)
        {
            wrapper.MessageToSource = message.Clone();
            wrapper.MessageToSource.Flags |= NexusCollectionMessageFlags.Ack;
        }
        
        return wrapper;
    }
}

/// <summary>
/// Interface for wrapping broadcast messages with delivery tracking metadata.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types.</typeparam>
internal interface INexusCollectionBroadcasterMessageWrapper<TUnion>
    where TUnion : INexusCollectionUnion<TUnion>
{
    /// <summary>
    /// Gets the client that originated the message, or null for server-originated messages.
    /// </summary>
    INexusBroadcastSession<TUnion>? SourceClient { get; }

    /// <summary>
    /// Gets the acknowledgment message sent to the source client, or null if no acknowledgment is needed.
    /// </summary>
    TUnion? MessageToSource { get; }

    /// <summary>
    /// Gets the message to broadcast to all clients.
    /// </summary>
    TUnion? Message { get; }

    /// <summary>
    /// Gets or sets the number of clients that will receive this message.
    /// </summary>
    public int ClientCount { get; set; }

    /// <summary>
    /// Signals that the message was sent to one client. Releases resources when all clients have received it.
    /// </summary>
    void SignalSent();
}

