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

        OperationSemaphore = new SemaphoreSlim(1, 1);
        StopCts = new CancellationTokenSource();
        Processor.Run(StopCts.Token);
        
        var pipe = _session!.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");

        // Invoke the method on the server to activate the pipe.
        _invoker!.Logger?.Log(
            (_invoker.Logger.Behaviors & NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0
                ? NexusLogLevel.Information
                : NexusLogLevel.Debug,
            null,
            null,
            $"Connecting Proxy Collection[{Id}];");
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe).ConfigureAwait(false);

        await pipe.ReadyTask.ConfigureAwait(false);

        _pipeId = pipe.Id;

        _ = pipe.CompleteTask.ContinueWith((s, state) =>
            Unsafe.As<NexusBroadcastClient<TUnion>>(state)!.Disconnected(), this, cancellationToken);

        var writer = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<TUnion>(pipe) : null;
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

            var reader = new NexusChannelReader<TUnion>(clientState.Pipe);
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

        }, this, TaskCreationOptions.DenyChildAttach);
        
        // Wait for either the complete task fires or the client is actually connected.
        var result = await Task.WhenAny(_client!.Pipe.CompleteTask, _initializedTcs.Task).ConfigureAwait(false);
        
        // Check to see if we have connected or have just been disconnected.
        var isDisconnected = _client!.Pipe.CompleteTask.IsCompleted;
        _initializedTcs = null;
        
        return !isDisconnected;
    }


    private void Disconnected()
    {
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

    protected override BroadcastMessageProcessResult OnProcessCore(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct)
    {
        return OnProcess(message, sourceClient, ct);
    }

    protected abstract BroadcastMessageProcessResult OnProcess(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);
    
    protected void InitializationCompleted()
    {
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
