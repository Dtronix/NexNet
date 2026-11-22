using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Threading;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Processes broadcast messages sequentially using a bounded channel to prevent flooding.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be processed.</typeparam>
internal class NexusBroadcastMessageProcessor<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly OnProcessDelegate _process;
    protected readonly INexusLogger? Logger;
    private Channel<ProcessRequestWrapper> _processorChannel;
    private readonly object _runLock = new();
    private CancellationToken _runToken;
    private Task _loop = Task.CompletedTask;
    private int _runGeneration;
    private readonly PooledResettableValueTaskCompletionSource<bool> _stoppedTcs;

    public ValueTask<bool> StoppedTask => _stoppedTcs.Task;

    /// <summary>
    /// The current run's processing loop. It completes when the run ends, whether cancelled or stopped by an error.
    /// </summary>
    internal Task LoopTask
    {
        get
        {
            lock (_runLock)
                return _loop;
        }
    }

    public NexusBroadcastMessageProcessor(INexusLogger? logger, OnProcessDelegate process)
    {
        _process = process;
        Logger = logger?.CreateLogger("Processor");
        _stoppedTcs = PooledResettableValueTaskCompletionSource<bool>.Rent();
        _processorChannel = CreateChannel();
    }

    private static Channel<ProcessRequestWrapper> CreateChannel()
    {
        return Channel.CreateBounded<ProcessRequestWrapper>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }


    /// <summary>
    /// Queues a message and waits for its processing result.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="client">The client that sent the message, or null.</param>
    /// <param name="runGeneration">When set, the message is accepted only by the run that <see cref="Run"/>
    /// returned this value for. A reader of a closed pipe then cannot feed a later run.</param>
    /// <exception cref="ChannelClosedException">The processor is stopped, or the given run has ended.</exception>
    public async ValueTask<bool> EnqueueWaitForResult(TUnion message, INexusBroadcastSession<TUnion>? client,
        int? runGeneration = null)
    {
        Channel<ProcessRequestWrapper> channel;
        lock (_runLock)
        {
            if (runGeneration is { } generation && generation != _runGeneration)
                throw new ChannelClosedException("The processor run ended.");
            channel = _processorChannel;
        }

        var tcs = PooledResettableValueTaskCompletionSource<bool>.Rent();
        var wrapper = ProcessRequestWrapper.Rent(client, message, tcs);
        try
        {
            await channel.Writer.WriteAsync(wrapper).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogDebug(e, $"S{client?.Id}: Enqueue rejected for {message.GetType().Name}, processor is stopped.");
            wrapper.Return();
            tcs.Return();
            throw;
        }
        var result = await tcs.Task.ConfigureAwait(false);
        tcs.Return();
        return result;
    }

    /// <summary>
    /// Runs an action on the processing loop, in order with the messages around it: no message is processed while
    /// it runs.
    /// </summary>
    /// <param name="action">The action to run. It receives the run's cancellation token.</param>
    /// <returns>True when the action ran to completion; false when it threw or the processor stopped first.</returns>
    public async ValueTask<bool> EnqueueActionWaitForResult(Func<CancellationToken, ValueTask> action)
    {
        var tcs = PooledResettableValueTaskCompletionSource<bool>.Rent();
        var wrapper = ProcessRequestWrapper.Rent(null, null!, tcs, action);
        try
        {
            await _processorChannel.Writer.WriteAsync(wrapper).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogDebug(e, "Enqueue of an action rejected, processor is stopped.");
            wrapper.Return();
            tcs.Return();
            throw;
        }
        var result = await tcs.Task.ConfigureAwait(false);
        tcs.Return();
        return result;
    }
    
    /// <summary>
    /// Starts the processing loop. A previous run that was cancelled but is still unwinding does not block this
    /// call: the new loop waits for it to exit before it processes anything.
    /// A run that stopped on its own after an unexpected error counts as stopped, so it can be started again.
    /// </summary>
    /// <returns>The run's generation, for <see cref="EnqueueWaitForResult"/>.</returns>
    /// <exception cref="InvalidOperationException">A previous run is still active and was not cancelled.</exception>
    public int Run(CancellationToken token)
    {
        lock (_runLock)
        {
            if (!_loop.IsCompleted && !_runToken.IsCancellationRequested)
                throw new InvalidOperationException("Broadcaster is already running.");

            var generation = ++_runGeneration;
            _runToken = token;
            _stoppedTcs.Reset();
            var channel = _processorChannel = CreateChannel();
            var previousLoop = _loop;
            _loop = Task.Run(() => RunLoop(this, channel, generation, previousLoop, token));
            return generation;
        }
    }

    private static async Task RunLoop(
        NexusBroadcastMessageProcessor<TUnion> processor,
        Channel<ProcessRequestWrapper> channel,
        int generation,
        Task previousLoop,
        CancellationToken ct)
    {
        // Processing is serialized: wait for a stopped run to finish its in-flight message.
        await previousLoop.ConfigureAwait(false);

        processor.Logger?.LogTrace("Started processing loop.");

        // One pass only: the finally below closes the channel, so the run is over however the read loop ends.
        // Looping again would spin on the closed channel until the token is cancelled.
        try
        {
            await foreach (var messageWrapper in channel.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                bool success = false;
                try
                {
                    if (messageWrapper.Action != null)
                    {
                        await messageWrapper.Action(ct).ConfigureAwait(false);
                        success = true;
                        continue;
                    }

                    processor.Logger?.LogTrace(
                        $"Source Client S{messageWrapper.Client?.Id}: Processing {messageWrapper.Message.GetType().Name} message.");

                    (success, var disconnect) =
                        await processor._process(messageWrapper.Message, messageWrapper.Client, ct).ConfigureAwait(false);

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
                        messageWrapper.Action != null
                            ? "Exception while running a processor action."
                            : $"S{messageWrapper.Client?.Id} Exception while processing collection.");

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            processor.Logger?.LogDebug("Broadcast loop cancelled.");
        }
        catch (Exception e)
        {
            // Unexpected: the run stops here. A later Run starts a fresh loop on a fresh channel.
            processor.Logger?.LogError(e, "Exception in broadcast loop; processor stopped.");
        }
        finally
        {
            // Complete the channel writer so any pending WriteAsync calls
            // in EnqueueWaitForResult throw ChannelClosedException instead of hanging.
            channel.Writer.TryComplete();

            // Drain any remaining items and resolve their completion sources
            // to prevent callers from hanging on tcs.Task.
            while (channel.Reader.TryRead(out var remaining))
            {
                remaining.CompletionTaskSource?.TrySetResult(false);
                remaining.Return();
            }

            // Mark the run stopped. A restart already reset the stopped signal for its own run; leave it alone.
            lock (processor._runLock)
            {
                if (processor._runGeneration == generation)
                    processor._stoppedTcs.TrySetResult(true);
            }
        }
    }
    
    
    private class ProcessRequestWrapper
    {
        private static readonly ConcurrentBag<ProcessRequestWrapper> _pool = new ();
        public INexusBroadcastSession<TUnion>? Client;
        public TUnion Message = null!;
        public PooledResettableValueTaskCompletionSource<bool>? CompletionTaskSource;
        public Func<CancellationToken, ValueTask>? Action;
        
        private ProcessRequestWrapper()
        {

        }
        
        public static ProcessRequestWrapper Rent(INexusBroadcastSession<TUnion>? client, 
            TUnion message,
            PooledResettableValueTaskCompletionSource<bool>? completionTaskSource,
            Func<CancellationToken, ValueTask>? action = null)
        {
            if (!_pool.TryTake(out var wrapper))
                wrapper = new ProcessRequestWrapper();
            
            wrapper.Client = client;
            wrapper.Message = message;
            wrapper.CompletionTaskSource = completionTaskSource;
            wrapper.Action = action;
            
            return wrapper;
        }

        public void Return()
        {
            Client = null;
            Message = null!;
            CompletionTaskSource = null;
            Action = null;
            _pool.Add(this);
        }
    }



    /// <summary>
    /// Delegate for processing broadcast messages.
    /// </summary>
    /// <param name="process">The message to process.</param>
    /// <param name="sourceClient">The client that sent the message, or null for server-originated messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of processing the message.</returns>
    public delegate ValueTask<BroadcastMessageProcessResult> OnProcessDelegate(TUnion process, INexusBroadcastSession<TUnion>? sourceClient, CancellationToken ct);
}

/// <summary>
/// Result of processing a broadcast message.
/// </summary>
/// <param name="Success">True if the message was processed successfully.</param>
/// <param name="Disconnect">True if the client should be disconnected after processing.</param>
public record struct BroadcastMessageProcessResult(bool Success, bool Disconnect);
