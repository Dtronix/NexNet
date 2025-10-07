using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Collections.Lists;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;


internal class NexusBroadcastConnectionManager
{
    private readonly SnapshotList<INexusBroadcastSession> _connectedClients;
    private readonly INexusLogger? _logger;
    private readonly Channel<INexusCollectionBroadcasterMessageWrapper> _messageBroadcastChannel;
    
    private bool _isRunning;
    
    internal bool DoNotSendAck = false; 
    public NexusBroadcastConnectionManager(INexusLogger? logger)
    {
        _logger = logger?.CreateLogger("Broadcast");
        _connectedClients = new SnapshotList<INexusBroadcastSession>(64);
        _messageBroadcastChannel = Channel.CreateBounded<INexusCollectionBroadcasterMessageWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsRunning => _isRunning;

    public void AddClientAsync(INexusBroadcastSession client)
    {
        if(!_isRunning)
            throw new InvalidOperationException("Broadcaster is not running.");
        
        // Add in the completion removal for execution later.
        _logger?.LogTrace($"S{client.Id} Starting collection message writer.");
        
        // Start the client reader for handling of messages the client sends.
        Task.Factory.StartNew(static async state =>
        {
            var client = (INexusBroadcastSession)(state!);
            INexusCollectionBroadcasterMessageWrapper? wrapper = null;
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
        
        // Start the internal broadcast listener for this client to handle sending updates to the client.
        Task.Factory.StartNew(static async state =>
        {
            var client = (INexusBroadcastSession)(state!);
            INexusCollectionBroadcasterMessageWrapper? wrapper = null;
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

    public void BroadcastAsync(INexusCollectionMessage message, INexusBroadcastSession? sourceClient)
    {
        // DoNotSendAck is for testing logic only and not used in any production.
        if (!_messageBroadcastChannel.Writer.TryWrite(message.Wrap(DoNotSendAck ? null : sourceClient)))
        {
            _logger?.LogCritical("Could not write to Broadcast channel.");
        }
    }

    public void Run(CancellationToken token)
    {
        if(_isRunning)
            throw new InvalidOperationException("Broadcaster is already running.");
        
        _isRunning = true;
        
        Task.Factory.StartNew(async static args =>
        {
            var (broadcaster, ct) = ((NexusBroadcastConnectionManager, CancellationToken))args!;
            
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
                            continue;
                        }

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
                                        $"S{client.Id} Sent to client collection");
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

internal class NexusCollectionBroadcasterMessageWrapper : INexusCollectionBroadcasterMessageWrapper
{    
    private static readonly ConcurrentBag<NexusCollectionBroadcasterMessageWrapper> _pool = new ();
    private int _completedCount;
    public int ClientCount { get; set; }
    public INexusBroadcastSession? SourceClient { get; set; }

    /// <summary>
    /// Message for the source client. Usually includes as Ack.
    /// </summary>
    public INexusCollectionMessage? MessageToSource { get; private set; }

    public INexusCollectionMessage? Message { get; private set; }

    private NexusCollectionBroadcasterMessageWrapper()
    {
        
    }

    public static NexusCollectionBroadcasterMessageWrapper Rent(INexusCollectionMessage message, INexusBroadcastSession? sourceClient)
    {
        if (!_pool.TryTake(out var wrapper))
            wrapper = new NexusCollectionBroadcasterMessageWrapper();

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

    public void SignalSent()
    {
        if (Interlocked.Increment(ref _completedCount) != ClientCount)
            return;

        MessageToSource?.Return();
        MessageToSource = null;
        
        Message!.Return();
        Message = null;
        
        _pool.Add(this);
    }
}

internal interface INexusCollectionBroadcasterMessageWrapper
{
    INexusBroadcastSession? SourceClient { get; }

    /// <summary>
    /// Message for the source client. Usually includes as Ack.
    /// </summary>
    INexusCollectionMessage? MessageToSource { get; }

    INexusCollectionMessage? Message { get; }
    
    public int ClientCount { get; set; }
    void SignalSent();
}

