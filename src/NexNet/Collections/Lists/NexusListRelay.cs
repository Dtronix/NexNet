using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
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
internal class NexusListRelay<T> : NexusBroadcastServer<INexusCollectionListMessage>, INexusList<T>, INexusListRelay
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
    public NexusListRelay(ushort id, NexusCollectionMode mode, INexusLogger? logger)
        : base(id, mode, logger)
    {
        _itemList = new VersionedList<T>(1024, logger);
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
                OnParentDisconnected();

                // Small delay before reconnection attempt
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
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

        OnParentDisconnected();
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

        switch (args.ChangedAction)
        {
            case NexusCollectionChangedAction.Ready:
                SyncFullStateFromParent();
                _state = NexusCollectionState.Connected;
                _readyTcs?.TrySetResult();
                _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                break;

            case NexusCollectionChangedAction.Reset:
                // Parent was cleared - clear our state and broadcast
                SyncFullStateFromParent();
                break;

            case NexusCollectionChangedAction.Add:
            case NexusCollectionChangedAction.Remove:
            case NexusCollectionChangedAction.Replace:
            case NexusCollectionChangedAction.Move:
                // Sync and broadcast individual changes
                SyncIncrementalChangeFromParent(args.ChangedAction);
                break;
        }
    }

    private void SyncFullStateFromParent()
    {
        if (_parentList == null)
            return;

        lock (_syncLock)
        {
            // Get the current parent state
            var parentItems = new List<T>();
            foreach (var item in _parentList)
            {
                parentItems.Add(item);
            }

            var newState = ImmutableList<T>.Empty.AddRange(parentItems);

            // Reset our local list to match
            _itemList.ResetTo(newState, 0);
            _lastKnownParentState = newState;

            Logger?.LogDebug($"Synced full state from parent: {parentItems.Count} items.");

            // Raise changed event
            using (var eventArgs = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Ready))
            {
                CoreChangedEvent.Raise(eventArgs.Value);
            }
        }
    }

    private void SyncIncrementalChangeFromParent(NexusCollectionChangedAction action)
    {
        if (_parentList == null)
            return;

        lock (_syncLock)
        {
            // Get the current parent state
            var parentItems = new List<T>();
            foreach (var item in _parentList)
            {
                parentItems.Add(item);
            }

            var currentParentState = ImmutableList<T>.Empty.AddRange(parentItems);
            var previousState = _lastKnownParentState;

            // Determine what changed and broadcast
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

                        // Broadcast to our clients
                        var message = NexusCollectionListInsertMessage.Rent();
                        message.Version = _itemList.Version;
                        message.Index = addedIndex;
                        message.Value = MemoryPackSerializer.Serialize(addedItem);
                        _ = ProcessMessage(message);
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

                            // Broadcast to our clients
                            var message = NexusCollectionListRemoveMessage.Rent();
                            message.Version = _itemList.Version;
                            message.Index = removedIndex;
                            _ = ProcessMessage(message);
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

                            // Broadcast to our clients
                            var message = NexusCollectionListReplaceMessage.Rent();
                            message.Version = _itemList.Version;
                            message.Index = replacedIndex;
                            message.Value = MemoryPackSerializer.Serialize(newValue);
                            _ = ProcessMessage(message);
                        }
                    }
                    break;

                case NexusCollectionChangedAction.Move:
                    // Move is complex - for now, do a full resync
                    // In a production implementation, we'd detect from/to indices
                    SyncFullStateFromParent();
                    return; // Skip the update below
            }

            // Update our tracking state
            _lastKnownParentState = currentParentState;

            // Raise changed event
            using (var eventArgs = NexusCollectionChangedEventArgs.Rent(action))
            {
                CoreChangedEvent.Raise(eventArgs.Value);
            }
        }
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

    private void OnParentDisconnected()
    {
        _state = NexusCollectionState.Disconnected;
        _itemList.Reset();
        _lastKnownParentState = ImmutableList<T>.Empty;
        _parentList = null;

        // Raise Reset event for local subscribers
        using (var args = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset))
        {
            CoreChangedEvent.Raise(args.Value);
        }

        _disconnectedTcs?.TrySetResult();
        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    #region INexusList<T> - Read Operations

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
    /// Called when a client connects - sends the current state.
    /// </summary>
    protected override void OnConnected(NexusBroadcastSession<INexusCollectionListMessage> client)
    {
        var state = _itemList.State;

        var reset = NexusCollectionListResetStartMessage.Rent();
        reset.Version = state.Version;
        reset.TotalValues = state.List.Count;
        client.BufferTryWrite(reset.Wrap());

        foreach (var values in ResetValuesEnumerator(state))
        {
            client.BufferTryWrite(values);
        }

        client.BufferTryWrite(NexusCollectionListResetCompleteMessage.Rent().Wrap());
    }

    private IEnumerable<INexusCollectionBroadcasterMessageWrapper<INexusCollectionListMessage>> ResetValuesEnumerator(
        VersionedList<T>.ListState state)
    {
        if (state.List.Count == 0)
            yield break;

        var bufferSize = Math.Min(state.List.Count, 40);

        foreach (var item in state.List.MemoryChunk(bufferSize))
        {
            var message = NexusCollectionListResetValuesMessage.Rent();
            message.Values = MemoryPackSerializer.Serialize(item);
            yield return message.Wrap();
        }
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

        // Internal messages (from parent sync) - broadcast to our connected clients
        Logger?.LogTrace($"Broadcasting message to relay clients: {message.GetType().Name}");
        return new ProcessResult(message, false);
    }

    #endregion
}
