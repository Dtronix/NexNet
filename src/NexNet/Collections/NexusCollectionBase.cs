using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections;

internal abstract class NexusCollectionBroadcaster : INexusCollectionConnector
{
    private NexusCollectionState _state;
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly bool IsServer;
    private readonly SnapshotList<Client>? _connectedClients;
    private CancellationTokenSource? _broadcastCancellation;
    protected readonly INexusLogger? Logger;
    private readonly Channel<INexusCollectionMessage> _messageBroadcastChannel;

    protected NexusCollectionBroadcaster(ushort id, NexusCollectionMode mode, INexusLogger? logger, bool isServer)
    {
        Id = id;
        Mode = mode;
        IsServer = isServer;
        Logger = logger?.CreateLogger($"Coll{id}");
        _connectedClients = new SnapshotList<Client>(64);
        _messageBroadcastChannel = Channel.CreateBounded<INexusCollectionMessage>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void StartUpdateBroadcast()
    {
        if(_broadcastCancellation != null)
            throw new InvalidOperationException("Broadcast has been already started");
        
        _broadcastCancellation = new CancellationTokenSource();

        _ = Task.Factory.StartNew(async static state =>
        {
            var collection = Unsafe.As<NexusCollectionBroadcaster>(state)!;

            collection.Logger?.LogTrace("Started broadcast reading loop.");
            
            while (collection._broadcastCancellation?.IsCancellationRequested == false)
            {
                try
                {
                    foreach (var client in collection._connectedClients)
                    {

                        await foreach (var message in collection._messageBroadcastChannel.Reader
                                           .ReadAllAsync(collection._broadcastCancellation!.Token)
                                           .ConfigureAwait(false))
                        {
                            // If the client is not accepting updates, then ignore the update and allow
                            // for future poling for updates.  This happens when a client is initializing
                            // all the items.
                            if (client.State == Client.StateType.AcceptingUpdates)
                            {
                                try
                                {
                                    if (!client.MessageSender.Writer.TryWrite(message))
                                    {
                                        collection.Logger?.LogTrace(
                                            $"S{client.Session.Id} Could not send to client collection");
                                        // Complete the pipe as it is full and not writing to the client at a decent
                                        // rate.
                                        await client.Pipe.CompleteAsync().ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        collection.Logger?.LogTrace($"S{client.Session.Id} Sent to client collection");
                                    }
                                }
                                catch (Exception e)
                                {
                                    collection.Logger?.LogTrace(e, "Exception while sending to collection");
                                    // If we threw, the client is disconnected.  Remove the client.
                                    collection._connectedClients.Remove(client);
                                }

                            }
                            else
                            {
                                client.RequireReset = true;
                                collection.Logger?.LogTrace(
                                    $"S{client.Session.Id} Client is not accepting updates");
                            }
                        }

                        // If this is the originating client, notify that the process has been completed.
                        if (result.Ack && req.Client == client)
                        {
                            // Make a clone of the response to send the ACK flag back to the sending client
                            // unless the sending client is the only client being notified.
                            var clientResponse = respondingToClientOnly
                                ? result.Message
                                : result.Message.Clone();
                            clientResponse.Remaining = 1;
                            clientResponse.Flags |= NexusCollectionMessageFlags.Ack;
                            if (!client.MessageSender.Writer.TryWrite(clientResponse))
                            {
                                collection.Logger?.LogTrace(
                                    $"S{client.Session.Id} ACK Client Could not send to client collection");
                                // Complete the pipe as it is full and not writing to the client at a decent
                                // rate.
                                await client.Pipe.CompleteAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                //collection.Logger?.LogTrace("ACK Client Sent to client collection");
                            }
                        }
                        else
                        {

                        }

                    }

    
                    await foreach (var req in collection._messageBroadcastChannel!.Reader
                                       .ReadAllAsync(collection._broadcastCancellation!.Token)
                                       .ConfigureAwait(false))
                    {
                        if(req.Message is INexusCollectionValueMessage valueMessage)
                            valueMessage.ReturnValueToPool();
                        
                        if (result.Disconnect)
                        {
                            req.Tcs?.TrySetResult(false);
                            if (req.Client != null)
                            {
                                req.Client.Session.Logger?.LogWarning(
                                    "Disconnected from collection due to protocol error.");
                                await req.Client.Session.DisconnectAsync(DisconnectReason.ProtocolError)
                                    .ConfigureAwait(false);
                            }

                            continue;
                        }
                        
                        // If the message is null, then the message is a noop.
                        if (result.Message != null)
                        {
                            // Set the total clients that should need to broadcast prior to returning.
                            result.Message.Remaining = collection._connectedClients!.Count - 1;
                            
                            // If there is 0 remaining and the client is set, then we are only responding to the client that
                            // sent the change to begin with.
                            var respondingToClientOnly = result.Message.Remaining <= 0 && req.Client != null;
                            
                            // For testing only
                            if (!collection.DoNotSendAck)
                            {
                                
                            }
                        }
                        else
                        {
                            collection.Logger?.LogTrace($"Translated into noop message");
                        }

                        req.Tcs?.TrySetResult(true);
                    }
                    
                    collection.Logger?.LogDebug("Stopped broadcast reading loop.");
                }
                catch (Exception e)
                {
                    collection.Logger?.LogError(e, "Exception in UpdateBroadcast loop");
                }
            }
            
            return default;
            
        }, this, TaskCreationOptions.DenyChildAttach);

        // The server is always connected.
        _state = NexusCollectionState.Connected;
    }

    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!IsServer)
            throw new InvalidOperationException("List is not setup in Server mode.");

        await pipe.ReadyTask.ConfigureAwait(false);

        // Check to see if the pipe was completed after the ready task.
        if (pipe.CompleteTask.IsCompleted)
            return;

        var writer = new NexusChannelWriter<INexusCollectionMessage>(pipe);
        var reader = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelReader<INexusCollectionMessage>(pipe) : null;
        
        var client = new Client(
            pipe,
            reader,
            writer,
            session) { State = Client.StateType.Initializing };
        _connectedClients!.Add(client);
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (_, state )=>
        {
            var (client, list) = ((Client,  SnapshotList<Client>))state!;
            list.Remove(client);
        }, (client, _connectedClients), TaskContinuationOptions.RunContinuationsAsynchronously);

        
        Logger?.LogTrace($"S{client.Session.Id} Starting channel listener.");
        
        // Start the listener on the channel to handle sending updates to the client.
        _ = Task.Factory.StartNew(static async state =>
        {
            var client = Unsafe.As<Client>(state!);

            try
            {
                await foreach (var message in client.MessageSender.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await client.Writer!.WriteAsync(message).ConfigureAwait(false);
                    message.CompleteBroadcast();
                }
            }
            catch (Exception e)
            {
                client.Session.Logger?.LogInfo(e, $"S{client.Session.Id} Could not send collection broadcast message to session.");
                // Ignore and disconnect.
            }

        }, client, TaskCreationOptions.DenyChildAttach);
        
        Logger?.LogTrace($"S{client.Session.Id} Sending client init data");
        // Initialize the client's data.
        
        var resetCount = 0;
        while(true)
        {
            var initResult = await SendClientInitData(client).ConfigureAwait(false);
            var requiresReset = client.RequireReset;
            client.RequireReset = false;
            
            if (!initResult)
                return;
            
            // If we don't require a reset, we can exit this loop.
            if (!requiresReset && !client.RequireReset)
                break;
            
            if (2 > ++resetCount)
            {
                Logger?.LogWarning($"S{client.Session.Id} Client reset attempt limit hit. Disconnecting");
                return;
            }
            
            Logger?.LogWarning($"S{client.Session.Id} Collection was updated during a reset attempt {resetCount}");
        }

        // Ensure all initial data is fully flushed.
        await writer.Writer.FlushAsync().ConfigureAwait(false);
        
        // If the reader is not null, that means we have a bidirectional collection.
        if (reader != null)
        {
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    //Logger?.LogTrace($"Received message. {message.GetType()}");
                    await _processChannel!.Writer.WriteAsync(new ProcessRequest(client, message, null)).ConfigureAwait(false);

                }
            }
            catch (Exception e)
            {
                client.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
                // Ignore and disconnect.
            }

        }

        await pipe.CompleteTask.ConfigureAwait(false);
    }
    
    private async Task<bool> WriteMessageToProcessChannel(INexusCollectionMessage message)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _processChannel!.Writer.WriteAsync(new ProcessRequest(null, message, tcs)).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }
    
    protected async Task<bool> UpdateAndWaitAsync(INexusCollectionMessage message)
    {
        if (_state != NexusCollectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot perform operations when collection is read-only");

        //Logger?.LogTrace($"--> Sending {message.GetType()} message.");
        if (IsServer)
        {
            return await WriteMessageToProcessChannel(message).ConfigureAwait(false);
        }
        else
        {
            //TODO: Review using PooledValueTask; https://mgravell.github.io/PooledAwait/
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ackTcs = tcs;
            var result = await _client!.Writer!.WriteAsync(message).ConfigureAwait(false);

            // Since the message is only used by one writer, return it directly to the cache.
            message.ReturnToCache();

            if (result)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }
    
    protected abstract IEnumerable<INexusCollectionMessage> ResetValuesEnumerator(
        NexusCollectionResetValuesMessage message);
    
    private async ValueTask<bool> SendClientInitData(Client client)
    {
        try
        {
            var writer = client.Writer!;
            var resetComplete = NexusCollectionResetCompleteMessage.Rent();
            var batchValue = NexusCollectionResetValuesMessage.Rent();
            bool sentData = false;

            foreach (var values in ResetValuesEnumerator(batchValue))
            {
                sentData = true;
                await writer.WriteAsync(values).ConfigureAwait(false);
            }
            
            batchValue.ReturnToCache();
            
            if (!sentData)
            {
                resetComplete.ReturnToCache();
                Logger?.LogError("No reset start reset message was sent during reset.");
                return false;
            }
            
            // Set the state to accepting since we have now received all the data needed.
            client.State = Client.StateType.AcceptingUpdates;
            
            await writer.WriteAsync(resetComplete).ConfigureAwait(false);
            resetComplete.ReturnToCache();
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Could not send collection initialization data.");
            return false;
        }
        
        return true;
    }

    public async Task DisconnectAsync()
    {
        // Disconnect on the server is a noop.
        if(IsServer 
           || Interlocked.Exchange(ref _state, NexusCollectionState.Disconnected) == NexusCollectionState.Disconnected)
            return;
        
        var client = _client;
        if (client == null)
            return;

        await client.Pipe.CompleteAsync().ConfigureAwait(false);

        if (_disconnectTcs != null)
        {
            await _disconnectTcs.Task.ConfigureAwait(false);
            _disconnectTcs = null;
        }
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    protected void EnsureAllowedModificationState()
    {
        if (IsServer)
            return;

        if (_client?.State != Client.StateType.AcceptingUpdates)
            throw new InvalidOperationException(
                "Cannot perform operations when collection is disconnected.");
    }

    protected void ClientDisconnected()
    {
        _state = NexusCollectionState.Disconnected;
        _ackTcs?.TrySetResult(false);
        
        // Reset the ready task
        if (_tcsReady.Task.Status != TaskStatus.WaitingForActivation)
            _tcsReady = new TaskCompletionSource();

        try
        {
            OnClientDisconnected();
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while disconnecting client.");
        }
        
        CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
        
        _disconnectTcs?.TrySetResult();
    }
    
    
    private readonly struct SemaphoreSlimDisposable(SemaphoreSlim semaphore) : IDisposable
    { 
        public void Dispose()
        {
            semaphore.Release();
        }
    }
    

    private class Client
    {
        public readonly INexusDuplexPipe Pipe;
        public readonly INexusChannelReader<INexusCollectionMessage>? Reader;
        public readonly INexusChannelWriter<INexusCollectionMessage>? Writer;
        public readonly INexusSession Session;
        public readonly Channel<INexusCollectionMessage> MessageSender;
        
        /// <summary>
        /// If this has been set to true, the collection has been sent updated while initializing
        /// and those updates can't be guaranteed to be included in the current initialization.
        /// So a full reset is required.
        /// </summary>
        public bool RequireReset;

        public StateType State;

        public Client(INexusDuplexPipe pipe, 
            INexusChannelReader<INexusCollectionMessage>? reader,
            INexusChannelWriter<INexusCollectionMessage>? writer,
            INexusSession session)
        {
            Pipe = pipe;
            Reader = reader;
            Writer = writer;
            Session = session;
            MessageSender = Channel.CreateUnbounded<INexusCollectionMessage>(new  UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = true, 
            });
        }
        
        public enum StateType
        {
            Unset,
            AcceptingUpdates,
            Initializing,
            Disconnected
        }
    }
}
