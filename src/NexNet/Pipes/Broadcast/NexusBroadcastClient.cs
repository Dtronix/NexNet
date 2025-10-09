using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes.Broadcast;


internal abstract class NexusBroadcastClient : INexusCollectionConnector
{
    private readonly NexusBroadcastMessageProcessor _processor;
    private readonly ushort _id;
    private readonly NexusCollectionMode _mode;
    private readonly INexusLogger? _logger;
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    private NexusBroadcastSession? _client;
    private readonly SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);
    
    private TaskCompletionSource? _initializedTcs;
    private TaskCompletionSource? _disconnectTcs;

    protected NexusBroadcastSession Client => _client;

    public Task DisabledTask => _disconnectTcs?.Task ?? Task.CompletedTask;

    protected NexusBroadcastClient(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        _id = id;
        _mode = mode;
        _logger = logger?.CreateLogger($"BRC{id}");
        CoreChangedEvent =  new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        _processor = new NexusBroadcastMessageProcessor(_logger, OnProcess);
    }

    public async ValueTask DisableAsync()
    {
        if (_client == null)
            return;
        // Complete the pipe and everything closes.
        await _client!.CompletePipe().ConfigureAwait(false);
        
        await _disconnectTcs!.Task.ConfigureAwait(false);
    }
    
    public async ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        if (_client != null)
            return true;
        
        if(_session == null)
            throw new InvalidOperationException("Session not connected");
        
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
            $"Connecting Proxy Collection[{_id}];");
        await _invoker.ProxyInvokeMethodCore(_id, new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe).ConfigureAwait(false);

        await pipe.ReadyTask.ConfigureAwait(false);

        _ = pipe.CompleteTask.ContinueWith((s, state) => 
            Unsafe.As<NexusBroadcastClient>(state)!.Disconnected(), this, cancellationToken);
        
        var writer = _mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<INexusCollectionUnion<>>(pipe) : null;
        _client = new NexusBroadcastSession(pipe, writer, _session);
        
        _initializedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        //_disconnectedTask = _disconnectTcs.Task;
        

        // Long-running task listening for changes.
        _ = Task.Factory.StartNew(async static state =>
        {
            var broadcaster = Unsafe.As<NexusBroadcastClient>(state)!;
            var clientState = broadcaster._client;
            if (clientState == null)
                return;

            var reader = new NexusChannelReader<INexusCollectionUnion<>>(clientState.Pipe);
            try
            {
                // Read through all the messages received until complete.
                await foreach (var message in reader.ConfigureAwait(false))
                {
                    await broadcaster._processor.EnqueueWaitForResult(message, null).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                clientState.Session.Logger?.LogInfo(e, "Error while reading session collection message.");
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
            _logger?.LogError(e, "Error while disconnecting client.");
        }
        
        using var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset);
        
        CoreChangedEvent.Raise(eventArgsOwner.Value);
        _client = null;

        _disconnectTcs?.TrySetResult();
    }
    protected abstract void OnDisconnected();
    
    protected async ValueTask<IDisposable> OperationLock()
    {
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(_operationSemaphore);
    }
    
    protected abstract NexusBroadcastMessageProcessor.ProcessResult OnProcess(INexusCollectionUnion<> message,
        INexusBroadcastSession? sourceClient,
        CancellationToken ct);
    
    protected void InitializationCompleted()
    {
        _initializedTcs?.TrySetResult();
    }
    
    void INexusCollectionConnector.TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }
    
    private readonly struct SemaphoreSlimDisposable(SemaphoreSlim semaphore) : IDisposable
    { 
        public void Dispose()
        {
            semaphore.Release();
        }
    }


}
