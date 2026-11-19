using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Internals.Threading;
using NexNet.Invocation;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

internal abstract class NexusBroadcastServer<TUnion> : NexusBroadcastBase<TUnion>, INexusBroadcastConnector, INexusBroadcastServerTestModifier
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    /// <summary>
    /// Most restarts of the message processor allowed within <see cref="RestartWindow"/> after it stops on an
    /// unexpected error. One more stop within the window gives up and stops the collection.
    /// </summary>
    internal const int MaxProcessorRestartsPerWindow = 5;

    /// <summary>
    /// The sliding window that <see cref="MaxProcessorRestartsPerWindow"/> counts restarts in.
    /// </summary>
    internal static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(1);

    private readonly NexusBroadcastConnectionManager<TUnion> _connectionManager;
    private readonly object _lifecycleLock = new();

    // Sessions currently inside ServerStartCollectionConnection, so a processor restart can reset them.
    private readonly ConcurrentDictionary<NexusBroadcastSession<TUnion>, byte> _sessions = new();

    // Start times (Environment.TickCount64) of the processor restarts within the restart window.
    private readonly Queue<long> _restartTimes = new();
    private bool _isRunning;

    /// <summary>
    /// Delay before the first restart of a processor that stopped on an unexpected error. It doubles with every
    /// restart still inside the restart window.
    /// </summary>
    internal TimeSpan ProcessorRestartBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The longest delay between a processor stop and its restart.
    /// </summary>
    internal TimeSpan MaxProcessorRestartBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True while the broadcaster is started and has not given up on its message processor.
    /// </summary>
    internal bool IsRunning => Volatile.Read(ref _isRunning);

    /// <summary>
    /// Test seam: runs before each message is processed. Always null outside tests.
    /// </summary>
    internal Action<TUnion>? ProcessTestHook { get; set; }

    bool INexusBroadcastServerTestModifier.DoNotSendAck
    {
        get => _connectionManager.DoNotSendAck;
        set => _connectionManager.DoNotSendAck = value;
    }

    protected NexusBroadcastServer(ushort id, NexusCollectionMode mode, INexusLogger? logger)
        : base(id, mode, logger, "BR")
    {
        _connectionManager = new NexusBroadcastConnectionManager<TUnion>(Logger);
    }
    
    public async ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if (!_isRunning)
        {
            Logger?.LogError("Connection attempted to connect while the broadcaster has stopped.  Connection will be closed.");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }
        
        var writer = new NexusChannelWriter<TUnion>(pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>());
        var client = new NexusBroadcastSession<TUnion>(pipe, writer, session);
        _sessions.TryAdd(client, 0);
        try
        {
            await RunClientAsync(pipe, writer, client).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(client, out _);
        }
    }

    private async ValueTask RunClientAsync(
        INexusDuplexPipe pipe,
        NexusChannelWriter<TUnion> writer,
        NexusBroadcastSession<TUnion> client)
    {
        var session = client.Session;
        Logger?.LogTrace($"S{session.Id} Sending client init data");

        // Initialize the client's data. The snapshot is captured and the client registered on the processing loop,
        // so no change is applied between them: every change before it is in the snapshot and is not sent to this
        // client, and every change after it is broadcast to this client after the snapshot (protocol specification
        // §12). The broadcasts wait in the client's buffer until the snapshot is written.
        bool initialized;
        IEnumerable<TUnion>? snapshot = null;
        try
        {
            initialized = await Processor.EnqueueActionWaitForResult(async ct =>
            {
                snapshot = CaptureSnapshot();
                await _connectionManager.RegisterClientAsync(client, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Cound not send client init data");
            initialized = false;
        }

        if (!initialized)
        {
            Logger?.LogWarning($"S{session.Id} Could not send client init data. Connection will be closed.");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }

        // Write the snapshot off the processing loop, waiting on the pipe's flow control, so a large snapshot
        // never overflows the client's buffer and a slow client does not hold up the other clients.
        if (snapshot != null && !await WriteSnapshotAsync(client, snapshot).ConfigureAwait(false))
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            // Sends the broadcasts buffered since the registration, then every later one.
            _connectionManager.StartClient(client);
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, $"S{session.Id} Could not start the collection writer. Connection will be closed.");
            await pipe.CompleteAsync().ConfigureAwait(false);
            return;
        }
        
        // Ensure all initial data is fully flushed. Through the channel writer: the session's writer loop may be
        // writing at the same time, and the pipe writer itself is not safe for that.
        await writer.FlushAsync().ConfigureAwait(false);
        
        var reader = Mode == NexusCollectionMode.BiDirectional 
            ? new NexusChannelReader<TUnion>(pipe, NexNet.Serialization.NexusFormatterRegistry.Get<TUnion>()) 
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
                    await Processor.EnqueueWaitForResult(message, client).ConfigureAwait(false);
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

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_isRunning)
                return;

            OperationSemaphore = new SemaphoreSlim(1, 1);
            var stopCts = StopCts = new CancellationTokenSource();
            _connectionManager.Run(stopCts.Token);
            Processor.Run(stopCts.Token);
            _restartTimes.Clear();
            Volatile.Write(ref _isRunning, true);
            _ = SuperviseProcessorAsync(stopCts.Token);
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
            StopCore();
    }

    private void StopCore()
    {
        OperationSemaphore?.Dispose();
        OperationSemaphore = null;
        Volatile.Write(ref _isRunning, false);
        StopCts?.Cancel();
    }

    /// <summary>
    /// Watches the message processor of one run (one <see cref="Start"/>). When it stops while the run is still
    /// active (an unexpected error ended its loop), logs the error, restarts it with backoff and resets the connected
    /// clients so they resynchronize. After <see cref="MaxProcessorRestartsPerWindow"/> restarts within
    /// <see cref="RestartWindow"/> it gives up: the broadcaster stops and its clients are disconnected.
    /// </summary>
    private async Task SuperviseProcessorAsync(CancellationToken token)
    {
        while (true)
        {
            Task loop;
            lock (_lifecycleLock)
            {
                if (token.IsCancellationRequested)
                    return;
                loop = Processor.LoopTask;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "Collection message processor loop faulted.");
            }

            // Stop cancels the token before the loop ends; anything else is an unexpected stop.
            if (token.IsCancellationRequested)
                return;

            TimeSpan delay;
            bool giveUp;
            lock (_lifecycleLock)
            {
                var now = Environment.TickCount64;
                while (_restartTimes.Count > 0 && now - _restartTimes.Peek() > (long)RestartWindow.TotalMilliseconds)
                    _restartTimes.Dequeue();

                giveUp = _restartTimes.Count >= MaxProcessorRestartsPerWindow;
                delay = giveUp ? TimeSpan.Zero : GetRestartDelay(_restartTimes.Count);
            }

            if (giveUp)
            {
                Logger?.LogError(
                    $"Collection message processor stopped unexpectedly {MaxProcessorRestartsPerWindow + 1} times within {RestartWindow}; stopping the collection. Clients are disconnected and new connections are refused until the server restarts.");

                lock (_lifecycleLock)
                {
                    if (token.IsCancellationRequested)
                        return;
                    StopCore();
                }

                await ResetClientsAsync().ConfigureAwait(false);
                return;
            }

            Logger?.LogError(
                $"Collection message processor stopped unexpectedly; restarting it in {delay.TotalMilliseconds:0} ms and resetting connected clients.");

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_lifecycleLock)
            {
                if (token.IsCancellationRequested)
                    return;

                Processor.Run(token);
                _restartTimes.Enqueue(Environment.TickCount64);
            }

            // The processor is running again: clients reconnect against the current state.
            await ResetClientsAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan GetRestartDelay(int recentRestarts)
    {
        var delay = ProcessorRestartBackoff;
        for (var i = 0; i < recentRestarts && delay < MaxProcessorRestartBackoff; i++)
            delay += delay;

        return delay < MaxProcessorRestartBackoff ? delay : MaxProcessorRestartBackoff;
    }

    /// <summary>
    /// Closes the pipe of every connected client. A message may have been lost with the stopped processor, so the
    /// clients' state can no longer be trusted; each client sees the collection disabled and re-enables it to
    /// resynchronize from the server state.
    /// </summary>
    private async ValueTask ResetClientsAsync()
    {
        foreach (var client in _sessions.Keys)
        {
            try
            {
                await client.CompletePipe().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger?.LogWarning(e, $"S{client.Id} Could not reset collection client.");
            }
        }
    }
    
    /// <summary>
    /// Writes the snapshot messages to the client in order, waiting for the pipe to accept each one. Every message
    /// is returned to its pool.
    /// </summary>
    /// <returns>True when the whole snapshot was written; false when the client closed or a write failed.</returns>
    private async ValueTask<bool> WriteSnapshotAsync(NexusBroadcastSession<TUnion> client, IEnumerable<TUnion> snapshot)
    {
        var sent = 0;
        try
        {
            foreach (var message in snapshot)
            {
                bool written;
                try
                {
                    written = await client.SendAsync(message, client.CompletionToken).ConfigureAwait(false);
                }
                finally
                {
                    message.Return();
                }

                if (!written)
                {
                    Logger?.LogInfo(
                        $"S{client.Id} Collection pipe closed after {sent} snapshot messages; the snapshot is incomplete. Closing the pipe.");
                    return false;
                }

                sent++;
            }

            return true;
        }
        catch (OperationCanceledException) when (client.CompletionToken.IsCancellationRequested)
        {
            Logger?.LogInfo(
                $"S{client.Id} Collection pipe closed after {sent} snapshot messages; the snapshot is incomplete.");
            return false;
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e,
                $"S{client.Id} Could not write the collection snapshot after {sent} messages. Closing the pipe.");
            return false;
        }
    }

    /// <summary>
    /// Runs on the processing loop when a client connects. Captures the current state and returns the messages that
    /// bring a new client to it (reset start, values, reset complete), or null when there is nothing to send.
    /// </summary>
    /// <remarks>
    /// Capture the state before returning: the messages are enumerated later, off the processing loop, while
    /// changes go on. Each message is rented and is returned to its pool once written.
    /// </remarks>
    protected virtual IEnumerable<TUnion>? CaptureSnapshot()
    {
        return null;
    }

    protected abstract ProcessResult OnProcess(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);

    protected override async ValueTask<BroadcastMessageProcessResult> OnProcessCore(TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct)
    {
        ProcessTestHook?.Invoke(message);
        var (broadcastMessage, disconnect, replyToSource) = OnProcess(message, sourceClient, ct);
        if (broadcastMessage == null)
        {
            if (replyToSource != null)
            {
                // Through the broadcast channel, so the source receives it after every change broadcast before it.
                if (sourceClient != null)
                    await _connectionManager.SendToClientAsync(replyToSource, sourceClient, ct).ConfigureAwait(false);
                else
                    replyToSource.Return();
            }

            return new BroadcastMessageProcessResult(false, disconnect);
        }

        await _connectionManager.BroadcastAsync(broadcastMessage, sourceClient, ct).ConfigureAwait(false);
        return new BroadcastMessageProcessResult(true, disconnect);
    }

    /// <summary>
    /// Result of processing a message.
    /// </summary>
    /// <param name="BroadcastMessage">The message to broadcast to every client, or null when nothing changed.</param>
    /// <param name="Disconnect">True to close the source client's pipe.</param>
    /// <param name="ReplyToSource">When nothing is broadcast: a message for the source client only. It is ordered
    /// after every change broadcast before it.</param>
    public record struct ProcessResult(TUnion? BroadcastMessage, bool Disconnect, TUnion? ReplyToSource = null);

    /// <summary>
    /// Broadcasts a server-originated message to every client. Call it only from an action running on the processing
    /// loop (<see cref="NexusBroadcastMessageProcessor{TUnion}.EnqueueActionWaitForResult"/>): no client registers
    /// between a change applied by that action and its broadcast. The message is returned to its pool once sent.
    /// </summary>
    protected ValueTask BroadcastFromLoopAsync(TUnion message, CancellationToken ct)
    {
        return _connectionManager.BroadcastAsync(message, null, ct);
    }

    protected ValueTask<bool> ProcessMessage(TUnion message)
    {
        return Processor.EnqueueWaitForResult(message, null);
    }
}

internal interface INexusBroadcastServerTestModifier
{
    internal bool DoNotSendAck { get; set; }
}
