using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections.Lists;

/// <summary>
/// Interface for relay collections that enables type-agnostic lifecycle management.
/// </summary>
internal interface INexusListRelay
{
    /// <summary>
    /// Starts the relay connection to the parent collection.
    /// </summary>
    void StartRelay();

    /// <summary>
    /// Stops the relay connection to the parent collection.
    /// </summary>
    void StopRelay();
}

/// <summary>
/// A relay collection that receives changes from a parent collection and broadcasts them to its own connected clients.
/// This collection is read-only and does not accept modifications from clients.
/// </summary>
/// <typeparam name="T">The type of elements in the list.</typeparam>
internal class NexusListRelay<T> : NexusBroadcastServer<INexusCollectionListMessage>, INexusList<T>, INexusListRelay, INexusCollectionSnapshotSource
{
    private readonly VersionedList<T> _itemList;
    private INexusCollectionClientConnector? _parentConnector;
    private INexusList<T>? _parentList;
    private bool _isRelayConfigured;
    private CancellationTokenSource? _relayCts;
    private TaskCompletionSource? _readyTcs;
    private TaskCompletionSource? _disconnectedTcs;

    // For tracking parent state to compute diffs
    private ImmutableList<T> _lastKnownParentState = ImmutableList<T>.Empty;
    private readonly object _syncLock = new object();

    private NexusCollectionState _state = NexusCollectionState.Disconnected;
    private readonly TimeProvider _time;

    /// <inheritdoc />
    public int Count => _itemList.Count;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public NexusCollectionState State => _state;

    /// <inheritdoc />
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;

    /// <summary>
    /// Gets a task that completes when the relay is connected and ready.
    /// </summary>
    public Task ReadyTask => _readyTcs?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Gets a task that completes when the relay is disconnected.
    /// </summary>
    public Task DisconnectedTask => _disconnectedTcs?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Creates a new relay collection.
    /// </summary>
    /// <param name="id">The collection ID.</param>
    /// <param name="mode">The collection mode (should be Relay).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">Time source driving the reconnect-loop delay.</param>
    public NexusListRelay(ushort id, NexusCollectionMode mode, INexusLogger? logger, TimeProvider time)
        : base(id, mode, logger)
    {
        _itemList = new VersionedList<T>(1024, logger);
        _time = time;
    }

    /// <summary>
    /// Configures the relay to connect to a parent collection.
    /// </summary>
    /// <param name="connector">The connector to the parent collection.</param>
    /// <exception cref="InvalidOperationException">Thrown if the relay is already configured.</exception>
    public void ConfigureRelay(INexusCollectionClientConnector connector)
    {
        if (_isRelayConfigured)
            throw new InvalidOperationException("Relay is already configured.");

        _parentConnector = connector ?? throw new ArgumentNullException(nameof(connector));
        _isRelayConfigured = true;
    }

    /// <summary>
    /// Starts the relay connection to the parent collection.
    /// </summary>
    void INexusListRelay.StartRelay()
    {
        if (!_isRelayConfigured || _parentConnector == null)
        {
            Logger?.LogDebug("Relay not configured, skipping start.");
            return;
        }

        _relayCts = new CancellationTokenSource();
        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the auto-reconnection loop
        _ = Task.Factory.StartNew(async () =>
        {
            var ct = _relayCts.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Logger?.LogDebug("Attempting to connect to parent collection...");
                    await ConnectToParent(ct).ConfigureAwait(false);

                    // Wait for disconnection
                    if (_parentList != null)
                    {
                        await _parentList.DisabledTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex, "Error in relay connection loop");
                }

                // Clear data on disconnection
                await OnParentDisconnectedAsync().ConfigureAwait(false);

                // Small delay before reconnection attempt
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Stops the relay connection to the parent collection.
    /// </summary>
    void INexusListRelay.StopRelay()
    {
        _relayCts?.Cancel();
        _relayCts?.Dispose();
        _relayCts = null;

        // Runs on the processing loop while it is still running, or directly once the collection has stopped.
        _ = OnParentDisconnectedAsync();
    }

    private async Task ConnectToParent(CancellationToken ct)
    {
        var parentCollection = await _parentConnector!.GetCollection().ConfigureAwait(false);

        if (parentCollection is not INexusList<T> parentList)
            throw new InvalidOperationException("Parent collection is not of expected type INexusList<T>");

        _parentList = parentList;

        // Subscribe to changes
        parentList.Changed.Subscribe(OnParentChanged);

        // Enable the connection
        var enabled = await parentList.EnableAsync(ct).ConfigureAwait(false);

        if (enabled)
        {
            Logger?.LogDebug("Connected to parent collection.");
        }
        else
        {
            Logger?.LogWarning("Failed to enable parent collection connection.");
            throw new InvalidOperationException("Failed to enable parent collection connection.");
        }
    }

    private void OnParentChanged(NexusCollectionChangedEventArgs args)
    {
        Logger?.LogTrace($"Parent changed: {args.ChangedAction}");

        var parentList = _parentList;
        if (parentList == null)
            return;

        switch (args.ChangedAction)
        {
            case NexusCollectionChangedAction.Ready:
            case NexusCollectionChangedAction.Reset:
            case NexusCollectionChangedAction.Add:
            case NexusCollectionChangedAction.Remove:
            case NexusCollectionChangedAction.Replace:
            case NexusCollectionChangedAction.Move:
                break;
            default:
                return;
        }

        // Capture the parent state now, as of this change; the relay applies it on its processing loop.
        var parentState = ImmutableList.CreateRange(parentList);
        var action = args.ChangedAction;
        _ = ApplyOnLoopAsync(() => ApplyParentChange(action, parentState));
    }

    /// <summary>
    /// Runs <paramref name="apply"/> on the processing loop, then broadcasts what it returns: one change, or a reset
    /// sequence that brings every connected client to the relay's resynchronized list. A change is applied to the
    /// local list and broadcast without a client registering in between, so a connecting client gets each change
    /// exactly once: in its snapshot, or as a broadcast after it. When the loop is stopped, no client can connect:
    /// the change is applied locally and not broadcast.
    /// </summary>
    private async Task ApplyOnLoopAsync(Func<RelayBroadcast> apply)
    {
        var ran = false;
        try
        {
            await Processor.EnqueueActionWaitForResult(async ct =>
            {
                ran = true;
                var broadcast = Apply(apply);
                if (RelayChangeTestHook is { } hook)
                    await hook().ConfigureAwait(false);
                if (broadcast.Change != null)
                    await BroadcastFromLoopAsync(broadcast.Change, ct).ConfigureAwait(false);
                else if (broadcast.Resync is { } state)
                {
                    // The same reset sequence a connecting client gets as its snapshot, written to each client
                    // with the same backpressure. Each client already connected starts a new reset and replaces
                    // its list (protocol specification §12.4).
                    await ResetClientsFromLoopAsync(ResetStartMessage(state), () => ResetValueMessages(state), ct)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (!ran)
        {
            Logger?.LogDebug(e, "Relay processing loop is stopped; applying the parent change locally.");
        }
        catch (Exception e)
        {
            Logger?.LogWarning(e, "Could not apply a parent change to the relay.");
        }

        if (!ran)
            Apply(apply).Change?.Return();
    }

    private RelayBroadcast Apply(Func<RelayBroadcast> apply)
    {
        lock (_syncLock)
            return apply();
    }

    /// <summary>
    /// What a parent change sends to the relay's clients: one change message, a resync to the given list state
    /// (a reset sequence), or nothing.
    /// </summary>
    private readonly record struct RelayBroadcast(
        INexusCollectionListMessage? Change,
        VersionedList<T>.ListState? Resync = null);

    /// <summary>
    /// Test seam: runs on the processing loop after a parent change is applied locally and before it is broadcast.
    /// Always null outside tests.
    /// </summary>
    internal Func<Task>? RelayChangeTestHook { get; set; }

    /// <summary>
    /// Applies a parent change to the local list. Runs on the processing loop (or with the loop stopped).
    /// </summary>
    /// <returns>What to broadcast to the relay's clients.</returns>
    private RelayBroadcast ApplyParentChange(NexusCollectionChangedAction action, ImmutableList<T> parentState)
    {
        switch (action)
        {
            case NexusCollectionChangedAction.Ready:
                // Connected (or reconnected) to the parent: resync, and reset the clients already connected.
                SyncFullState(parentState);
                _state = NexusCollectionState.Connected;
                _readyTcs?.TrySetResult();
                _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return new RelayBroadcast(null, _itemList.State);

            case NexusCollectionChangedAction.Reset:
            case NexusCollectionChangedAction.Move:
                // Parent was cleared or resynchronized, or an item moved: resync the full state and reset the
                // clients already connected, as the clients of a NexusListServer are reset.
                SyncFullState(parentState);
                return new RelayBroadcast(null, _itemList.State);

            default:
                return new RelayBroadcast(SyncIncrementalChange(action, parentState));
        }
    }

    private void SyncFullState(ImmutableList<T> parentState)
    {
        // Reset our local list to match
        _itemList.ResetTo(parentState, 0);
        _lastKnownParentState = parentState;

        Logger?.LogDebug($"Synced full state from parent: {parentState.Count} items.");

        // Raise changed event
        using (var eventArgs = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Ready))
        {
            CoreChangedEvent.Raise(eventArgs.Value);
        }
    }

    private INexusCollectionListMessage? SyncIncrementalChange(NexusCollectionChangedAction action, ImmutableList<T> currentParentState)
    {
        var previousState = _lastKnownParentState;
        INexusCollectionListMessage? message = null;

        // Determine what changed
        switch (action)
        {
            case NexusCollectionChangedAction.Add:
                // Item was added - find the new item
                if (currentParentState.Count > previousState.Count)
                {
                    // Find the added item (typically at the end for Add)
                    var addedIndex = currentParentState.Count - 1;
                    var addedItem = currentParentState[addedIndex];

                    // Apply to our local list
                    var insertOp = InsertOperation<T>.Rent();
                    insertOp.Index = addedIndex;
                    insertOp.Item = addedItem;
                    _itemList.ApplyOperation(insertOp, _itemList.Version);
                    insertOp.Return();

                    var insert = NexusCollectionListInsertMessage.Rent();
                    insert.Version = _itemList.Version;
                    insert.Index = addedIndex;
                    insert.Value = PayloadSerializer.Serialize(addedItem);
                    message = insert;
                }
                break;

            case NexusCollectionChangedAction.Remove:
                // Item was removed - find the removed index
                if (currentParentState.Count < previousState.Count)
                {
                    // Find the removed index by comparing lists
                    var removedIndex = FindRemovedIndex(previousState, currentParentState);
                    if (removedIndex >= 0)
                    {
                        // Apply to our local list
                        var removeOp = RemoveOperation<T>.Rent();
                        removeOp.Index = removedIndex;
                        _itemList.ApplyOperation(removeOp, _itemList.Version);
                        removeOp.Return();

                        var remove = NexusCollectionListRemoveMessage.Rent();
                        remove.Version = _itemList.Version;
                        remove.Index = removedIndex;
                        message = remove;
                    }
                }
                break;

            case NexusCollectionChangedAction.Replace:
                // Item was replaced - find the replaced index
                if (currentParentState.Count == previousState.Count)
                {
                    var replacedIndex = FindReplacedIndex(previousState, currentParentState);
                    if (replacedIndex >= 0)
                    {
                        var newValue = currentParentState[replacedIndex];

                        // Apply to our local list
                        var modifyOp = ModifyOperation<T>.Rent();
                        modifyOp.Index = replacedIndex;
                        modifyOp.Value = newValue;
                        _itemList.ApplyOperation(modifyOp, _itemList.Version);
                        modifyOp.Return();

                        var replace = NexusCollectionListReplaceMessage.Rent();
                        replace.Version = _itemList.Version;
                        replace.Index = replacedIndex;
                        replace.Value = PayloadSerializer.Serialize(newValue);
                        message = replace;
                    }
                }
                break;
        }

        // Update our tracking state
        _lastKnownParentState = currentParentState;

        // Raise changed event
        using (var eventArgs = NexusCollectionChangedEventArgs.Rent(action))
        {
            CoreChangedEvent.Raise(eventArgs.Value);
        }

        return message;
    }

    private static int FindRemovedIndex(ImmutableList<T> previous, ImmutableList<T> current)
    {
        var minCount = Math.Min(previous.Count, current.Count);
        for (var i = 0; i < minCount; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(previous[i], current[i]))
            {
                return i;
            }
        }
        // If we got here, the item was at the end
        return previous.Count - 1;
    }

    private static int FindReplacedIndex(ImmutableList<T> previous, ImmutableList<T> current)
    {
        for (var i = 0; i < previous.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(previous[i], current[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Clears the local state after the parent connection is lost, on the processing loop after the changes queued
    /// before it.
    /// </summary>
    private Task OnParentDisconnectedAsync()
    {
        _parentList = null;
        return ApplyOnLoopAsync(ApplyParentDisconnected);
    }

    private RelayBroadcast ApplyParentDisconnected()
    {
        // Track if we were previously connected - used to determine TCS handling
        var wasConnected = _state == NexusCollectionState.Connected;

        _state = NexusCollectionState.Disconnected;
        _itemList.Reset();
        _lastKnownParentState = ImmutableList<T>.Empty;

        // Raise Reset event for local subscribers
        using (var args = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset))
        {
            CoreChangedEvent.Raise(args.Value);
        }

        _disconnectedTcs?.TrySetResult();

        // Only create a new ReadyTask TCS if we were previously connected (reconnect scenario).
        // For first connection failures, keep the same TCS so that callers waiting on ReadyTask
        // will be notified when the first successful connection occurs.
        // This fixes a race condition where the test could read ReadyTask before the async loop
        // creates a new TCS, causing the test to wait on a TCS that will never complete.
        if (wasConnected)
        {
            _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Connected clients keep the last state until the parent is back; the Ready resync then resets them.
        return default;
    }

    #region INexusList<T> - Read Operations

    NexusCollectionSnapshot INexusCollectionSnapshotSource.GetSnapshot(INexusSession session)
    {
        // One read of the immutable state, so the count and version belong together.
        var state = Volatile.Read(ref _itemList.State);
        return CreateSnapshot(session, NexusCollectionKind.List, state.List.Count, state.Version);
    }

    /// <inheritdoc />
    public bool Contains(T item) => _itemList.Contains(item);

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public int IndexOf(T item) => _itemList.IndexOf(item);

    /// <inheritdoc />
    public T this[int index] => _itemList[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _itemList.State.List.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _itemList.State.List.GetEnumerator();

    #endregion

    #region INexusList<T> - Write Operations (Read-Only - All Throw)

    private static readonly InvalidOperationException ReadOnlyException =
        new InvalidOperationException("Relay collections are read-only.");

    /// <inheritdoc />
    public Task<bool> AddAsync(T item) => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> ClearAsync() => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> InsertAsync(int index, T item) => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> MoveAsync(int fromIndex, int toIndex) => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> RemoveAsync(T item) => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> RemoveAtAsync(int index) => throw ReadOnlyException;

    /// <inheritdoc />
    public Task<bool> ReplaceAsync(int index, T value) => throw ReadOnlyException;

    #endregion

    #region INexusList<T> - Connection Methods

    /// <inheritdoc />
    public ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        // Server-side relay is always enabled
        return new ValueTask<bool>(true);
    }

    /// <inheritdoc />
    public ValueTask DisableAsync()
    {
        // Server-side relay cannot be disabled from client requests
        return default;
    }

    /// <inheritdoc />
    public Task DisabledTask => _disconnectedTcs?.Task ?? Task.CompletedTask;

    #endregion

    #region NexusBroadcastServer Overrides

    /// <summary>
    /// Runs on the processing loop when a client connects: captures the current state and returns the messages that
    /// bring the client to it. The base class writes them with backpressure, so a large relayed list reaches a slow
    /// client whole instead of overflowing its buffer.
    /// </summary>
    protected override IEnumerable<INexusCollectionListMessage> CaptureSnapshot()
    {
        // Captured here, on the processing loop; the messages are built as they are written.
        var state = _itemList.State;
        return SnapshotMessages(state);
    }

    private static IEnumerable<INexusCollectionListMessage> SnapshotMessages(VersionedList<T>.ListState state)
    {
        yield return ResetStartMessage(state);

        foreach (var message in ResetValueMessages(state))
            yield return message;
    }

    private static INexusCollectionListMessage ResetStartMessage(VersionedList<T>.ListState state)
    {
        var reset = NexusCollectionListResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = state.List.Count;
        return reset;
    }

    /// <summary>
    /// The messages of a reset after its start: the values, then the reset complete. Each one is rented as it is
    /// enumerated.
    /// </summary>
    private static IEnumerable<INexusCollectionListMessage> ResetValueMessages(VersionedList<T>.ListState state)
    {
        if (state.List.Count > 0)
        {
            var bufferSize = Math.Min(state.List.Count, 40);
            foreach (var item in state.List.MemoryChunk(bufferSize))
            {
                var message = NexusCollectionListResetValuesMessage.Rent();
                message.Values = PayloadSerializer.SerializeValues(item);
                yield return message;
            }
        }

        yield return NexusCollectionListResetCompleteMessage.Rent();
    }

    /// <summary>
    /// Processes incoming messages from connected clients.
    /// For a relay, we reject all client modifications but broadcast internal messages from the parent.
    /// </summary>
    protected override ProcessResult OnProcess(INexusCollectionListMessage message,
        INexusBroadcastSession<INexusCollectionListMessage>? sourceClient,
        CancellationToken ct)
    {
        // Relay collections are read-only - reject all client modifications
        if (sourceClient != null)
        {
            Logger?.LogDebug("Rejecting client modification attempt on relay collection.");
            return new ProcessResult(null, true); // Disconnect client trying to modify
        }

        // Parent changes are applied and broadcast by actions on the processing loop, never queued as messages.
        Logger?.LogWarning($"Unexpected internal message on relay collection: {message.GetType().Name}");
        message.Return();
        return new ProcessResult(null, false);
    }

    #endregion
}
