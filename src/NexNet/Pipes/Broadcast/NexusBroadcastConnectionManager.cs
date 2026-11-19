using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Collections.Lists;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Manages connected clients and broadcasts messages to all active sessions.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be broadcast.</typeparam>
internal class NexusBroadcastConnectionManager<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private readonly INexusLogger? _logger;
    private readonly Channel<INexusCollectionBroadcasterMessageWrapper<TUnion>> _messageBroadcastChannel;
    
    private readonly object _runLock = new();

    // State of the latest Run. Each run has its own client list, so a stopped run that is still unwinding
    // cannot complete or clear clients that joined after a restart.
    private RunState? _run;
    
    internal bool DoNotSendAck = false; 
    public NexusBroadcastConnectionManager(INexusLogger? logger)
    {
        _logger = logger?.CreateLogger("Broadcast");
        _messageBroadcastChannel = Channel.CreateBounded<INexusCollectionBroadcasterMessageWrapper<TUnion>>(new BoundedChannelOptions(50)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsRunning => _run is { } run && !run.Token.IsCancellationRequested;

    /// <summary>
    /// Completes when the loop of the latest run has fully exited and its clients have been completed.
    /// </summary>
    internal Task LoopTask => _run?.Loop ?? Task.CompletedTask;

    /// <summary>
    /// Starts the client's writer and adds the client to the broadcast list at once. Broadcasts already queued
    /// can reach the client before anything the caller writes to its buffer afterwards. A server that sends a
    /// snapshot first uses <see cref="RegisterClientAsync"/>, then <see cref="StartClient"/> once the snapshot is
    /// written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The broadcaster is not running.</exception>
    public void AddClientAsync(INexusBroadcastSession<TUnion> client)
    {
        var run = StartClientCore(client);
        run.Clients.Add(client);
    }

    /// <summary>
    /// Queues the client's registration behind every broadcast already queued. The client receives only the
    /// broadcasts queued after this call. They collect in its buffer until its writer starts
    /// (<see cref="StartClient"/>), so a snapshot written to the client before that comes first. A client whose
    /// buffer fills up is closed.
    /// </summary>
    /// <param name="client">The client to register.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask RegisterClientAsync(INexusBroadcastSession<TUnion> client, CancellationToken ct = default)
    {
        return _messageBroadcastChannel.Writer.WriteAsync(new ClientControlItem(client, null), ct);
    }

    /// <summary>
    /// Queues a message for one client only, in order with the broadcasts: the client receives it after every
    /// broadcast queued before this call.
    /// </summary>
    /// <param name="message">The message to send. It is returned to its pool once sent.</param>
    /// <param name="client">The client to send the message to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask SendToClientAsync(TUnion message, INexusBroadcastSession<TUnion> client, CancellationToken ct = default)
    {
        var wrapper = message.Wrap();
        try
        {
            await _messageBroadcastChannel.Writer.WriteAsync(new ClientControlItem(client, wrapper), ct).ConfigureAwait(false);
        }
        catch
        {
            wrapper.SignalSent();
            throw;
        }
    }

    /// <summary>
    /// Starts the task that writes the client's buffer to the client. The client receives no broadcasts until it
    /// is registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">The broadcaster is not running.</exception>
    public void StartClient(INexusBroadcastSession<TUnion> client) => StartClientCore(client);

    private RunState StartClientCore(INexusBroadcastSession<TUnion> client)
    {
        var run = _run;
        if(run == null || run.Stopped)
            throw new InvalidOperationException("Broadcaster is not running.");
        
        // Add in the completion removal for execution later.
        _logger?.LogTrace($"S{client.Id} Starting collection message writer.");
        
        // Start the internal broadcast listener for this client to handle sending updates to the client.
        Task.Factory.StartNew(static async state =>
        {
            var client = (INexusBroadcastSession<TUnion>)(state!);
            INexusCollectionBroadcasterMessageWrapper<TUnion>? wrapper = null;
            await foreach (var messageWrapper in client.BufferRead(client.CompletionToken).ConfigureAwait(false))
            {
                try
                {
                    wrapper = messageWrapper;
                    var message = messageWrapper.SourceClient == client
                        ? messageWrapper.MessageToSource ?? throw new Exception("Message to source client is null.")
                        : messageWrapper.Message;

                    if (message == null)
                        throw new Exception("Message is null.");
                    
                    client.Logger?.LogTrace($"Sending {message.GetType().Name} to client.");

                    if (!await client.SendAsync(message, client.CompletionToken).ConfigureAwait(false))
                    {
                        client.Logger?.LogInfo("Cound not send client message.");
                        await client.CompletePipe().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception e)
                {
                    client.Logger?.LogInfo(e, "Could not send collection broadcast message to session.");
                    // Ignore and disconnect.
                    try
                    {
                        await client.CompletePipe().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        client.Logger?.LogInfo(ex, "Exception while completing pipe.");
                    }

                    return;

                }
                finally
                {
                    wrapper?.SignalSent();
                }
            }

        }, client, TaskCreationOptions.DenyChildAttach);

        return run;
    }

    public async ValueTask BroadcastAsync(TUnion message, INexusBroadcastSession<TUnion>? sourceClient, CancellationToken ct = default)
    {
        // DoNotSendAck is for testing logic only and not used in any production.
        // Using WriteAsync provides backpressure - if the channel is full, we wait instead of dropping messages.
        await _messageBroadcastChannel.Writer.WriteAsync(message.Wrap(DoNotSendAck ? null : sourceClient), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the broadcast loop. A previous run that was cancelled but is still unwinding does not block this
    /// call: the new loop waits for it to exit before it reads the channel.
    /// </summary>
    /// <exception cref="InvalidOperationException">A previous run has not been cancelled.</exception>
    public void Run(CancellationToken token)
    {
        lock (_runLock)
        {
            var previous = _run;
            if (previous != null && !previous.Token.IsCancellationRequested)
                throw new InvalidOperationException("Broadcaster is already running.");

            var run = new RunState(token);
            _run = run;
            run.Loop = Task.Run(() => RunLoop(this, run, previous?.Loop));
        }
    }

    private static async Task RunLoop(
        NexusBroadcastConnectionManager<TUnion> broadcaster,
        RunState run,
        Task? previousLoop)
    {
        var ct = run.Token;
        var connectedClients = run.Clients;

        // The channel has a single reader; wait for the stopped run to let go of it.
        if (previousLoop != null)
            await previousLoop.ConfigureAwait(false);

        broadcaster._logger?.LogTrace("Started broadcast loop.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var broadcastMessage in broadcaster._messageBroadcastChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (broadcastMessage is ClientControlItem control)
                    {
                        await HandleControlItem(broadcaster, control, connectedClients).ConfigureAwait(false);
                        continue;
                    }

                    // Update the count.
                    broadcastMessage.ClientCount = connectedClients.Count;
                    
                    // Ensure we actually have any clients.
                    if (broadcastMessage.ClientCount == 0)
                    {
                        // Force the client count to 1 and signal a return. 
                        broadcastMessage.ClientCount = 1;
                        broadcastMessage.SignalSent();
                        broadcaster._logger?.LogTrace($"Broadcasting skipping {broadcastMessage.Message?.GetType()} due to no client connections.");
                        continue;
                    }
                    
                    broadcaster._logger?.LogTrace($"Broadcasting {broadcastMessage.Message?.GetType()}");

                    foreach (var client in connectedClients)
                    {
                        try
                        {
                            if (!client.BufferTryWrite(broadcastMessage))
                            {
                                broadcaster._logger?.LogInfo(
                                    $"S{client.Id} Collection client buffer is full: the client is not keeping up with the changes. Closing its pipe.");
                                // Complete the pipe as it is full and not writing to the client at a decent
                                // rate.
                                await client.CompletePipe().ConfigureAwait(false);
                                // Signal completion for this client since message won't be processed
                                broadcastMessage.SignalSent();
                            }
                            else
                            {
                                broadcaster._logger?.LogTrace(
                                    $"S{client.Id} Sent {broadcastMessage.Message?.GetType()} to client collection");
                            }
                        }
                        catch (Exception e)
                        {
                            broadcaster._logger?.LogInfo(e,
                                $"S{client.Id} Exception while sending to collection. Removing from broadcast.");
                            
                            // If we threw, the client is disconnected. Remove the client and signal completion.
                            connectedClients.Remove(client);
                            broadcastMessage.SignalSent();
                        }

                    }
                }
                broadcaster._logger?.LogDebug("Exited broadcast reading loop.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected on stop.
                broadcaster._logger?.LogDebug("Broadcast loop cancelled.");
            }
            catch (Exception e)
            {
                broadcaster._logger?.LogError(e, "Exception in broadcast loop");
            }
        }
        
        run.Stopped = true;

        // Close the client connections.
        foreach (var client in connectedClients)
        {
            try
            {
                await client.CompletePipe().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                broadcaster._logger?.LogWarning(e, $"S{client.Id} Could not complete pipe.");
            }
        }
        
        connectedClients.Clear();
    }

    private static async ValueTask HandleControlItem(
        NexusBroadcastConnectionManager<TUnion> broadcaster,
        ClientControlItem control,
        SnapshotList<INexusBroadcastSession<TUnion>> connectedClients)
    {
        var client = control.Client;

        // Registration: every broadcast queued before it was skipped for this client, every one after it follows.
        if (control.Message == null)
        {
            if (client.CompletionToken.IsCancellationRequested)
            {
                broadcaster._logger?.LogTrace($"S{client.Id} Skipping registration of a closed client.");
                return;
            }

            connectedClients.Add(client);
            broadcaster._logger?.LogTrace($"S{client.Id} Registered for broadcasts.");
            return;
        }

        // Message for one client.
        try
        {
            if (client.BufferTryWrite(control.Message))
                return;

            broadcaster._logger?.LogInfo(
                $"S{client.Id} Collection client buffer is full: the client is not keeping up with the changes. Closing its pipe.");
            // The client's buffer is full: it is not keeping up, so close its pipe as a broadcast would.
            control.Message.SignalSent();
            await client.CompletePipe().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            broadcaster._logger?.LogInfo(e, $"S{client.Id} Exception while sending to collection.");
            control.Message.SignalSent();
        }
    }

    /// <summary>
    /// An item in the broadcast channel that targets one client: its registration (no message), or a message for
    /// that client only. It goes through the channel so it is ordered with the broadcasts.
    /// </summary>
    private sealed class ClientControlItem : INexusCollectionBroadcasterMessageWrapper<TUnion>
    {
        public readonly INexusBroadcastSession<TUnion> Client;
        public readonly INexusCollectionBroadcasterMessageWrapper<TUnion>? Message;

        public ClientControlItem(INexusBroadcastSession<TUnion> client,
            INexusCollectionBroadcasterMessageWrapper<TUnion>? message)
        {
            Client = client;
            Message = message;
        }

        INexusBroadcastSession<TUnion>? INexusCollectionBroadcasterMessageWrapper<TUnion>.SourceClient => null;
        TUnion? INexusCollectionBroadcasterMessageWrapper<TUnion>.MessageToSource => default;
        TUnion? INexusCollectionBroadcasterMessageWrapper<TUnion>.Message => default;
        public int ClientCount { get; set; }

        public void SignalSent()
        {
            // Not sent itself: the wrapped message signals on its own.
        }
    }

    private sealed class RunState
    {
        public readonly CancellationToken Token;
        public readonly SnapshotList<INexusBroadcastSession<TUnion>> Clients = new(64);
        public Task Loop = Task.CompletedTask;
        public volatile bool Stopped;

        public RunState(CancellationToken token) => Token = token;
    }
}

/// <summary>
/// Wraps a broadcast message with metadata for tracking delivery to multiple clients.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be wrapped.</typeparam>
internal class NexusCollectionBroadcasterMessageWrapper<TUnion> : INexusCollectionBroadcasterMessageWrapper<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{    
    private static readonly ConcurrentBag<NexusCollectionBroadcasterMessageWrapper<TUnion>> _pool = new ();
    private int _completedCount;
    public int ClientCount { get; set; }
    public INexusBroadcastSession<TUnion>? SourceClient { get; set; }

    /// <summary>
    /// Message for the source client. Usually includes as Ack.
    /// </summary>
    public TUnion? MessageToSource { get; private set; }

    public TUnion? Message { get; private set; }

    private NexusCollectionBroadcasterMessageWrapper()
    {
        
    }

    public void SignalSent()
    {
        if (Interlocked.Increment(ref _completedCount) != ClientCount)
            return;

        MessageToSource?.Return();
        MessageToSource = default;
        
        Message!.Return();
        Message = default;
        
        _pool.Add(this);
    }

    public static INexusCollectionBroadcasterMessageWrapper<TUnion> Rent<TMessage>(
        TMessage message,
        INexusBroadcastSession<TUnion>? sourceClient) 
        where TMessage : TUnion, INexusCollectionUnion<TUnion>, new()
    {
        if (!_pool.TryTake(out var wrapper))
            wrapper = new NexusCollectionBroadcasterMessageWrapper<TUnion>();

        wrapper.Message = message;
        wrapper.SourceClient = sourceClient;
        wrapper._completedCount = 0;
        wrapper.ClientCount = 1;
        if (sourceClient != null)
        {
            wrapper.MessageToSource = message.Clone();
            wrapper.MessageToSource.Flags |= NexusCollectionMessageFlags.Ack;
        }
        
        return wrapper;
    }
}

/// <summary>
/// Interface for wrapping broadcast messages with delivery tracking metadata.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types.</typeparam>
internal interface INexusCollectionBroadcasterMessageWrapper<TUnion>
    where TUnion : INexusCollectionUnion<TUnion>
{
    /// <summary>
    /// Gets the client that originated the message, or null for server-originated messages.
    /// </summary>
    INexusBroadcastSession<TUnion>? SourceClient { get; }

    /// <summary>
    /// Gets the acknowledgment message sent to the source client, or null if no acknowledgment is needed.
    /// </summary>
    TUnion? MessageToSource { get; }

    /// <summary>
    /// Gets the message to broadcast to all clients.
    /// </summary>
    TUnion? Message { get; }

    /// <summary>
    /// Gets or sets the number of clients that will receive this message.
    /// </summary>
    public int ClientCount { get; set; }

    /// <summary>
    /// Signals that the message was sent to one client. Releases resources when all clients have received it.
    /// </summary>
    void SignalSent();
}

