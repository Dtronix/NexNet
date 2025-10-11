using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Threading;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

internal class NexusBroadcastMessageProcessor<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly OnProcessDelegate _process;
    protected readonly INexusLogger? Logger;
    private readonly Channel<ProcessRequestWrapper> _processorChannel;
    private bool _isRunning;
    private readonly PooledResettableValueTaskCompletionSource<bool> _stoppedTcs;
    
    public ValueTask<bool> StoppedTask => _stoppedTcs.Task;
    
    public NexusBroadcastMessageProcessor(INexusLogger? logger, OnProcessDelegate process)
    {
        _process = process;
        Logger = logger?.CreateLogger("Processor");
        _stoppedTcs = PooledResettableValueTaskCompletionSource<bool>.Rent();
        
        _processorChannel = Channel.CreateBounded<ProcessRequestWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }


    public async ValueTask<bool> EnqueueWaitForResult(TUnion message, INexusBroadcastSession<TUnion>? client)
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
        _stoppedTcs.Reset();
        
        Task.Factory.StartNew(async static args =>
        {
            var (processor, ct) = ((NexusBroadcastMessageProcessor<TUnion>, CancellationToken))args!;
            
            processor.Logger?.LogTrace("Started processing loop.");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var messageWrapper in processor._processorChannel.Reader.ReadAllAsync(ct)
                                       .ConfigureAwait(false))
                    {
                        bool success = false;
                        try
                        {
                            processor.Logger?.LogTrace(
                                $"Source Client S{messageWrapper.Client?.Id}: Processing {messageWrapper.Message.GetType().Name} message.");

                            (success, var disconnect) =
                                processor._process(messageWrapper.Message, messageWrapper.Client, ct);

                            if (disconnect)
                                processor.Logger?.LogTrace(
                                    $"Source Client S{messageWrapper.Client?.Id}: Processing disconnected pipe. Process Success result: {success}.");

                            if (disconnect && messageWrapper.Client != null)
                            {
                                await messageWrapper.Client.CompletePipe().ConfigureAwait(false);
                            }
                        }
                        catch (Exception e)
                        {
                            processor.Logger?.LogInfo(e,
                                $"S{messageWrapper.Client?.Id} Exception while processing collection.");

                            if (messageWrapper.Client != null)
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
                catch (OperationCanceledException)
                {
                    processor.Logger?.LogDebug("Broadcast loop cancelled.");
                }
                catch (Exception e)
                {
                    processor.Logger?.LogError(e, "Exception in broadcast loop.");
                }
                finally
                {
                    processor._isRunning = false;
                    processor._stoppedTcs.TrySetResult(true);
                }
            }
            
        }, (this, token), TaskCreationOptions.DenyChildAttach);
    }
    
    
    private class ProcessRequestWrapper
    {
        private static readonly ConcurrentBag<ProcessRequestWrapper> _pool = new ();
        public INexusBroadcastSession<TUnion>? Client;
        public TUnion Message = null!;
        public PooledResettableValueTaskCompletionSource<bool>? CompletionTaskSource;
        
        private ProcessRequestWrapper()
        {

        }
        
        public static ProcessRequestWrapper Rent(INexusBroadcastSession<TUnion>? client, 
            TUnion message,
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



    public delegate BroadcastMessageProcessResult OnProcessDelegate(TUnion process, INexusBroadcastSession<TUnion>? sourceClient, CancellationToken ct);
}


public record struct BroadcastMessageProcessResult(bool Success, bool Disconnect);
