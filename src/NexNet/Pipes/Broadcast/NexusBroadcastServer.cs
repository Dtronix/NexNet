using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Internals.Threading;
using NexNet.Invocation;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

internal abstract class NexusBroadcastServer<TUnion> : INexusBroadcastConnector, INexusBroadcastServerTestModifier
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly NexusBroadcastConnectionManager<TUnion> _connectionManager;
    private readonly NexusBroadcastMessageProcessor<TUnion> _processor;
    private CancellationTokenSource? _stopCts;
    private bool _isRunning;
    private SemaphoreSlim? _operationSemaphore;
    
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly INexusLogger? Logger;
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;

    bool INexusBroadcastServerTestModifier.DoNotSendAck
    {
        get => _connectionManager.DoNotSendAck;
        set => _connectionManager.DoNotSendAck = value;
    }

    protected NexusBroadcastServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"BR{id}");
        CoreChangedEvent =  new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        _connectionManager = new NexusBroadcastConnectionManager<TUnion>(Logger);
        _processor = new NexusBroadcastMessageProcessor<TUnion>(Logger, ProcessMessage);
    }
    
    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if (!_isRunning)
        {
            Logger?.LogError("Connection attempted to connect while the broadcaster has stopped.  Connection will be closed.");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }
        
        var writer = new NexusChannelWriter<TUnion>(pipe);
        var client = new NexusBroadcastSession<TUnion>(pipe, writer, session);
        _connectionManager.AddClientAsync(client);
        
        Logger?.LogTrace($"S{session.Id} Sending client init data");
        // Initialize the client's data.
        
        try
        {
            OnConnected(client);
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Cound not send client init data");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }
        
        // Ensure all initial data is fully flushed.
        await writer.Writer.FlushAsync().ConfigureAwait(false);
        
        var reader = Mode == NexusCollectionMode.BiDirectional 
            ? new NexusChannelReader<TUnion>(pipe) 
            : null;

        if (reader != null)
        {
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    // We want to wait for the processing of these messages to ensure we don't
                    // flood the processor with messages from one client.  Each client gets one item processed 
                    // at a time.  This allows also using the backpressure mechanism on pipes to deal with flooding.
                    //Logger?.LogTrace($"Received message. {message.GetType()}");
                    await _processor.EnqueueWaitForResult(message, client).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                client.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
                // Ignore and disconnect.
            }

        }
        
        //Ensure we await here because as soon as we exit this method, the pipe closes.
        await pipe.CompleteTask.ConfigureAwait(false);
    }

    public void Start()
    {
        if (_isRunning)
            return;

        _operationSemaphore = new SemaphoreSlim(1, 1);
        _stopCts = new CancellationTokenSource();
        _connectionManager.Run(_stopCts.Token);
        _processor.Run(_stopCts.Token);
        _isRunning = true;
    }

    public void Stop()
    {
        _operationSemaphore?.Dispose();
        _operationSemaphore = null;
        _isRunning = false;
        _stopCts?.Cancel();
    }
    
    protected abstract void OnConnected(NexusBroadcastSession<TUnion> client);
    protected abstract ProcessResult OnProcess(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);
    
    
    private BroadcastMessageProcessResult ProcessMessage(TUnion message, INexusBroadcastSession<TUnion>? sourceClient, CancellationToken ct)
    {
        var (broadcastMessage, disconnect) = OnProcess(message, sourceClient, ct);
        if (broadcastMessage == null)
            return new BroadcastMessageProcessResult(false, disconnect);

        _connectionManager.BroadcastAsync(broadcastMessage, sourceClient);
        return new BroadcastMessageProcessResult(true, disconnect);
    }

    public record struct ProcessResult(TUnion? BroadcastMessage, bool Disconnect);
    
    protected ValueTask<bool> ProcessMessage(TUnion message)
    {
        return _processor.EnqueueWaitForResult(message, null);
    }
    
    protected async ValueTask<IDisposable> OperationLock()
    {
        if(_operationSemaphore == null)
            throw new InvalidOperationException("Server has not started.");
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(_operationSemaphore);
    }
}

internal interface INexusBroadcastServerTestModifier
{
    internal bool DoNotSendAck { get; set; }
}
