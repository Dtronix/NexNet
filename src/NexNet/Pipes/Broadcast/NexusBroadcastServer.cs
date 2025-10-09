using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;


internal abstract class NexusBroadcastServer : INexusBroadcastConnector
{
    private readonly NexusBroadcastConnectionManager _connectionManager;
    private readonly NexusBroadcastMessageProcessor _processor;
    private CancellationTokenSource? _stopCts;
    private bool _isRunning;
    
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly INexusLogger? Logger;
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;

    internal bool DoNotSendAck
    {
        set
        {
            _connectionManager.DoNotSendAck = value;
        }
    }

    protected NexusBroadcastServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"BRS{id}");
        CoreChangedEvent =  new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        _connectionManager = new NexusBroadcastConnectionManager(Logger);
        _processor = new NexusBroadcastMessageProcessor(Logger, ProcessMessage);
    }
    
    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if (!_isRunning)
        {
            Logger?.LogError("Connection attempted to connect while the broadcaster has stopped.  Connection will be closed.");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }
        
        var writer = new NexusChannelWriter<INexusCollectionUnion<>>(pipe);
        var client = new NexusBroadcastSession(pipe, writer, session);
        _connectionManager.AddClientAsync(client);
        
        Logger?.LogTrace($"S{session.Id} Sending client init data");
        // Initialize the client's data.
        
        try
        {
            client.BufferTryWrite(NexusCollectionListResetStartMessage.Rent().Wrap());
            foreach (var values in ResetValuesEnumerator())
            {
                client.BufferTryWrite(values.Wrap());
            }
            
            client.BufferTryWrite(NexusCollectionListResetCompleteMessage.Rent().Wrap());
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
            ? new NexusChannelReader<INexusCollectionUnion<>>(pipe) 
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
        
        _stopCts = new CancellationTokenSource();
        _connectionManager.Run(_stopCts.Token);
        _processor.Run(_stopCts.Token);
        _isRunning = true;
    }

    public void Stop()
    {
        _isRunning = false;
        _stopCts?.Cancel();
    }

    protected abstract IEnumerable<INexusCollectionUnion<>> ResetValuesEnumerator();
    protected abstract ProcessResult OnProcess(INexusCollectionUnion<> message,
        INexusBroadcastSession? sourceClient,
        CancellationToken ct);
    
    
    private NexusBroadcastMessageProcessor.ProcessResult ProcessMessage(INexusCollectionUnion<> message, INexusBroadcastSession? sourceClient, CancellationToken ct)
    {
        var (broadcastMessage, disconnect) = OnProcess(message, sourceClient, ct);
        if (broadcastMessage == null)
            return new NexusBroadcastMessageProcessor.ProcessResult(false, disconnect);

        _connectionManager.BroadcastAsync(broadcastMessage, sourceClient);
        return new NexusBroadcastMessageProcessor.ProcessResult(true, disconnect);
    }

    public record struct ProcessResult(INexusCollectionUnion<>? BroadcastMessage, bool Disconnect);
    
    protected ValueTask<bool> ProcessMessage(INexusCollectionUnion<> message)
    {
        return _processor.EnqueueWaitForResult(message, null);
    }
}
