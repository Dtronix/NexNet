using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Collections.Versioned;
using NexNet.Internals.Threading;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;

using NexNet.Internals;

namespace NexNet.Collections.Lists;

internal class NexusListClient<T> : NexusBroadcastClient<INexusCollectionListMessage>, INexusList<T>
{
    private readonly VersionedList<T> _itemList;
    private List<T>? _resettingList;
    private List<INexusCollectionListMessage>? _resettingMessageBuffer;
    private int _resettingListVersion;
    private int _resettingTotalValues;

    // True once the first reset of this pipe has completed. Changes that arrive before it are buffered.
    private bool _synchronized;

    /// <summary>
    /// Testing only. Counts the changes received before or during a reset. The server orders a new client's
    /// snapshot before any change, so this stays zero against a NexNet server.
    /// </summary>
    internal int TestChangesReceivedBeforeSnapshot;
    private PooledResettableValueTaskCompletionSource<bool> _operationCompletionSource;
    
    public int Count => _itemList.Count;
    /// <summary>
    /// True when the client may not change the list: any mode but BiDirectional (ServerToClient and Relay), whose writes
    /// <see cref="ThrowIfReadOnly"/> refuses.
    /// </summary>
    public bool IsReadOnly => Mode != NexusCollectionMode.BiDirectional;
    
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed => CoreChangedEvent;
    
    public NexusListClient(ushort id, NexusCollectionMode mode, INexusLogger? logger)
        : base(id, mode, logger)
    {
        _itemList = new(1024, logger);
        _operationCompletionSource = PooledResettableValueTaskCompletionSource<bool>.Rent();
    }

    protected override void OnDisconnected()
    {
        _itemList.Reset();

        // Drop any partial reset so the next pipe starts clean.
        _resettingList = null;
        _resettingMessageBuffer = null;
        _resettingListVersion = -1;
        _resettingTotalValues = 0;
        _synchronized = false;

        // Ensure any ongoing operations fail.
        _operationCompletionSource.TrySetResult(false);
    }

    protected override BroadcastMessageProcessResult OnProcess(
        INexusCollectionListMessage message,
        INexusBroadcastSession<INexusCollectionListMessage>? sourceClient,
        CancellationToken ct)
    {
        switch (message)
        {
            case NexusCollectionListResetStartMessage startMessage:
                if (_resettingList != null)
                {
                    Client?.Logger?.LogWarning("Received start message while already resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }
                
                if (startMessage.TotalValues < 0)
                {
                    Client?.Logger?.LogWarning($"Received start message with invalid total value count {startMessage.TotalValues}.");
                    return new BroadcastMessageProcessResult(false, true);
                }

                // The count comes from the wire: cap the initial capacity, the list grows as values arrive.
                _resettingList = new List<T>(Math.Min(startMessage.TotalValues, 1024));
                _resettingTotalValues = startMessage.TotalValues;
                // Keep changes that were buffered before the reset started; they are replayed after it.
                _resettingMessageBuffer ??= new List<INexusCollectionListMessage>();
                _resettingListVersion = startMessage.Version;
                return new BroadcastMessageProcessResult(true, false);
            
            case NexusCollectionListResetCompleteMessage:
                if (_resettingList == null)
                {
                    Client?.Logger?.LogWarning("Received complete message while not resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }

                if (_resettingList.Count != _resettingTotalValues)
                {
                    Client?.Logger?.LogWarning(
                        $"Reset completed with {_resettingList.Count} values, but {_resettingTotalValues} were announced.");
                    return new BroadcastMessageProcessResult(false, true);
                }

                if (!ProcessClientResetCompleted(ct))
                    return new BroadcastMessageProcessResult(false, true);

                // Raise Reset event to indicate the collection has been synchronized
                using (var args = NexusCollectionChangedEventArgs.Rent(NexusCollectionChangedAction.Reset))
                {
                    CoreChangedEvent.Raise(args.Value);
                }

                InitializationCompleted(ct);
                return new BroadcastMessageProcessResult(true, false);

            case NexusCollectionListResetValuesMessage valuesMessage:
                if (_resettingList == null)
                {
                    Client?.Logger?.LogWarning("Received values message while not resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }
                
                var values = valuesMessage.DeserializeValue<T[]>();
                if (values != null)
                {
                    if (values.Length > _resettingTotalValues - _resettingList.Count)
                    {
                        Client?.Logger?.LogWarning(
                            $"Received more reset values than the {_resettingTotalValues} announced.");
                        return new BroadcastMessageProcessResult(false, true);
                    }

                    _resettingList.AddRange(values);
                }

                return new BroadcastMessageProcessResult(true, false);
        }

        if (_resettingList != null || !_synchronized)
        {
            // Resetting, or the reset has not started yet. Buffer changes and replay them after the reset completes,
            // where the stale rule drops the ones the snapshot already holds.
            Interlocked.Increment(ref TestChangesReceivedBeforeSnapshot);
            (_resettingMessageBuffer ??= new List<INexusCollectionListMessage>()).Add(message);
            return new BroadcastMessageProcessResult(true, false);
        }

        if (message is NexusCollectionListNoopMessage)
        {
            // The server made the pending request obsolete: it completes with false. A Noop with no request
            // pending is ignored (the completion source is already completed).
            _operationCompletionSource.TrySetResult(false);
            return new BroadcastMessageProcessResult(true, false);
        }
        
        var (op, version) = NexusListTransformers<T>.RentOperation(message);

        // Unknown operation
        if (op == null)
        {
            Client?.Logger?.LogWarning($"{message} did not match any known operations.");
            return new BroadcastMessageProcessResult(false, true);
        }

        // Discard stale operations already included in a reset snapshot.
        if (version > 0 && version <= _itemList.Version)
        {
            op.Return();
            return new BroadcastMessageProcessResult(true, false);
        }

        var result = _itemList.ApplyOperation(op, version);

        if (result != ListProcessResult.DiscardOperation && result != ListProcessResult.Successful)
        {
            Client?.Logger?.LogWarning($"Processing {message} message failed with {result}");
            if ((message.Flags & NexusCollectionMessageFlags.Ack) != 0)
            {
                _operationCompletionSource.TrySetResult(false);
            }
            return new BroadcastMessageProcessResult(false, true);
        }

        using (var args = NexusCollectionChangedEventArgs.Rent(NexusListTransformers<T>.GetAction(message)))
        {
            CoreChangedEvent.Raise(args.Value);
        }

        if ((message.Flags & NexusCollectionMessageFlags.Ack) != 0)
        {
            _operationCompletionSource.TrySetResult(true);
        }

        op.Return();

        return new BroadcastMessageProcessResult(true, false);
    }
    private bool ProcessClientResetCompleted(CancellationToken ct = default)
    {
        if (_resettingList == null)
        {
            Client?.Logger?.LogWarning("Received values message while not resetting.");
            return false;
        }

        var messageBuffer = _resettingMessageBuffer;
        if(messageBuffer == null)
            throw new InvalidOperationException("Message buffer was not set while resetting.");
        
        var list = ImmutableList<T>.Empty.AddRange(_resettingList!);

        // Reset the state manually.
        _itemList.ResetTo(list, _resettingListVersion);
        
        _resettingList!.Clear();
        _resettingList.Capacity = 0;
        _resettingList = null;
        _resettingListVersion = -1;
        _resettingTotalValues = 0;
        _resettingMessageBuffer = null;
        _synchronized = true;

        var bufferedCount = messageBuffer.Count;
        if (bufferedCount > 0)
        {
            Client?.Logger?.LogDebug($"Processing {bufferedCount} buffered messages.");
            // Process any buffered Messages.
            foreach (var bufferedMessage in messageBuffer)
            {
                var result = OnProcess(bufferedMessage, null, ct);

                if (result.Disconnect)
                {
                    Client?.Logger?.LogDebug($"Processing {bufferedMessage} buffered message failed and disconnected.");
                    return false;
                }
            }
        }

        messageBuffer.Clear();
        messageBuffer.Capacity = 0;
        
        return true;
    }
    
    private async ValueTask<bool> ProcessMessage(INexusCollectionListMessage message)
    {
        if(Client == null)
            throw new InvalidOperationException("Client not connected");
        
        _operationCompletionSource.Reset();
        
        await Client.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
        message.Return();
        
        return await _operationCompletionSource.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a change on a list the client may not change (any mode but BiDirectional) before anything else is
    /// checked, so the refusal is the same whether or not the list is enabled or the item exists. Java and JavaScript
    /// refuse the same way.
    /// </summary>
    private void ThrowIfReadOnly()
    {
        if (Mode != NexusCollectionMode.BiDirectional)
            throw new InvalidOperationException(
                $"The collection is read-only (mode {Mode}): only a BiDirectional collection accepts changes from a client.");
    }

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _itemList.IndexOf(item);

    public async Task<bool> RemoveAsync(T item)
    {
        ThrowIfReadOnly();
        var index = _itemList.IndexOf(item, out var version);

        if (index == -1)
            return false;

        var message = NexusCollectionListRemoveMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        
        
        
        message.Version = version;
        message.Index = index;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> ClearAsync()
    {
        ThrowIfReadOnly();
        var message = NexusCollectionListClearMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> InsertAsync(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ThrowIfReadOnly();

        var message = NexusCollectionListInsertMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = PayloadSerializer.Serialize(item);

        return await ProcessMessage(message).ConfigureAwait(false);

    }

    public async Task<bool> MoveAsync(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ThrowIfReadOnly();

        var message = NexusCollectionListMoveMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.FromIndex = fromIndex;
        message.ToIndex = toIndex;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> ReplaceAsync(int index, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ThrowIfReadOnly();

        var message = NexusCollectionListReplaceMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = PayloadSerializer.Serialize(value);

        return await ProcessMessage(message).ConfigureAwait(false);
   
    }

    public async Task<bool> RemoveAtAsync(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ThrowIfReadOnly();

        var message = NexusCollectionListRemoveMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> AddAsync(T item)
    {
        ThrowIfReadOnly();
        var message = NexusCollectionListInsertMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        var state = _itemList.State;
        message.Version = state.Version;
        message.Index = state.List.Count;
        message.Value = PayloadSerializer.Serialize(item);

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public T this[int index] => _itemList[index];

    public IEnumerator<T> GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _itemList.State.List.GetEnumerator();
    }
}
