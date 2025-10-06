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


internal abstract class NexusBroadcastClient
{
    private readonly NexusBroadcastMessageProcessor _processor;
    private CancellationTokenSource? _stopCts;
    
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected readonly INexusLogger? Logger;
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    private NexusBroadcastSession? _client;
    private readonly SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);
    
    private TaskCompletionSource? _initializedTcs;
    private TaskCompletionSource? _disconnectTcs;

    protected NexusBroadcastSession Client => _client;

    protected NexusBroadcastClient(ushort id, NexusCollectionMode mode, INexusLogger? logger)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"BRC{id}");
        CoreChangedEvent =  new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        _processor = new NexusBroadcastMessageProcessor(Logger, OnProcess);
    }
    
    /// <summary>
    /// Client side connect.  Execution on the server is a noop.
    /// </summary>
    /// <param name="token"></param>
    /// <exception cref="Exception"></exception>
    public async Task<bool> ConnectAsync()
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
            $"Connecting Proxy Collection[{Id}];");
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)),
            InvocationFlags.DuplexPipe).ConfigureAwait(false);

        await pipe.ReadyTask.ConfigureAwait(false);

        _ = pipe.CompleteTask.ContinueWith((s, state) => 
            Unsafe.As<NexusBroadcastClient>(state)!.Disconnected(), this);
        
        var writer = Mode == NexusCollectionMode.BiDirectional ? new NexusChannelWriter<INexusCollectionMessage>(pipe) : null;
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

            var reader = new NexusChannelReader<INexusCollectionMessage>(clientState.Pipe);
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
            Logger?.LogError(e, "Error while disconnecting client.");
        }
        
        using var eventArgsOwner = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset);
        
        CoreChangedEvent.Raise(eventArgsOwner.Value);
        _client = null;

        _disconnectTcs?.TrySetResult();
    }
    protected abstract void OnDisconnected();
    
    public void ConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }
    
    protected async ValueTask<IDisposable> OperationLock()
    {
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(_operationSemaphore);
    }
    
    protected abstract NexusBroadcastMessageProcessor.ProcessResult OnProcess(INexusCollectionMessage message,
        INexusBroadcastSession? sourceClient,
        CancellationToken ct);
    
    protected void InitializationCompleted()
    {
        _initializedTcs?.TrySetResult();
    }
    
    private readonly struct SemaphoreSlimDisposable(SemaphoreSlim semaphore) : IDisposable
    { 
        public void Dispose()
        {
            semaphore.Release();
        }
    }
}
