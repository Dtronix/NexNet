using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

internal abstract class NexusCollection : INexusCollectionConnector
{
    private NexusCollectionState _state;
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;


    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;

    private static int _ackId = 0; 
    
    private readonly SnapshotList<Client>? _nexusPipeList;
    
    private CancellationTokenSource? _broadcastCancellation;
    private Channel<ProcessRequest>? _processChannel;
    private ConcurrentDictionary<int, TaskCompletionSource<bool>>? _ackTcs;
    protected readonly INexusLogger? Logger;
    private TaskCompletionSource? _clientConnectTcs;
    private TaskCompletionSource? _disconnectTcs;
    protected bool IsClientResetting { get; private set; }
    
    public NexusCollectionState State => _state;

    /// <summary>
    /// Internal testing for times the server does not ack a message, when it would normally do so.
    /// </summary>
    internal bool DoNotSendAck = false;

    private record struct ProcessRequest(
        Client? Client,
        INexusCollectionMessage Message,
        TaskCompletionSource<bool>? Tcs);
    
    public bool IsReadOnly => !IsServer && Mode != NexusCollectionMode.BiDrirectional;
    
    protected NexusCollection(ushort id, NexusCollectionMode mode, ConfigBase config, bool isServer)
    {
        Id = id;
        Mode = mode;
        IsServer = isServer;
        
        Logger = config.Logger?.CreateLogger("NexusCollection", $"{this.GetType().Name}:{id}");
        
        if (isServer)
        {
            _nexusPipeList = new SnapshotList<Client>(64);
            
        }
        else
        {
            _ackTcs = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
        }
    }

    protected record struct ServerProcessMessageResult(INexusCollectionMessage? Message, bool Disconnect, bool Ack);
    private ServerProcessMessageResult ServerProcessMessage(INexusCollectionMessage message)
    {
        switch (message)
        {
            case NexusCollectionResetStartMessage:
            case NexusCollectionResetValuesMessage:
            case NexusCollectionResetCompleteMessage:
            case NexusCollectionAckMessage:
            {
                Logger?.LogError($"Server received an invalid message from the client. {message.GetType()}");
                return new ServerProcessMessageResult(null, true, false);
            }
        }
        
        try
        {
            return OnServerProcessMessage(message);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Exception while processing server message");
            return new ServerProcessMessageResult(null, true, false);
        }
    }
    
    public Task<bool> ClearAsync()
    {
        var message = NexusCollectionClearMessage.Rent();
        message.Version = GetVersion();
        return UpdateAndWaitAsync(message);
    }
    
    protected abstract ServerProcessMessageResult OnServerProcessMessage(INexusCollectionMessage message);
    protected abstract bool OnClientProcessMessage(INexusCollectionMessage message);
    protected abstract bool OnClientResetValues(ReadOnlySpan<byte> data);
    protected abstract bool OnClientResetStarted(int version, int totalValues);
    protected abstract bool OnClientResetCompleted();
    
    private bool ClientProcessMessage(INexusCollectionMessage serverMessage)
    {
        try
        {
            switch (serverMessage)
            {
                // No broadcasting on the reset operations as they are only client operations.
                case NexusCollectionResetStartMessage message:
                    if (IsClientResetting)
                        return false;
                    IsClientResetting = true;
                    return OnClientResetStarted(message.Version, message.TotalValues);
            
                case NexusCollectionResetValuesMessage message:
                    if (!IsClientResetting)
                        return false;
                    return OnClientResetValues(message.Values.Span);

                case NexusCollectionResetCompleteMessage:
                    if (!IsClientResetting)
                        return false;
                    IsClientResetting = false;
                    var completeResult = OnClientResetCompleted();
                    _clientConnectTcs?.SetResult();
                    return completeResult;      
                
                case NexusCollectionClearMessage message:
                    if (IsClientResetting)
                        return false;
                    return OnClientClear(message.Version);
                    
                case NexusCollectionAckMessage ackOperation:
                    if(_ackTcs!.TryRemove(ackOperation.Id, out var tcs))
                        tcs.TrySetResult(true);
                    return true;
            }

            if (IsClientResetting)
                return false;
            
            return OnClientProcessMessage(serverMessage);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Exception while processing client message");
            return false;
        }
    }

    protected abstract bool OnClientClear(int version);

    protected bool RequireValidProcessState()
    {
        if (IsClientResetting)
            return false;

        // If this is the server, and we are not in bidirectional mode, then the client
        // is sending messages when they are not supposed to.
        return !IsServer || Mode == NexusCollectionMode.BiDrirectional;
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
                    collection.Logger?.LogDebug("Started broadcast reading loop.");
                    await foreach (var req in collection._processChannel!.Reader
                                       .ReadAllAsync(collection._broadcastCancellation!.Token)
                                       .ConfigureAwait(false))
                    {
                        var result = collection.ServerProcessMessage(req.Message);
                        
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
                            result.Message.Remaining = collection._nexusPipeList!.Count;
                            foreach (var client in collection._nexusPipeList)
                            {
                                try
                                {
                                    // If the client is not accepting updates, then ignore the update and allow
                                    // for future poling for updates.  This happens when a client is initializing
                                    // all the items.
                                    if (client.State == Client.StateType.AcceptingUpdates)
                                    {
                                        if (!client.MessageSender.Writer.TryWrite(result.Message))
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

                        req.Tcs?.TrySetResult(true);

                        // Notify the sending client that the operation was complete.
                        if (result.Ack && req.Client != null && !collection.DoNotSendAck)
                        {
                            var ackMessage = NexusCollectionAckMessage.Rent();
                            ackMessage.Remaining = 1;
                            ackMessage.Id = req.Message.Id;
                            if (!req.Client.MessageSender.Writer.TryWrite(ackMessage))
                            {
                                ackMessage.ReturnToCache();
                                req.Client.Session.Logger?.LogWarning("Could not write to client message processor.");
                                await req.Client.Session.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    collection.Logger?.LogError(e, "Exception in UpdateBroadcast loop");
                }
            }


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
        var reader = Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelReader<INexusCollectionMessage>(pipe) : null;
        
        var client = new Client(
            pipe,
            reader,
            writer,
            session) { State = Client.StateType.Initializing };
        _nexusPipeList!.Add(client);
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (_, state )=>
        {
            var (client, list) = ((Client,  SnapshotList<Client>))state!;
            list.Remove(client);
        }, (client, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);

        Logger?.LogTrace("Sending client init data");
        // Initialize the client's data.
        var initResult = await SendClientInitData(writer).ConfigureAwait(false);
        
        if (!initResult)
            return;
        
        Logger?.LogTrace("Starting channel listener.");
        
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
                    Logger?.LogTrace($"Received message. {message.GetType()}");
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

    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <param name="token"></param>
    /// <exception cref="Exception"></exception>
    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        // Connect on the server is a noop.
        if (IsServer)
            return false;
        
        // Client is already connected.
        if(_state == NexusCollectionState.Connected)
            return true;

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

        _ = pipe.CompleteTask.ContinueWith((s, state) => 
            Unsafe.As<NexusCollection>(state)!.ClientDisconnected(), this, token);

        _client = new Client(
            pipe,
            new NexusChannelReader<INexusCollectionMessage>(pipe),
            Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelWriter<INexusCollectionMessage>(pipe) : null,
            _session);
        
        _clientConnectTcs = new TaskCompletionSource();
        _disconnectTcs = new TaskCompletionSource();

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
                            collection.Logger?.LogWarning($"Could not find AckTcs for id {ack.Id}.");
                    }
                    
                    collection.Logger?.LogTrace($"Client received message. {message.GetType()}");
                    var success = collection.ClientProcessMessage(message);
                    
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

            collection.ClientDisconnected();
            
        }, this, TaskCreationOptions.DenyChildAttach);
        
        // Wait for either the complete task fires or the client is actually connected.
        await Task.WhenAny(_client!.Pipe.CompleteTask, _clientConnectTcs.Task).ConfigureAwait(false);
        
        // Check to see if we have connected or have just been disconnected.
        var isConnected = _clientConnectTcs.Task.IsCompleted;
        _clientConnectTcs = null;

        if (isConnected)
            _state = NexusCollectionState.Connected;

        return isConnected;
    }

    protected async Task<bool> UpdateAndWaitAsync(INexusCollectionMessage message)
    {
        if (_state != NexusCollectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot perform operations when collection is read-only");

        if (IsServer)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _processChannel!.Writer.WriteAsync(new ProcessRequest(null, message, tcs)).ConfigureAwait(false);

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
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    protected abstract IEnumerable<INexusCollectionMessage> ResetValuesEnumerator(
        NexusCollectionResetValuesMessage message);
    
    protected async ValueTask<bool> SendClientInitData(NexusChannelWriter<INexusCollectionMessage> writer)
    {
        try
        {
            var resetComplete = NexusCollectionResetCompleteMessage.Rent();
            var batchValue = NexusCollectionResetValuesMessage.Rent();
            bool sentData = false;

            foreach (var values in ResetValuesEnumerator(batchValue))
            {
                sentData = true;
                await writer.WriteAsync(values);
            }
            
            batchValue.ReturnToCache();
            
            if (!sentData)
            {
                resetComplete.ReturnToCache();
                Logger?.LogError("No reset start reset message was sent during reset.");
                return false;
            }
            
            await writer.WriteAsync(resetComplete);
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
            await _disconnectTcs.Task;
            _disconnectTcs = null;
        }
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    protected void ClientDisconnected()
    {
        foreach (var taskCompletionSource in _ackTcs!)
            taskCompletionSource.Value.TrySetResult(false);
        
        _ackTcs.Clear();

        try
        {
            OnClientDisconnected();
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while disconnecting client.");
        }
        
        _disconnectTcs?.SetResult();
    }
    protected abstract void OnClientDisconnected();

    protected abstract int GetVersion();
    

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
