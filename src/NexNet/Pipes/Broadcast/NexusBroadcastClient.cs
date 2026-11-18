using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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

    protected NexusBroadcastSession<TUnion>? Client => _client;

    /// <summary>
    /// Connection state of the collection: Connected once the initial reset has been processed, Disconnected
    /// before that and after the pipe closes.
    /// </summary>
    public NexusCollectionState State { get; private set; } = NexusCollectionState.Disconnected;

    public Task DisabledTask => _disconnectTcs?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Gets a task that completes when the collection is ready (connected and synchronized).
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

        var disconnectedTask = _disconnectTcs!.Task.ConfigureAwait(false);
        // Complete the pipe and everything closes.
        await _client!.CompletePipe().ConfigureAwait(false);
        
        await disconnectedTask;
        await Processor.StoppedTask.ConfigureAwait(false);
    }

    public async ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        if (_client != null)
            return true;

        if(_session == null)
            throw new InvalidOperationException("Session not connected");

        // Rent first: a local refusal (pipe limit or budget) then has nothing to undo.
        var pipe = _session!.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");

        OperationSemaphore = new SemaphoreSlim(1, 1);
        var stopCts = StopCts = new CancellationTokenSource();
        Processor.Run(stopCts.Token);

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
        catch
        {
            // The server refused the pipe (pipe limit or authorization), or the session ended before it opened.
            await AbortEnableAsync(pipe, stopCts).ConfigureAwait(false);
            throw;
        }

        _pipeId = pipe.Id;

        // Not tied to the enable token: the cleanup must run whenever the pipe closes, even after that token is
        // canceled.
        _ = pipe.CompleteTask.ContinueWith((s, state) =>
            Unsafe.As<NexusBroadcastClient<TUnion>>(state)!.Disconnected(), this, CancellationToken.None);

        var writer = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<TUnion>(pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>()) : null;
        _client = new NexusBroadcastSession<TUnion>(pipe, writer, _session);
        
        _initializedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>
        {
            var broadcaster = Unsafe.As<NexusBroadcastClient<TUnion>>(state)!;
            var clientState = broadcaster._client;
            var logger = broadcaster.Logger;

            if (clientState == null)
                return;

            if(logger != null)
                logger.PathSegment = $"S{clientState.Id}|P{broadcaster._pipeId:00000}|BR{broadcaster.Id}";

            var reader = new NexusChannelReader<TUnion>(clientState.Pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>());
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    logger?.LogTrace($"Received {message.GetType()} message.");
                    await broadcaster.Processor.EnqueueWaitForResult(message, null).ConfigureAwait(false);
                }
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

        }, this, TaskCreationOptions.DenyChildAttach);
        
        // Capture locally to avoid race with Disconnected() nulling _client.
        var client = _client!;

        // Wait for either the complete task fires or the client is actually connected.
        await Task.WhenAny(client.Pipe.CompleteTask, _initializedTcs.Task).ConfigureAwait(false);

        // Check to see if we have connected or have just been disconnected.
        var isDisconnected = client.Pipe.CompleteTask.IsCompleted;
        _initializedTcs = null;

        return !isDisconnected;
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

    private void Disconnected()
    {
        State = NexusCollectionState.Disconnected;

        try
        {
            OnDisconnected();
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while disconnecting client.");
        }

        OperationSemaphore?.Dispose();
        OperationSemaphore = null;

        using (var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset))
        {
            CoreChangedEvent.Raise(eventArgsOwner.Value);
        }

        _client = null;

        // ReSharper disable once MethodHasAsyncOverload
        StopCts?.Cancel();
        StopCts = null;

        _disconnectTcs?.TrySetResult();
        _disconnectTcs = null;
        _readyTcs = null;
    }
    protected abstract void OnDisconnected();

    protected override async ValueTask<BroadcastMessageProcessResult> OnProcessCore(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct)
    {
        BroadcastMessageProcessResult result;
        try
        {
            result = OnProcess(message, sourceClient, ct);
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
            try
            {
                await client.CompletePipe().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger?.LogDebug(e, "Error while closing the collection pipe after a protocol error.");
            }
        }

        return result;
    }

    protected abstract BroadcastMessageProcessResult OnProcess(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);
    
    protected void InitializationCompleted()
    {
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
