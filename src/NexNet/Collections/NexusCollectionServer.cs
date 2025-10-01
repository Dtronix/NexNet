using System;
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

namespace NexNet.Collections;

internal abstract partial class NexusCollectionServer : INexusCollectionConnector
{
    private NexusCollectionState _state;
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    
    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;

    protected readonly INexusLogger? Logger;
    
    protected SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;
    private readonly NexusCollectionBroadcaster _broadcaster;
    private readonly NexusCollectionMessageProcessor _processor;
    
    protected NexusCollectionServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"Coll{id}");
        _broadcaster = new NexusCollectionBroadcaster(Logger);
        _processor = new NexusCollectionMessageProcessor(Logger, ProcessMessage);
    }

    private bool ProcessMessage(INexusCollectionMessage process, CancellationToken ct)
    {
        _broadcaster.BroadcastAsync()
    }


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

    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        var writer = new NexusChannelWriter<INexusCollectionMessage>(pipe);
        _broadcaster.AddClientAsync(new NexusCollectionClient(pipe, writer, session));
        
        Logger?.LogTrace($"S{session.Id} Sending client init data");
        // Initialize the client's data.

        // If this a connection to a relay connection, send the init data from the source collection and not this one
        // as we don't keep any data in here.
        var sourceCollection = _relayFrom ?? this;
        var resetCount = 0;
        while(true)
        {
            var initResult = await sourceCollection.SendInitData(client).ConfigureAwait(false);
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
        
        _processor.EnqueueWaitForResult()

        return await WriteMessageToProcessChannel(message).ConfigureAwait(false);
    }
    
    protected abstract IEnumerable<INexusCollectionMessage> ResetValuesEnumerator(
        NexusCollectionResetValuesMessage message);
    
    private async ValueTask<bool> SendInitData(INexusCollectionClient client)
    {
        try
        {
            var resetComplete = NexusCollectionResetCompleteMessage.Rent();
            var batchValue = NexusCollectionResetValuesMessage.Rent();
            bool sentData = false;

            foreach (var values in ResetValuesEnumerator(batchValue))
            {
                sentData = true;
                await client.SendAsync().WriteAsync(values).ConfigureAwait(false);
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
