using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Pipes;

namespace NexNet.Collections;

internal abstract partial class NexusCollectionServer : INexusCollectionConnector
{
    private readonly NexusCollectionConnectionManager _connectionManager;
    private readonly NexusCollectionMessageProcessor _processor;
    
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly INexusLogger? Logger;
    protected SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;

    protected NexusCollectionServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"Coll{id}");
        _connectionManager = new NexusCollectionConnectionManager(Logger);
        _processor = new NexusCollectionMessageProcessor(Logger, ProcessMessage);
    }

    private bool ProcessMessage(INexusCollectionMessage message, INexusCollectionClient? sourceClient, CancellationToken ct)
    {
        var result = OnProcess(message, ct);
        if (result != null)
        {
            _connectionManager.BroadcastAsync(result, sourceClient);
            return true;
        }
        
        return false;
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        Logger?.LogError("Can not configure a non-proxy.");
        throw new NotImplementedException();
    }

    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        var writer = new NexusChannelWriter<INexusCollectionMessage>(pipe);
        var client = new NexusCollectionClient(pipe, writer, session);
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
            ? new NexusChannelReader<INexusCollectionMessage>(pipe) 
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
    
    protected abstract IEnumerable<INexusCollectionMessage> ResetValuesEnumerator();
    protected abstract INexusCollectionMessage? OnProcess(INexusCollectionMessage process, CancellationToken ct);
    
    private ValueTask<bool> ProcessMessage(INexusCollectionMessage message)
    {
        return _processor.EnqueueWaitForResult(message, null);
    }
}
