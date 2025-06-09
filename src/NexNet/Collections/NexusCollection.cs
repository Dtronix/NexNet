using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections;

internal abstract class NexusCollection : INexusCollectionConnector
{
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    private readonly ConfigBase _config;
    

    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;

    private static int _ackId = 0; 
    
    private readonly SnapshotList<Client>? _nexusPipeList;
    
    private CancellationTokenSource? _broadcastCancellation;
    private Channel<ProcessRequest> _processChannel;
    private ConcurrentDictionary<int, TaskCompletionSource<bool>>? _ackTcs;
    protected bool IsClientResetting { get; private set; }

    private record struct ProcessRequest(Client? Client, INexusCollectionMessage Message, TaskCompletionSource<bool>? Tcs);
    
    public bool IsReadOnly => !IsServer && Mode != NexusCollectionMode.BiDrirectional;
    
    protected NexusCollection(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
    {
        
        Id = id;
        Mode = mode;
        _config = config;
        IsServer = isServer;
        
        if (isServer)
        {
            _nexusPipeList = new SnapshotList<Client>(64);
            
        }
        else
        {
            _ackTcs = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
        }
    }

    protected record struct ProcessServerOperationResult(INexusCollectionMessage? Message, bool Disconnect, bool Ack);
    private ProcessServerOperationResult ProcessServerOperation(INexusCollectionMessage clientMessage)
    {
        switch (clientMessage)
        {

        }
    }
    
    protected abstract bool OnProcessClientMessage(INexusCollectionMessage message);
    
    private bool ProcessClientOperation(INexusCollectionMessage serverMessage)
    {
        switch (serverMessage)
        {
            // No broadcasting on the reset operations as they are only client operations.
            case NexusCollectionResetStartMessage:
                IsClientResetting = true;
                break;
            
            case NexusCollectionResetValuesMessage message:
                if (!IsClientResetting)
                    return false;
                break;

            case NexusCollectionResetCompleteMessage:
                if (!IsClientResetting)
                    return false;

                IsClientResetting = false;
                break;
            
            case NexusCollectionAckMessage ackOperation:
                _ackTcs.
        }
        
        return OnProcessClientMessage(serverMessage);
    }


    public void StartUpdateBroadcast()
    {
        if(_broadcastCancellation != null)
            throw new InvalidOperationException("Broadcast has been already started");
        
        _broadcastCancellation = new CancellationTokenSource();
        _processChannel = Channel.CreateBounded<ProcessRequest>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _ = Task.Factory.StartNew(async static state =>
        {
            var collection = Unsafe.As<NexusCollection>(state)!;

            while (true)
            {
                try
                {
                    collection._config.Logger?.LogDebug("Started broadcast reading loop.");
                    await foreach (var req in collection._processChannel.Reader
                                       .ReadAllAsync(collection._broadcastCancellation!.Token)
                                       .ConfigureAwait(false))
                    {
                        var (message, disconnect, ackMessage) = collection.ProcessOperation(req.Message);
                        
                        if(req.Message is INexusCollectionValueMessage valueMessage)
                            valueMessage.ReturnValueToPool();

                        // If the result is false, close the whole session.
                        if (disconnect)
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
                        if (message != null)
                        {
                            // Set the total clients that should need to broadcast prior to returning.
                            message.Remaining = collection._nexusPipeList.Count;
                            foreach (var client in collection._nexusPipeList)
                            {
                                try
                                {
                                    // If the client is not accepting updates, then ignore the update and allow
                                    // for future poling for updates.  This happens when a client is initializing
                                    // all the items.
                                    if (client.State == Client.StateType.AcceptingUpdates)
                                    {
                                        if (!client.MessageSender.Writer.TryWrite(message))
                                        {
                                            // Complete the pipe as it is full and not writing to the client at a decent
                                            // rate.
                                            await client.Pipe.CompleteAsync().ConfigureAwait(false);
                                        }
                                    }
                                }
                                catch
                                {
                                    // If we threw, the client is disconnected.  Remove the client.
                                    collection._nexusPipeList.Remove(client);
                                }
                            }
                        }

                        if (req.Tcs != null)
                        {
                            req.Tcs?.TrySetResult(true);
                            ackMessage?.ReturnToCache();
                        }
                        
                        // Notify the sending client that the operation was complete.
                        if (ackMessage != null && req.Client != null && !req.Client.MessageSender.Writer.TryWrite(ackMessage))
                        {
                            req.Client.Session.Logger?.LogWarning("Could not write to client message processor.");
                            await req.Client.Session.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
                        }
                    }

                    collection.DisconnectedFromServer();
                }
                catch (Exception e)
                {
                    collection._config.Logger?.LogError(e, "Exception in UpdateBroadcast loop");
                }
            }


        }, this, TaskCreationOptions.DenyChildAttach);
    }
    
    protected async Task<bool> UpdateAndWaitAsync(INexusCollectionMessage message)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot perform operations when collection is read-only");

        if (IsServer)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _processChannel.Writer.WriteAsync(new ProcessRequest(null, message, tcs)).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        else
        {

            message.Id = Interlocked.Increment(ref _ackId);

            //TODO: Review using PooledValueTask; https://mgravell.github.io/PooledAwait/
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ackTcs!.TryAdd(message.Id, tcs);
            var result = await _client!.Writer!.WriteAsync(message).ConfigureAwait(false);

            // Since the message is only used by one writer, return it directly to the cache.
            message.ReturnToCache();

            if (result)
                return await tcs.Task.ConfigureAwait(false);

            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }
    
    public async ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!IsServer)
            throw new InvalidOperationException("List is not setup in Server mode.");

        await pipe.ReadyTask.ConfigureAwait(false);

        // Check to see if the pipe was completed after the ready task.
        if (pipe.CompleteTask.IsCompleted)
            return;

        var writer = new NexusChannelWriter<INexusCollectionMessage>(pipe);
        var reader = Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelReader<INexusCollectionMessage>(pipe) : null;
        
        await InitializeNewClient(writer).ConfigureAwait(false);

        var client = new Client(
            pipe,
            reader,
            writer,
            session) { State = Client.StateType.Initializing };
        _nexusPipeList.Add(client);
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (client, list) = ((Client,  SnapshotList<Client>))state!;
            list.Remove(client);
        }, (client, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);

        writer.WriteAsync(NexusCollectionResetStartMessage.Rent());
        // Send initialization data.
        await SendClientInitData(writer).ConfigureAwait(false);
        
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
                client.Session.Logger?.LogInfo(e, "Could not send collection broadcast message to session.");
                // Ignore and disconnect.
            }

        }, client, TaskCreationOptions.DenyChildAttach);

        client.State = Client.StateType.AcceptingUpdates;
        // If the reader is not null, that means we have a bidirectional collection.
        if (reader != null)
        {
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    await _processChannel.Writer.WriteAsync(new ProcessRequest(client, message, null)).ConfigureAwait(false);

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
    
    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <exception cref="Exception"></exception>
    public async Task ConnectAsync()
    {
        // Connect on the server is a noop.
        if (IsServer)
            return;

        var pipe = _session!.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");

        // Invoke the method on the server to activate the pipe.
        _invoker!.Logger?.Log(
            (_invoker.Logger.Behaviors & Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0
                ? Logging.NexusLogLevel.Information
                : Logging.NexusLogLevel.Debug,
            _invoker.Logger.Category,
            null,
            $"Connecting Proxy Collection[{Id}];");
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe).ConfigureAwait(false);

        await pipe.ReadyTask.ConfigureAwait(false);

        _client = new Client(
            pipe,
            new NexusChannelReader<INexusCollectionMessage>(pipe),
            Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelWriter<INexusCollectionMessage>(pipe) : null,
            _session);

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>

        {
            var collection = Unsafe.As<NexusCollection>(state)!;
            try
            {
                await collection._client!.Pipe.ReadyTask.ConfigureAwait(false);

                await foreach (var message in collection._client!.Reader!.ConfigureAwait(false))
                {
                    
                    if (message is NexusCollectionAckMessage ack)
                    {
                        if(collection._ackTcs!.TryRemove(ack.Id, out var ackTcs))
                            ackTcs.TrySetResult(true);
                        else 
                            collection._client.Session.Logger?.LogWarning($"Could not find AckTcs for id {ack.Id}.");
                    }
                    
                    var success = collection.ProcessClientOperation(message);
                    
                    // Don't return these messages to the cache as they are created on reading.
                    //operation.ReturnToCache();
                    if(message is INexusCollectionValueMessage valueMessage)
                        valueMessage.ReturnValueToPool();
       
                    // If the result is false, close the whole pipe
                    if (!success)
                    {
                        await collection._client.Session.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
                        return;
                    }
                } 
            }
            catch (Exception e)
            {
                collection._client?.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
            }


            collection.DisconnectedFromServer();
        }, this, TaskCreationOptions.DenyChildAttach);
    }
    
    protected abstract IEnumerable<NexusCollectionResetValuesMessage> ResetValuesEnumerator(
        NexusCollectionResetValuesMessage message);
    
    protected async ValueTask SendClientInitData(NexusChannelWriter<INexusCollectionMessage> writer)
    {

        
        var reset = NexusCollectionResetStartMessage.Rent();
        var resetComplete = NexusCollectionResetCompleteMessage.Rent();
        var batchValue = NexusCollectionResetValuesMessage.Rent();
 
        await writer.WriteAsync(reset);

        foreach (var values in ResetValuesEnumerator(batchValue))
        {
            await writer.WriteAsync(values);
        }
        await writer.WriteAsync(resetComplete);
        
        reset.ReturnToCache();
        resetComplete.ReturnToCache();
        batchValue.ReturnToCache();
    }

    public async Task DisconnectAsync()
    {
        // Disconnect on the server is a noop.
        if(IsServer)
            return;
        
        var client = _client;
        if (client == null)
            return;

        await client.Pipe.CompleteAsync().ConfigureAwait(false);

        foreach (var taskCompletionSource in _ackTcs)
            taskCompletionSource.Value.TrySetResult(false);
        
        _ackTcs.Clear();
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }
    
    protected abstract void DisconnectedFromServer();

    /// <summary>
    /// Process the message into an operation.  The client is only passed on server.
    /// </summary>
    /// <param name="operation">Operational message to process.</param>
    /// <returns>True on successful processing.  False on error.  When false, the channel to the client will close.</returns>
    protected abstract (INexusCollectionMessage? message, bool disconnect, INexusCollectionMessage? ackMessage) 
        ProcessOperation(INexusCollectionMessage operation);

    private class Client
    {
        public readonly INexusDuplexPipe Pipe;
        public readonly INexusChannelReader<INexusCollectionMessage>? Reader;
        public readonly INexusChannelWriter<INexusCollectionMessage>? Writer;
        public readonly INexusSession Session;
        public readonly Channel<INexusCollectionMessage> MessageSender;

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
            MessageSender = Channel.CreateBounded<INexusCollectionMessage>(new BoundedChannelOptions(10)
            {
                SingleReader = true,
                SingleWriter = true, 
                FullMode = BoundedChannelFullMode.Wait
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
