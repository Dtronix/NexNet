using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using NexNet.Internals.Threading;
using NexNet.Logging;

namespace NexNet.Collections;

internal class NexusCollectionMessageProcessor
{
    private readonly OnProcessDelegate _process;
    protected readonly INexusLogger? Logger;
    private readonly Channel<ProcessRequestWrapper> _processorChannel;
    private bool _isRunning;
    
    public NexusCollectionMessageProcessor(INexusLogger? logger, OnProcessDelegate process)
    {
        _process = process;
        Logger = logger?.CreateLogger("Processor");
        
        _processorChannel = Channel.CreateBounded<ProcessRequestWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }


    public async ValueTask<bool> EnqueueWaitForResult(INexusCollectionMessage message, INexusCollectionClient? client)
    {
        var tcs = PooledResettableValueTaskCompletionSource<bool>.Rent();
        var wrapper = ProcessRequestWrapper.Rent(client, message, tcs);
        await _processorChannel.Writer.WriteAsync(wrapper).ConfigureAwait(false);
        var result = await tcs.Task.ConfigureAwait(false);
        tcs.Return();
        return result;
    }
    
    public void Run(CancellationToken token)
    {
        if(_isRunning)
            throw new InvalidOperationException("Broadcaster is already running.");
        
        _isRunning = true;
        
        Task.Factory.StartNew(async static args =>
        {
            var (processor, ct) = ((NexusCollectionMessageProcessor, CancellationToken))args!;
            
            processor.Logger?.LogTrace("Started processing loop.");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var messageWrapper in processor._processorChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        bool success = false;
                        try
                        {
                            (success, var disconnect) = processor._process(messageWrapper.Message, messageWrapper.Client, ct);

                            if (disconnect && messageWrapper.Client != null)
                                await messageWrapper.Client.CompletePipe().ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            processor.Logger?.LogInfo(e,
                                $"S{messageWrapper.Client?.Id} Exception while processing collection.");
                            
                            if(messageWrapper.Client != null)
                                await messageWrapper.Client.CompletePipe().ConfigureAwait(false)!;
                        }
                        finally
                        {
                            messageWrapper.CompletionTaskSource?.TrySetResult(success);
                            messageWrapper.Return();
                        }
                    }
                    processor.Logger?.LogDebug("Exited broadcast reading loop.");
                }
                catch (Exception e)
                {
                    processor.Logger?.LogError(e, "Exception in broadcast loop");
                }
            }
            
        }, (this, token), TaskCreationOptions.DenyChildAttach);
    }
    
    
    private class ProcessRequestWrapper
    {
        private static readonly ConcurrentBag<ProcessRequestWrapper> _pool = new ();
        public INexusCollectionClient? Client;
        public INexusCollectionMessage Message = null!;
        public PooledResettableValueTaskCompletionSource<bool>? CompletionTaskSource;
        
        private ProcessRequestWrapper()
        {

        }
        
        public static ProcessRequestWrapper Rent(INexusCollectionClient? client, 
            INexusCollectionMessage message,
            PooledResettableValueTaskCompletionSource<bool>? completionTaskSource)
        {
            if (!_pool.TryTake(out var wrapper))
                wrapper = new ProcessRequestWrapper();
            
            wrapper.Client = client;
            wrapper.Message = message;
            wrapper.CompletionTaskSource = completionTaskSource;
            
            return wrapper;
        }

        public void Return()
        {
            _pool.Add(this);
        }
    }

    public record struct ProcessResult(bool Success, bool Disconnect);

    public delegate ProcessResult OnProcessDelegate(INexusCollectionMessage process, INexusCollectionClient? sourceClient, CancellationToken ct);
}
