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
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Collections;

internal abstract class NexusCollection<T, TBaseMessage> : INexusCollectionConnector
    where TBaseMessage : INexusCollectionMessage
{
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected static readonly Type TType = typeof(T);

    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    
    private readonly LockFreeArrayList<Client> _nexusPipeList;
    
    private CancellationTokenSource? _broadcastCancellation;
    private Channel<(IOperation Op, int Version)> _broadcastChannel;

    protected NexusCollection(ushort id, NexusCollectionMode mode, bool isServer)
    {
        Id = id;
        Mode = mode;
        IsServer = isServer;
        _nexusPipeList = new LockFreeArrayList<Client>(64);
    }

    public void StartUpdateBroadcast()
    {
        if(_broadcastCancellation != null)
            throw new InvalidOperationException("Broadcast has been already started");
        
        _broadcastCancellation = new CancellationTokenSource();
        _broadcastChannel = Channel.CreateBounded<(IOperation Op, int Version)>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _ = Task.Factory.StartNew(async static state =>
        {
            var collection = Unsafe.As<NexusCollection<T, TBaseMessage>>(state)!;
            
            await foreach (var operation in collection._broadcastChannel.Reader.ReadAllAsync(collection._broadcastCancellation!.Token))
            {
                var message = collection.ConvertToListOperation(operation.Op, operation.Version);
                foreach (var client in collection._nexusPipeList)
                {
                    try
                    {
                        // If the client is not accepting updates, then ignore the update and allow
                        // for future poling for updates.  This happens when a client is initializing
                        // all the items.
                        if (client.State == Client.StateType.AcceptingUpdates)
                            await client.Writer!.WriteAsync(message);
                    }
                    catch
                    {
                        // If we threw, the client is disconnected.  Remove the client.
                        collection._nexusPipeList.Remove(client);
                    }
                }
                message.ReturnToCache();
            }

            collection.DisconnectedFromServer();
        }, this, TaskCreationOptions.LongRunning);
    }

    protected ValueTask BroadcastAsync(IOperation message, int version)
    {
        return _broadcastChannel.Writer.WriteAsync((message, version));
    }
    
    protected async ValueTask UpdateServerAsync(TBaseMessage message)
    {
        var result = await _client.Writer.WriteAsync(message);
        
        message.ReturnToCache();

        if (result == false)
            await DisconnectAsync();
    }

    protected abstract TBaseMessage ConvertToListOperation(IOperation operation, int version);
    
    public async ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!IsServer)
            throw new InvalidOperationException("List is not setup in Server mode.");

        await pipe.ReadyTask;

        // Check to see if the pipe was completed after the ready task.
        if (pipe.CompleteTask.IsCompleted)
            return;

        var writer = new NexusChannelWriter<TBaseMessage>(pipe);
        var reader = Mode == NexusCollectionMode.BiDrirectional ? null : new NexusChannelReader<TBaseMessage>(pipe);
        
        await InitializeNewClient(writer);
        
        _nexusPipeList.Add(new Client(
            pipe, 
            reader,
            writer,
            session));
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (pipe, list) = ((INexusDuplexPipe, LockFreeArrayList<INexusDuplexPipe>))state!;
            list.Remove(pipe);
        }, (pipe, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);

        // If the reader is not null, that means we have a bidirectional collection.
        if (reader != null)
        {
            await foreach (var operation in reader)
            {
                var result = await ProcessOperation(operation);

                // If the result is false, close the whole session.
                if (!result)
                {
                    await session.DisconnectAsync(DisconnectReason.ProtocolError);
                    return;
                }
            }
        }

        await pipe.CompleteTask;
    }

    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <exception cref="Exception"></exception>
    public async Task ConnectAsync()
    {
        // Connect on the server is a noop.
        if(IsServer)
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
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<Byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)), InvocationFlags.DuplexPipe);

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

            await collection._client!.Pipe.ReadyTask;

            await foreach (var operation in collection._client!.Reader!)
            {
                var result = await collection.ProcessOperation(operation);
                
                // If the result is false, close the whole pipe
                if (!result)
                {
                    await collection._client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
                    return;
                }
            }

            collection.DisconnectedFromServer();
        }, this, TaskCreationOptions.LongRunning);
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
    
    protected abstract ValueTask<bool> ProcessOperation(TBaseMessage operation);
    
    private class Client
    {
        public readonly INexusDuplexPipe Pipe;
        public readonly INexusChannelReader<TBaseMessage>? Reader;
        public readonly INexusChannelWriter<TBaseMessage>? Writer;
        public readonly INexusSession Session;

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
        }
        
        public enum StateType
        {
            Unset,
            AcceptingUpdates,
            Disconnected
        }
    }

}
