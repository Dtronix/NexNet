using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Internals.Threading;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes.Broadcast;


internal abstract class NexusBroadcastClient<TUnion> : NexusBroadcastBase<TUnion>, INexusCollectionConnector
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private ushort _pipeId;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    private NexusBroadcastSession<TUnion>? _client;
    
    private TaskCompletionSource? _initializedTcs;
    private TaskCompletionSource? _disconnectTcs;
    private TaskCompletionSource? _readyTcs;

    // Token of the current enable's processor run, and the failure recorded when that processor stopped on its own.
    private CancellationToken _processorToken;
    private InvalidOperationException? _processorFailure;

    // Serializes message processing with the reset that runs when the pipe closes.
    private readonly object _processLock = new();

    protected NexusBroadcastSession<TUnion>? Client => _client;

    /// <summary>
    /// Connection state of the collection: Connected once the initial reset has been processed, Disconnected
    /// before that and after the pipe closes.
    /// </summary>
    public NexusCollectionState State { get; private set; } = NexusCollectionState.Disconnected;

    /// <summary>
    /// Test seam: runs before each message is processed. Always null outside tests.
    /// </summary>
    internal Action<TUnion>? ProcessTestHook { get; set; }

    public Task DisabledTask => _disconnectTcs?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Gets a task that completes when the collection is ready (connected and synchronized). Each
    /// <see cref="EnableAsync"/> starts a fresh task. It faults with an <see cref="InvalidOperationException"/> when
    /// the enable fails or the collection disconnects before its first synchronization completes, and keeps its
    /// outcome after a later disconnect. Completed when the collection was never enabled.
    /// </summary>
    public Task ReadyTask => _readyTcs?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Gets a task that completes when the collection becomes disconnected.
    /// </summary>
    public Task DisconnectedTask => _disconnectTcs?.Task ?? Task.CompletedTask;

    protected NexusBroadcastClient(ushort id, NexusCollectionMode mode, INexusLogger? logger)
        : base(id, mode, logger, "BRC")
    {
    }

    public async ValueTask DisableAsync()
    {
        if (_client == null)
            return;

        // Captured before the pipe closes: once it has, a new enable may replace them.
        var disconnectedTask = _disconnectTcs!.Task.ConfigureAwait(false);
        var loopTask = Processor.LoopTask;
        // Complete the pipe and everything closes.
        await _client!.CompletePipe().ConfigureAwait(false);

        await disconnectedTask;

        // This run's processor loop, not Processor.StoppedTask: that pooled task is reset by the next run's start.
        try
        {
            await loopTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A faulted loop is logged by SuperviseProcessorAsync; the collection is disabled either way.
            Logger?.LogDebug(e, "Collection message processor loop ended with an error.");
        }
    }

    public async ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        if (_client != null)
            return true;

        if(_session == null)
            throw new InvalidOperationException("Session not connected");

        // A fresh ReadyTask for this enable, visible before the first await so callers can wait on it while the
        // enable is in flight. Every path that ends the enable without a synchronization fails it.
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyTcs = readyTcs;

        // Rent first: a local refusal (pipe limit or budget) then has nothing to undo.
        var pipe = _session!.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");

        OperationSemaphore = new SemaphoreSlim(1, 1);
        var stopCts = StopCts = new CancellationTokenSource();
        Volatile.Write(ref _processorFailure, null);
        _processorToken = stopCts.Token;
        var runGeneration = Processor.Run(stopCts.Token);
        _ = SuperviseProcessorAsync(Processor.LoopTask, stopCts.Token);

        try
        {
            // Invoke the method on the server to activate the pipe.
            _invoker!.Logger?.Log(
                (_invoker.Logger.Behaviors & NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0
                    ? NexusLogLevel.Information
                    : NexusLogLevel.Debug,
                null,
                null,
                $"Connecting Proxy Collection[{Id}];");
            var __args = new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe));
            await _invoker.ProxyInvokeMethodCore(Id, NexNet.Internals.PayloadSerializer.Serialize(__args),
                InvocationFlags.DuplexPipe).ConfigureAwait(false);

            await pipe.ReadyTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The server refused the pipe (pipe limit or authorization), or the session ended before it opened.
            await AbortEnableAsync(pipe, stopCts).ConfigureAwait(false);
            FailReady(readyTcs, new InvalidOperationException(
                $"The collection could not be enabled: {e.Message}", e));
            throw;
        }

        _pipeId = pipe.Id;

        var writer = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<TUnion>(pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>()) : null;
        _client = new NexusBroadcastSession<TUnion>(pipe, writer, _session);

        _initializedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectTcs = _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new EnableRun(this, _client, stopCts, OperationSemaphore, readyTcs, disconnectTcs);

        // Registered once the enable's state is in place, so a pipe that is already closed still runs the full
        // disconnect path against it. Not tied to the enable token: the cleanup must run whenever the pipe closes,
        // even after that token is canceled.
        _ = pipe.CompleteTask.ContinueWith(static (_, state) =>
        {
            var run = Unsafe.As<EnableRun>(state)!;
            run.Owner.Disconnected(run);
        }, run, CancellationToken.None);

        // Long-running task listening for changes.
        // The reader feeds this run only: messages still buffered in this pipe after it closes must not reach the
        // processor of a later pipe, ahead of that pipe's reset.
        _ = Task.Factory.StartNew(async static state =>
        {
            var (broadcaster, runGeneration) = ((NexusBroadcastClient<TUnion>, int))state!;
            var clientState = broadcaster._client;
            var logger = broadcaster.Logger;

            if (clientState == null)
                return;

            if(logger != null)
                logger.PathSegment = $"S{clientState.Id}|P{broadcaster._pipeId:00000}|BR{broadcaster.Id}";

            var processorToken = broadcaster._processorToken;
            var reader = new NexusChannelReader<TUnion>(clientState.Pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>());
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    logger?.LogTrace($"Received {message.GetType()} message.");
                    await broadcaster.Processor.EnqueueWaitForResult(message, null, runGeneration).ConfigureAwait(false);
                }
            }
            catch (ChannelClosedException) when (!processorToken.IsCancellationRequested)
            {
                // The processor closed its queue without being stopped: it ended on an unexpected error.
                broadcaster.OnProcessorStopped(processorToken);
            }
            catch (Exception e)
            {
                logger?.LogInfo(e, "Error while reading session collection message.");
                // Ignore and disconnect.
            }

            // Ensure the pipe is completed if the reading loop exited.
            await clientState.CompletePipe().ConfigureAwait(false);

            // Return the pipe so its ID, slot and receive window are freed once the server has closed it too.
            if (clientState.Pipe is IRentedNexusDuplexPipe rentedPipe)
                await rentedPipe.DisposeAsync().ConfigureAwait(false);

        }, (this, runGeneration), TaskCreationOptions.DenyChildAttach);
        
        // Capture locally to avoid race with Disconnected() nulling _client.
        var client = _client!;

        // Wait for either the complete task fires or the client is actually connected.
        await Task.WhenAny(client.Pipe.CompleteTask, _initializedTcs.Task).ConfigureAwait(false);

        // Check to see if we have connected or have just been disconnected.
        var isDisconnected = client.Pipe.CompleteTask.IsCompleted;
        _initializedTcs = null;

        // Finish this run's cleanup before reporting the failure, so an immediate retry starts a fresh enable instead
        // of returning true for the closed pipe.
        if (isDisconnected)
            await disconnectTcs.Task.ConfigureAwait(false);

        // The pipe is closed after the failure is recorded, so a processor failure is always visible here.
        if (isDisconnected && Volatile.Read(ref _processorFailure) is { } failure)
            throw new InvalidOperationException(failure.Message, failure);

        return !isDisconnected;
    }

    /// <summary>
    /// Watches the message processor of one enable. A processor that stops while its run is still active ended on
    /// an unexpected error.
    /// </summary>
    private async Task SuperviseProcessorAsync(Task loop, CancellationToken token)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Collection message processor loop faulted.");
        }

        OnProcessorStopped(token);
    }

    /// <summary>
    /// Handles a processor that stopped on its own: logs the error, sets <see cref="State"/> to Disconnected, faults
    /// <see cref="ReadyTask"/> and closes the collection pipe. The pipe close runs the normal disconnect path: the collection resets,
    /// <see cref="DisabledTask"/> completes, and a pending <see cref="EnableAsync"/> throws. Re-enabling the
    /// collection (relays do it on their own) resynchronizes it from the server.
    /// </summary>
    /// <param name="token">Token of the processor run that stopped. A cancelled run was stopped on purpose.</param>
    private void OnProcessorStopped(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        var failure = new InvalidOperationException(
            "The collection message processor stopped unexpectedly; the collection was disabled. Enable it again to resynchronize.");

        // Only the first observer of the current run's stop handles it.
        if (!token.Equals(_processorToken)
            || Interlocked.CompareExchange(ref _processorFailure, failure, null) != null)
            return;

        Logger?.LogError(failure.Message);

        if (_readyTcs is { } readyTcs)
            FailReady(readyTcs, failure);

        State = NexusCollectionState.Disconnected;

        if (_client is { } client)
            _ = ClosePipeAsync(client).AsTask();
    }

    /// <summary>
    /// Closes the collection pipe after a protocol error or a processor failure. The pipe's completion then runs
    /// <see cref="Disconnected"/>, which resets the collection.
    /// </summary>
    private async ValueTask ClosePipeAsync(NexusBroadcastSession<TUnion> client)
    {
        try
        {
            await client.CompletePipe().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogDebug(e, "Error while closing the collection pipe.");
        }
    }


    /// <summary>
    /// Undoes a failed <see cref="EnableAsync"/> before the pipe opened: returns the pipe to the pipe manager,
    /// stops the message processor and resets the state, so a later <see cref="EnableAsync"/> can retry.
    /// </summary>
    private async ValueTask AbortEnableAsync(IRentedNexusDuplexPipe pipe, CancellationTokenSource stopCts)
    {
        try
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogDebug(e, "Error while returning the pipe of a failed collection connect.");
        }

        // Stops the processor; a later Run waits for this loop to unwind before it processes anything.
        // ReSharper disable once MethodHasAsyncOverload
        stopCts.Cancel();

        if (ReferenceEquals(StopCts, stopCts))
            StopCts = null;
        stopCts.Dispose();

        OperationSemaphore?.Dispose();
        OperationSemaphore = null;
        State = NexusCollectionState.Disconnected;
    }

    /// <summary>
    /// Faults a <see cref="ReadyTask"/> that has not completed yet. The fault is observed here, so an unawaited
    /// ReadyTask raises no unobserved-task exception; callers awaiting it still see it.
    /// </summary>
    private static void FailReady(TaskCompletionSource readyTcs, Exception failure)
    {
        if (readyTcs.TrySetException(failure))
            _ = readyTcs.Task.Exception;
    }

    /// <summary>
    /// The state of one enable run. The pipe-close cleanup of a run acts on this, never on whichever run the fields
    /// hold by then: once <see cref="_client"/> is cleared a new enable may already have installed its own.
    /// </summary>
    private sealed class EnableRun(
        NexusBroadcastClient<TUnion> owner,
        NexusBroadcastSession<TUnion> client,
        CancellationTokenSource stopCts,
        SemaphoreSlim? operationSemaphore,
        TaskCompletionSource readyTcs,
        TaskCompletionSource disconnectTcs)
    {
        public readonly NexusBroadcastClient<TUnion> Owner = owner;
        public readonly NexusBroadcastSession<TUnion> Client = client;
        public readonly CancellationTokenSource StopCts = stopCts;
        public readonly SemaphoreSlim? OperationSemaphore = operationSemaphore;
        public readonly TaskCompletionSource ReadyTcs = readyTcs;
        public readonly TaskCompletionSource DisconnectTcs = disconnectTcs;
    }

    /// <summary>
    /// Test seam: runs during a run's disconnect cleanup, after the collection accepts a new enable and before the
    /// run's own tasks complete. Always null outside tests.
    /// </summary>
    internal Action? DisconnectCleanupTestHook { get; set; }

    private void Disconnected(EnableRun run)
    {
        try
        {
            State = NexusCollectionState.Disconnected;

            // Stop the run and reset the state together, between two messages: a message of the closed pipe that the
            // processor still holds is then dropped (see OnProcessCore), and cannot reach the reset state.
            lock (_processLock)
            {
                // ReSharper disable once MethodHasAsyncOverload
                run.StopCts.Cancel();

                try
                {
                    OnDisconnected();
                }
                catch (Exception e)
                {
                    Logger?.LogError(e, "Error while disconnecting client.");
                }
            }

            if (ReferenceEquals(OperationSemaphore, run.OperationSemaphore))
                OperationSemaphore = null;
            run.OperationSemaphore?.Dispose();

            using (var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset))
            {
                CoreChangedEvent.Raise(eventArgsOwner.Value);
            }

            Interlocked.CompareExchange(ref _client, null, run.Client);

            DisconnectCleanupTestHook?.Invoke();

            // From here on another enable may own the fields: only clear what still belongs to this run.
            if (ReferenceEquals(StopCts, run.StopCts))
                StopCts = null;
        }
        finally
        {
            // A disconnect before the first synchronization fails ReadyTask instead of leaving it pending forever. A
            // processor failure (OnProcessorStopped) has already faulted it with its own reason. ReadyTask keeps this
            // run's outcome until the next enable replaces it.
            FailReady(run.ReadyTcs, new InvalidOperationException(
                "The collection disconnected before its initial synchronization completed. Enable it again to resynchronize."));

            run.DisconnectTcs.TrySetResult();
            Interlocked.CompareExchange(ref _disconnectTcs, null, run.DisconnectTcs);
        }
    }
    protected abstract void OnDisconnected();

    protected override async ValueTask<BroadcastMessageProcessResult> OnProcessCore(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct)
    {
        // Outside the protocol-error handling below, so an injected failure reaches the processor itself.
        ProcessTestHook?.Invoke(message);

        BroadcastMessageProcessResult result;
        try
        {
            lock (_processLock)
            {
                // The pipe closed: its remaining messages are dropped. The state was reset for the next pipe.
                if (ct.IsCancellationRequested)
                    return new BroadcastMessageProcessResult(false, false);

                result = OnProcess(message, sourceClient, ct);
            }
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, $"Exception while processing {message.GetType().Name} message.");
            result = new BroadcastMessageProcessResult(false, true);
        }

        // Items read from the pipe are enqueued without a source session, so the processor does not act on the
        // disconnect result for them. A protocol error closes the pipe here instead.
        if (result.Disconnect && _client is { } client)
        {
            Logger?.LogInfo($"Protocol error while processing {message.GetType().Name} message. Closing the pipe.");
            await ClosePipeAsync(client).ConfigureAwait(false);
        }

        return result;
    }

    protected abstract BroadcastMessageProcessResult OnProcess(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);
    
    /// <summary>
    /// Marks the initial synchronization of the current run complete: sets <see cref="State"/> to Connected and
    /// completes <see cref="ReadyTask"/>.
    /// </summary>
    /// <param name="ct">Token of the processor run that processed the synchronization. A synchronization of a
    /// stopped run (the collection disconnected, possibly with a new enable already in flight) is ignored, so it
    /// cannot report a later run as connected.</param>
    protected void InitializationCompleted(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        State = NexusCollectionState.Connected;
        using (var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Ready))
        {
            CoreChangedEvent.Raise(eventArgsOwner.Value);
        }
        _initializedTcs?.TrySetResult();
        _readyTcs?.TrySetResult();
    }
    
    void INexusCollectionConnector.TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }
}
