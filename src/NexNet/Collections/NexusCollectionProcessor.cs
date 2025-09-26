using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Collections;

internal abstract class NexusCollectionProcessor
{
    private readonly INexusLogger? _logger;
    private readonly Channel<ProcessRequestWrapper> _processorChannel;
    private bool _isRunning;
    
    private record ProcessRequestWrapper(
        INexusCollectionClient? Client, 
        INexusCollectionMessage Message,
        TaskCompletionSource<bool> CompletionTaskSource);

    protected NexusCollectionProcessor(INexusLogger? logger)
    {
        _logger = logger?.CreateLogger("Processor");
        
        _processorChannel = Channel.CreateBounded<ProcessRequestWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    protected abstract void OnProcess(INexusCollectionMessage process);
    
    public async ValueTask<bool> EnqueueWaitForResult(INexusCollectionMessage message, INexusCollectionClient? client)
    {
        var tcs = new TaskCompletionSource<bool>();
        var wrapper = new ProcessRequestWrapper(client, message, tcs);
        await _processorChannel.Writer.WriteAsync(wrapper).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }
    
    public void Run(CancellationToken token)
    {
        if(_isRunning)
            throw new InvalidOperationException("Broadcaster is already running.");
        
        _isRunning = true;
        
        Task.Factory.StartNew(async static args =>
        {
            var (processor, ct) = ((NexusCollectionProcessor, CancellationToken))args!;
            
            processor._logger?.LogTrace("Started processing loop.");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var messageWrapper in processor._processorChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            processor.OnProcess(messageWrapper.Message);
                        }
                        catch (Exception e)
                        {

                            processor._logger?.LogInfo(e,
                                $"S{messageWrapper.Client?.Id} Exception while sending to collection. Removing from broadcast.");

                            // If we threw, the client is disconnected. Remove the client and signal completion.
                            processor._connectedClients.Remove(client);
                            messageWrapper.SignalCompletion();
                        }
                        finally
                        {
                            messageWrapper.
                        }
                    }
                    processor._logger?.LogDebug("Exited broadcast reading loop.");
                }
                catch (Exception e)
                {
                    processor._logger?.LogError(e, "Exception in broadcast loop");
                }
            }
            
            processor._isRunning = false;

            // Close the client connections.
            foreach (var client in processor._connectedClients)
            {
                try
                {
                    await client.CompletePipe().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    processor._logger?.LogWarning(e, $"S{client.Id} Could not complete pipe.");
                }
            }
            
            processor._connectedClients.Clear();
            
        }, (this, token), TaskCreationOptions.DenyChildAttach);
    }
    
}
