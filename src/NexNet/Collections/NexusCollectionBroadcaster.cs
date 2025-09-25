using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Internals.Collections.Lists;
using NexNet.Logging;

namespace NexNet.Collections;


internal class NexusCollectionBroadcaster
{
    private readonly SnapshotList<INexusCollectionClient> _connectedClients;
    private readonly INexusLogger? _logger;
    private readonly Channel<NexusBroadcastMessageWrapper> _messageBroadcastChannel;
    
    private bool _isRunning;
    public NexusCollectionBroadcaster(INexusLogger? logger)
    {
        _logger = logger?.CreateLogger("Broadcast");
        _connectedClients = new SnapshotList<INexusCollectionClient>(64);
        _messageBroadcastChannel = Channel.CreateBounded<NexusBroadcastMessageWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsRunning => _isRunning;

    public void AddClientAsync(INexusCollectionClient client)
    {
        if(!IsRunning)
            throw new InvalidOperationException("Broadcaster is not running.");
        
        // Add in the completion removal for execution later.
        _logger?.LogTrace($"S{client.Id} Starting collection message writer.");
        
        // Start the listener on the channel to handle sending updates to the client.
        Task.Factory.StartNew(static async state =>
        {
            var client = (INexusCollectionClient)(state!);
            NexusBroadcastMessageWrapper? wrapper = null;
            try
            {
                await foreach (NexusBroadcastMessageWrapper messageWrapper in client.MessageBuffer.Reader.ReadAllAsync(client.CompletionToken).ConfigureAwait(false))
                {
                    wrapper = messageWrapper;
                    var message = messageWrapper.SourceClient == client
                        ? messageWrapper.SourceMessage ?? throw new Exception("Message to source client is null.")
                        : messageWrapper.Message;

                    await client.WriteAsync(message, client.CompletionToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                client.Logger?.LogInfo(e,"Could not send collection broadcast message to session.");
                // Ignore and disconnect.
            }
            finally
            {
                wrapper?.SignalCompletion();
            }

        }, client, TaskCreationOptions.DenyChildAttach);
        
        _connectedClients.Add(client);
    }

    public ValueTask BroadcastAsync(INexusCollectionMessage message, INexusCollectionClient? sourceClient)
    {
        var broadcastMessage = new NexusBroadcastMessageWrapper(sourceClient, message);
        return _messageBroadcastChannel.Writer.WriteAsync(broadcastMessage);
    }

    public void Run(CancellationToken token)
    {
        if(IsRunning)
            throw new InvalidOperationException("Broadcaster is already running.");
        
        Task.Factory.StartNew(async static args =>
        {
            var (broadcaster, ct) = ((NexusCollectionBroadcaster, CancellationToken))args!;
            
            broadcaster._logger?.LogTrace("Started broadcast loop.");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var broadcastMessage in broadcaster._messageBroadcastChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        // Update the count.
                        broadcastMessage.ClientCount = broadcaster._connectedClients.Count;
                        foreach (var client in broadcaster._connectedClients)
                        {
                            try
                            {
                                if (!client.MessageBuffer.Writer.TryWrite(broadcastMessage))
                                {
                                    broadcaster._logger?.LogTrace(
                                        $"S{client.Id} Could not send to client collection");
                                    // Complete the pipe as it is full and not writing to the client at a decent
                                    // rate.
                                    await client.CompletePipe().ConfigureAwait(false);
                                    // Signal completion for this client since message won't be processed
                                    broadcastMessage.SignalCompletion();
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
                                broadcastMessage.SignalCompletion();
                            }

                        }
                    }
                    broadcaster._logger?.LogDebug("Stopped broadcast reading loop.");
                }
                catch (Exception e)
                {
                    broadcaster._logger?.LogError(e, "Exception in boradcast loop");
                }
            }
        }, (this, token), TaskCreationOptions.DenyChildAttach);

        _isRunning = true;
    }
}



internal class NexusBroadcastMessageWrapper
{
    private int _completedCount = 0;
    public int ClientCount;
    public INexusCollectionClient? SourceClient { get; }
    
    /// <summary>
    /// Message for the source client. Usually includes as Ack.
    /// </summary>
    public INexusCollectionMessage? SourceMessage { get; }
    public INexusCollectionMessage Message { get; }

    public NexusBroadcastMessageWrapper(INexusCollectionClient? sourceClient, INexusCollectionMessage message)
    {
        SourceClient = sourceClient;
        Message = message;
        
        if (sourceClient != null)
        {
            SourceMessage = message.Clone();
            SourceMessage.Flags |= NexusCollectionMessageFlags.Ack;
        }
    }

    public void SignalCompletion()
    {
        if (Interlocked.Increment(ref _completedCount) == ClientCount)
        {
            SourceMessage?.ReturnToCache();
            Message.ReturnToCache();
        }
    }
}
