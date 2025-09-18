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

internal abstract partial class NexusCollection : INexusCollectionConnector
{
    private NexusCollectionState _state;
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    
    protected readonly bool IsServer;

    private TaskCompletionSource _tcsReady = new TaskCompletionSource();
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    
    private NexusCollection? _relayFrom;
    private NexusCollection? _relayTo;
    private INexusCollectionClientConnector? _clientRelayConnector;
    
    private readonly SnapshotList<Client>? _nexusPipeList;
    
    private CancellationTokenSource? _broadcastCancellation;
    private Channel<ProcessRequest>? _processChannel;
    private TaskCompletionSource<bool>? _ackTcs;
    protected readonly INexusLogger? Logger;
    private TaskCompletionSource? _clientConnectTcs;
    private TaskCompletionSource? _disconnectTcs;
    private Task _disconnectedTask = Task.CompletedTask;
    protected bool IsClientResetting { get; private set; }

    public Task DisconnectedTask => _disconnectedTask;
    
    public Task ReadyTask => _tcsReady.Task;
    
    private SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);

    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent = new();
    
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
    
    protected NexusCollection(ushort id, NexusCollectionMode mode, INexusLogger? logger, bool isServer)
    {
        Id = id;
        Mode = mode;
        IsServer = isServer;
        Logger = logger?.CreateLogger($"Collection<{this.GetType().Name}>:{id}");
        
        if (isServer)
        {
            _nexusPipeList = new SnapshotList<Client>(64);
            
            // This task is always compelted on the server.
            _disconnectedTask = Task.CompletedTask;
        }
    }

    protected record struct ServerProcessMessageResult(INexusCollectionMessage? Message, bool Disconnect, bool Ack);
    private ServerProcessMessageResult ServerProcessMessage(INexusCollectionMessage message)
    {
        
        //Logger?.LogTrace($"Server processing {message} message.");
        switch (message)
        {
            case NexusCollectionResetStartMessage:
            case NexusCollectionResetValuesMessage:
            case NexusCollectionResetCompleteMessage:
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
        EnsureAllowedModificationState();
        var message = NexusCollectionClearMessage.Rent();
        message.Version = GetVersion();
        return UpdateAndWaitAsync(message);
    }
    
    protected abstract ServerProcessMessageResult OnServerProcessMessage(INexusCollectionMessage message);
    protected abstract bool OnClientProcessMessage(INexusCollectionMessage message);
    protected abstract bool OnClientResetValues(ReadOnlySpan<byte> data);
    protected abstract bool OnClientResetStarted(int version, int totalValues);
    protected abstract bool OnClientResetCompleted();

    protected void ProcessFlags(INexusCollectionMessage serverMessage)
    {
        if ((serverMessage.Flags & NexusCollectionMessageFlags.Ack) != 0)
        {
            var ackTcs = _ackTcs;
            if(ackTcs is null)
                Logger?.LogError("No operation is awaiting an ACK.");
            else
                ackTcs.TrySetResult(true);
        }
    }
    
    private bool ClientProcessMessage(INexusCollectionMessage messageFromServer)
    {
        try
        {
            // First relay the message to child collection if one exists
            var relayResult = true;
            if (_relayTo != null)
            {
                try
                {
                    //Logger?.LogTrace($"Client relaying {messageFromServer} message to child collection");
                    
                    // Process the message in the child collection as if it came from a server
                    relayResult = _relayTo.ClientProcessMessage(messageFromServer);
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, $"Client error while relaying {messageFromServer} message to child collection");
                    relayResult = false;
                }
            }
            
            //Logger?.LogTrace($"Client processing {messageFromServer} message.");
            
            switch (messageFromServer)
            {
                // No broadcasting on the reset operations as they are only client operations.
                case NexusCollectionResetStartMessage message:
                    if (IsClientResetting)
                        return false;
                    IsClientResetting = true;
                    return OnClientResetStarted(message.Version, message.TotalValues) && relayResult;
            
                case NexusCollectionResetValuesMessage message:
                    if (!IsClientResetting)
                        return false;
                    return OnClientResetValues(message.Values.Span) && relayResult;

                case NexusCollectionResetCompleteMessage:
                    if (!IsClientResetting)
                        return false;
                    IsClientResetting = false;
                    var completeResult = OnClientResetCompleted();
                    _clientConnectTcs?.TrySetResult();
                    _tcsReady.TrySetResult();
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    return completeResult && relayResult;

                case NexusCollectionClearMessage message:
                    if (IsClientResetting)
                        return false;
                    var clearResult = OnClientClear(message.Version);
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    return clearResult && relayResult;
            }

            if (IsClientResetting)
                return false;
            
            var processResult = OnClientProcessMessage(messageFromServer);
            return processResult && relayResult;
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

        // Special condition when this collection is acting as a relay.
        if (_clientRelayConnector != null && Mode == NexusCollectionMode.ServerToClient)
            return true;

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

            while (collection._broadcastCancellation?.IsCancellationRequested == false)
            {
                try
                {
                    collection.Logger?.LogTrace("Started broadcast reading loop.");
                    await foreach (var req in collection._processChannel!.Reader
                                       .ReadAllAsync(collection._broadcastCancellation!.Token)
                                       .ConfigureAwait(false))
                    {
                        //collection.Logger?.LogTrace($"Server received broadcast message request {req.Message}.");
                        var result = collection.ServerProcessMessage(req.Message);
                        
                        //collection.Logger?.LogTrace($"Server received message type: {result.Message?.GetType()}");
                        
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
                            result.Message.Remaining = collection._nexusPipeList!.Count - 1;
                            
                            // If there is 0 remaining and the client is set, then we are only responding to the client that
                            // sent the change to begin with.
                            var respondingToClientOnly = result.Message.Remaining <= 0 && req.Client != null;
                            
                            // For testing only
                            if (!collection.DoNotSendAck)
                            {
                                foreach (var client in collection._nexusPipeList)
                                {
                                    try
                                    {
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
                                                    "ACK Client Could not send to client collection");
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
                                            // If the client is not accepting updates, then ignore the update and allow
                                            // for future poling for updates.  This happens when a client is initializing
                                            // all the items.
                                            if (client.State == Client.StateType.AcceptingUpdates)
                                            {
                                                if (!client.MessageSender.Writer.TryWrite(result.Message))
                                                {
                                                    collection.Logger?.LogTrace("Could not send to client collection");
                                                    // Complete the pipe as it is full and not writing to the client at a decent
                                                    // rate.
                                                    await client.Pipe.CompleteAsync().ConfigureAwait(false);
                                                }
                                                else
                                                {
                                                    collection.Logger?.LogTrace("Sent to client collection");
                                                }
                                            }
                                            else
                                            {
                                                collection.Logger?.LogTrace("Client is not accepting updates");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        collection.Logger?.LogTrace(ex, "Exception while sending to collection");
                                        // If we threw, the client is disconnected.  Remove the client.
                                        collection._nexusPipeList.Remove(client);
                                    }
                                }
                            }
                        }
                        else
                        {
                            collection.Logger?.LogTrace("Translated into noop message");
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
        
        Logger?.LogTrace("Sending client init data");
        // Initialize the client's data.
        var initResult = await SendClientInitData(client).ConfigureAwait(false);
        
        if (!initResult)
            return;
        
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

    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <param name="token"></param>
    /// <exception cref="Exception"></exception>
    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        if(_clientRelayConnector != null)
            throw new InvalidOperationException("Collection is configured in a relay and can't be manually controlled.");
        
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
            null,
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
        
        _clientConnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectedTask = _disconnectTcs.Task;

        if (_relayTo != null)
        {
            _relayTo._disconnectedTask = _disconnectedTask;
        }

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>

        {
            var collection = Unsafe.As<NexusCollection>(state)!;
            try
            {
                await collection._client!.Pipe.ReadyTask.ConfigureAwait(false);

                await foreach (var message in collection._client!.Reader!.ConfigureAwait(false))
                {
                    //collection.Logger?.LogTrace($"<-- Receiving {message.GetType()}");
                    var success = collection.ClientProcessMessage(message);
                    
                    if(success)
                        collection.ProcessFlags(message);
                    
                    // Don't return these messages to the cache as they are created on reading.
                    //operation.ReturnToCache();
                    if (message is INexusCollectionValueMessage valueMessage)
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
        {
            if (_relayFrom != null)
                throw new InvalidOperationException(
                    "Cannot perform operations when collection is operating in a relay, read-only state.");
        }
        else
        {
            if (_client?.State != Client.StateType.AcceptingUpdates)
                throw new InvalidOperationException(
                    "Cannot perform operations when collection is disconnected.");
        }
    }

    protected void ClientDisconnected()
    {
        _state = NexusCollectionState.Disconnected;
        _ackTcs?.TrySetResult(false);
        
        // Reset the ready task
        _tcsReady.TrySetResult();
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
    protected abstract void OnClientDisconnected();

    protected abstract int GetVersion();

    protected async ValueTask<IDisposable> OperationLock()
    {
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(_operationSemaphore);
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
