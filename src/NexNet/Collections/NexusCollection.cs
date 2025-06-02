using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections;

internal abstract class NexusCollection<T, TBaseMessage> : INexusCollectionConnector
    where TBaseMessage : INexusCollectionMessage
{
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    private readonly ConfigBase _config;
    protected static readonly Type TType = typeof(T);

    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;

    private static int _ackId = 0; 
    
    private readonly LockFreeArrayList<Client>? _nexusPipeList;
    
    private CancellationTokenSource? _broadcastCancellation;
    private Channel<(Client client, TBaseMessage message)> _processChannel;
    private ConcurrentDictionary<int, TaskCompletionSource>? _ackTcs;

    protected NexusCollection(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
    {
        
        Id = id;
        Mode = mode;
        _config = config;
        IsServer = isServer;
        
        if (isServer)
        {
            _nexusPipeList = new LockFreeArrayList<Client>(64);
            
        }
        else
        {
            _ackTcs = new ConcurrentDictionary<int, TaskCompletionSource>();
        }
    }

    public void StartUpdateBroadcast()
    {
        if(_broadcastCancellation != null)
            throw new InvalidOperationException("Broadcast has been already started");
        
        _broadcastCancellation = new CancellationTokenSource();
        _processChannel = Channel.CreateBounded<(Client client, TBaseMessage message)>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _ = Task.Factory.StartNew(async static state =>
        {
            var collection = Unsafe.As<NexusCollection<T, TBaseMessage>>(state)!;

            while (true)
            {
                try
                {
                    collection._config.Logger?.LogDebug("Started broadcast reading loop.");
                    await foreach (var value in collection._processChannel.Reader.ReadAllAsync(collection._broadcastCancellation!.Token))
                    {
                        var (message, disconnect, ackMessage) = collection.ProcessOperation(value.message);

                        // If the result is false, close the whole session.
                        if (disconnect)
                        {
                            value.client.Session.Logger?.LogWarning("Disconnected from collection due to protocol error.");
                            await value.client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
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
                                            await client.Pipe.CompleteAsync();
                                        }

                                        //await client.Writer!.WriteAsync(message);
                                    }
                                }
                                catch
                                {
                                    // If we threw, the client is disconnected.  Remove the client.
                                    collection._nexusPipeList.Remove(client);
                                }
                            }
                        }
                        
                        // Notify the sending client that the operation was complete.
                        if (ackMessage == null)
                            continue;

                        if (!value.client.MessageSender.Writer.TryWrite(ackMessage))
                        {
                            value.client.Session.Logger?.LogWarning("Could not write to client message processor.");
                            await value.client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
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

    //protected ValueTask BroadcastAsync(IOperation message, int version)
    //{
    //    return _processChannel.Writer.WriteAsync((message, version));
    //}
    
    protected async Task UpdateServerAsync(TBaseMessage message)
    {
        var id = message.Id = Interlocked.Increment(ref _ackId);
        var result = await _client.Writer.WriteAsync(message);
        
        // Since the message is only used by one writer, return it directly to the cache.
        message.ReturnToCache();

        if (result == false)
        {
            await DisconnectAsync();
            return;
        }
        
        //TODO: Review using PooledValueTask; https://mgravell.github.io/PooledAwait/
        var tcs = new TaskCompletionSource();
        _ackTcs!.TryAdd(id, tcs);
        await tcs.Task;
    }
    
    public async ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!IsServer)
            throw new InvalidOperationException("List is not setup in Server mode.");

        await pipe.ReadyTask;

        // Check to see if the pipe was completed after the ready task.
        if (pipe.CompleteTask.IsCompleted)
            return;

        var writer = new NexusChannelWriter<TBaseMessage>(pipe);
        var reader = Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelReader<TBaseMessage>(pipe) : null;
        
        
        await InitializeNewClient(writer);

        var client = new Client(
            pipe,
            reader,
            writer,
            session) { State = Client.StateType.Initializing };
        _nexusPipeList.Add(client);
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (pipe, list) = ((Client,  LockFreeArrayList<Client>))state!;
            list.Remove(pipe);
        }, (client, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);

        // Send initialization data.
        await SendClientInitData(writer);
        
        // Start the listener on the channel to handle sending updates to the client.
        _ = Task.Factory.StartNew(static async state =>
        {
            var client = Unsafe.As<Client>(state!);

            try
            {
                await foreach (var message in client.MessageSender.Reader.ReadAllAsync())
                {
                    await client.Writer!.WriteAsync(message);
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
                await foreach (var message in reader)
                {
                    _processChannel.Writer.WriteAsync((client, message));
                    //await client.ClientMessageProcessor.Writer.WriteAsync((client, message));

                }
            }
            catch (Exception e)
            {
                client.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
                // Ignore and disconnect.
            }

        }

        await pipe.CompleteTask;
    }
    
    protected abstract ValueTask SendClientInitData(NexusChannelWriter<TBaseMessage> operation);

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
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<Byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe);

        await pipe.ReadyTask;

        _client = new Client(
            pipe,
            new NexusChannelReader<TBaseMessage>(pipe),
            Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelWriter<TBaseMessage>(pipe) : null,
            _session);

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>

        {
            var collection = Unsafe.As<NexusCollection<T, TBaseMessage>>(state)!;
            try
            {
                await collection._client!.Pipe.ReadyTask;

                await foreach (var operation in collection._client!.Reader!)
                {
                    
                    if (operation is NexusCollectionAckMessage ack)
                    {
                        if(collection._ackTcs!.TryRemove(ack.Id, out var ackTcs))
                            ackTcs.SetResult();
                        else 
                            collection._client.Session.Logger?.LogWarning($"Could not find AckTcs for id {ack.Id}.");
                    }
                    
                    (TBaseMessage? message, bool disconnect, _) = collection.ProcessOperation(operation);

       
                    // If the result is false, close the whole pipe
                    if (disconnect)
                    {
                        await collection._client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
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

    public async Task DisconnectAsync()
    {
        // Disconnect on the server is a noop.
        if(IsServer)
            return;
        
        var client = _client;
        if (client == null)
            return;

        await client.Pipe.CompleteAsync();
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    
    protected abstract void DisconnectedFromServer();
    
    protected abstract ValueTask InitializeNewClient(NexusChannelWriter<TBaseMessage> writer);

    /// <summary>
    /// Process the message into an operation.  The client is only passed on server.
    /// </summary>
    /// <param name="operation">Operational message to process.</param>
    /// <returns>True on successful processing.  False on error.  When false, the channel to the client will close.</returns>
    protected abstract (TBaseMessage? message, bool disconnect, TBaseMessage? ackMessage) 
        ProcessOperation(TBaseMessage operation);

    protected class Client
    {
        public readonly INexusDuplexPipe Pipe;
        public readonly INexusChannelReader<TBaseMessage>? Reader;
        public readonly INexusChannelWriter<TBaseMessage>? Writer;
        public readonly INexusSession Session;
        public readonly Channel<TBaseMessage> MessageSender;

        public StateType State;

        public Client(INexusDuplexPipe pipe, 
            INexusChannelReader<TBaseMessage>? reader,
            INexusChannelWriter<TBaseMessage>? writer,
            INexusSession session)
        {
            Pipe = pipe;
            Reader = reader;
            Writer = writer;
            Session = session;
            MessageSender = Channel.CreateBounded<TBaseMessage>(new BoundedChannelOptions(10)
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
