using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes.Broadcast;


internal abstract class NexusBroadcastClient
{
    private readonly NexusCollectionMessageProcessor _processor;
    private CancellationTokenSource? _stopCts;
    
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly INexusLogger? Logger;
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    private NexusCollectionClient? _client;
    private TaskCompletionSource? _clientConnectTcs;
    //private TaskCompletionSource? _disconnectTcs;

    protected NexusBroadcastClient(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"BRC{id}");
        CoreChangedEvent =  new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        _processor = new NexusCollectionMessageProcessor(Logger, ProcessMessage);
    }
    
    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <param name="token"></param>
    /// <exception cref="Exception"></exception>
    public async Task<bool> ConnectAsync()
    {
        if (_client != null)
            return true;
        
        if(_session == null)
            throw new InvalidOperationException("Session not connected");
        
        var pipe = _session!.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");

        // Invoke the method on the server to activate the pipe.
        _invoker!.Logger?.Log(
            (_invoker.Logger.Behaviors & NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0
                ? NexusLogLevel.Information
                : NexusLogLevel.Debug,
            null,
            null,
            $"Connecting Proxy Collection[{Id}];");
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe).ConfigureAwait(false);

        await pipe.ReadyTask.ConfigureAwait(false);

        _ = pipe.CompleteTask.ContinueWith((s, state) => 
            Unsafe.As<NexusBroadcastClient>(state)!.Disconnected(), this);
        
        var writer = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<INexusCollectionMessage>(pipe) : null;
        _client = new NexusCollectionClient(pipe, writer, _session);
        
        _clientConnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        //_disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        //_disconnectedTask = _disconnectTcs.Task;
        

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>
        {
            var broadcaster = Unsafe.As<NexusBroadcastClient>(state)!;
            var clientState = broadcaster._client;
            if (clientState == null)
                return;
            
            try
            {
                await foreach (var message in clientState.BufferRead().ConfigureAwait(false))
                {
                    clientState.Logger?.LogTrace($"<-- Receiving {message.GetType()}");
                    var success = await broadcaster._processor.EnqueueWaitForResult(message.Message!, null).ConfigureAwait(false);
                    
                    if(success)
                        clientState.ProcessFlags(message);
                    
                    var relayTo = clientState._relayTo;
                    if (relayTo != null && success)
                    {
                        // Relays ignore these types of messages.
                        if (message is NexusCollectionListResetStartMessage
                            or NexusCollectionListResetValuesMessage
                            or NexusCollectionListResetCompleteMessage)
                            continue;

                        try
                        {
                            // Set the total clients that should need to broadcast prior to returning.
                            message.Remaining = relayTo._connectedClients!.Count - 1;
                            foreach (var client in relayTo._connectedClients)
                            {
                                try
                                {
                                    if (!client.MessageSender.Writer.TryWrite(message))
                                    {
                                        clientState.Logger?.LogTrace("Could not send to client collection");
                                        // Complete the pipe as it is full and not writing to the client at a decent
                                        // rate.
                                        await client.Pipe.CompleteAsync().ConfigureAwait(false);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    clientState.Logger?.LogTrace(ex,
                                        "Exception while forwarding relay message to client");
                                    // If we threw, the client is disconnected.  Remove the client.
                                    relayTo._connectedClients.Remove(client);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            clientState.Logger?.LogError(e, "Exception while processing relay message");
                        }
                    }
                    else
                    {
                        // Don't return these messages to the cache as they are created on reading.
                        //operation.ReturnToCache();
                        if (message is INexusCollectionValueMessage valueMessage)
                            valueMessage.ReturnValueToPool();

                        // If the result is false, close the whole pipe
                        if (!success)
                        {
                            await clientState._client.Session.DisconnectAsync(DisconnectReason.ProtocolError)
                                .ConfigureAwait(false);
                            return;
                        }
                    }
                } 
            }
            catch (Exception e)
            {
                clientState._client?.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
            }
        }, this, TaskCreationOptions.DenyChildAttach);
        
        // Wait for either the complete task fires or the client is actually connected.
        var result = await Task.WhenAny(_client!.Pipe.CompleteTask, _clientConnectTcs.Task).ConfigureAwait(false);
        
        // Check to see if we have connected or have just been disconnected.
        var isDisconnected = _client!.Pipe.CompleteTask.IsCompleted;
        _clientConnectTcs = null;

        return !isDisconnected;
    }
    
    
    protected void Disconnected()
    {
        _state = NexusCollectionState.Disconnected;
        _ackTcs?.TrySetResult(false);
        
        // Reset the ready task
        if (_tcsReady.Task.Status != TaskStatus.WaitingForActivation)
            _tcsReady = new TaskCompletionSource();

        try
        {
            OnDisconnected();
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while disconnecting client.");
        }
        
        using var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset);
        
        CoreChangedEvent.Raise(eventArgsOwner.Value);
        
        _disconnectTcs?.TrySetResult();
    }
    protected abstract void OnDisconnected();
    
    public void ConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }


 
    protected abstract IEnumerable<INexusCollectionMessage> ResetValuesEnumerator();
    protected abstract ProcessResult OnProcess(INexusCollectionMessage message,
        INexusCollectionClient? sourceClient,
        CancellationToken ct);
    
    
    private NexusCollectionMessageProcessor.ProcessResult ProcessMessage(INexusCollectionMessage message, INexusCollectionClient? sourceClient, CancellationToken ct)
    {
        var (broadcastMessage, disconnect) = OnProcess(message, sourceClient, ct);
        if (broadcastMessage == null)
            return new NexusCollectionMessageProcessor.ProcessResult(false, disconnect);

        _connectionManager.BroadcastAsync(broadcastMessage, sourceClient);
        return new NexusCollectionMessageProcessor.ProcessResult(true, disconnect);
    }

    public record struct ProcessResult(INexusCollectionMessage? BroadcastMessage, bool Disconnect);
    
    protected ValueTask<bool> ProcessMessage(INexusCollectionMessage message)
    {
        return _processor.EnqueueWaitForResult(message, null);
    }
}
