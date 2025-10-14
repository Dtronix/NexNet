using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Versioned;
using NexNet.Internals.Threading;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections.Lists;

internal class NexusListClient<T> : NexusBroadcastClient<INexusCollectionListMessage>, INexusList<T>
{
    private readonly VersionedList<T> _itemList;
    private List<T>? _resettingList = null;
    private List<INexusCollectionListMessage>? _resettingMessageBuffer = null;
    private int _resettingListVersion;
    private PooledResettableValueTaskCompletionSource<bool> _operationCompletionSource;
    
    public int Count => _itemList.Count;
    public bool IsReadOnly { get; } = false;
    
    public NexusCollectionState State { get; }
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
                    Client.Logger?.LogWarning("Received start message while already resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }
                
                _resettingList = new List<T>(startMessage.TotalValues);
                _resettingMessageBuffer = new List<INexusCollectionListMessage>();
                _resettingListVersion = startMessage.Version;
                return new BroadcastMessageProcessResult(true, false);
            
            case NexusCollectionListResetCompleteMessage:
                if (_resettingList == null)
                {
                    Client.Logger?.LogWarning("Received values message while not resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }
                var completeResult = ProcessClientResetCompleted();

                InitializationCompleted();
                return new BroadcastMessageProcessResult(false, !completeResult);

            case NexusCollectionListResetValuesMessage valuesMessage:
                if (_resettingList == null)
                {
                    Client.Logger?.LogWarning("Received values message while not resetting.");
                    return new BroadcastMessageProcessResult(false, true);
                }
                
                var values = MemoryPackSerializer.Deserialize<T[]>(valuesMessage.Values.Span);
                if(values != null)
                    _resettingList!.AddRange(values);
                
                return new BroadcastMessageProcessResult(true, false);
        }

        if (_resettingList != null)
        {
            // Resetting. Buffer messages for after initialization has been completed. Buffer and process later.
            _resettingMessageBuffer!.Add(message);
            return new BroadcastMessageProcessResult(true, false);
        }
        
        var (op, version) = NexusListTransformers<T>.RentOperation(message);
        
        // Unknown operation
        if (op == null)
        {
            Client.Logger?.LogWarning($"{message} did not match any known operations.");
            return new BroadcastMessageProcessResult(false, true);
        }

        var result = _itemList.ApplyOperation(op, version);
        
        if (result != ListProcessResult.DiscardOperation && result != ListProcessResult.Successful)
        {
            Client.Logger?.LogWarning($"Processing {message} message failed with {result}");
            if (message.Flags.HasFlag(NexusCollectionMessageFlags.Ack))
            {
                _operationCompletionSource.TrySetResult(false);
            }
            return new BroadcastMessageProcessResult(false, true);
        }

        using (var args = NexusCollectionChangedEventArgs.Rent(NexusListTransformers<T>.GetAction(message)))
        {
            CoreChangedEvent.Raise(args.Value);
        }

        if (message.Flags.HasFlag(NexusCollectionMessageFlags.Ack))
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
            Client.Logger?.LogWarning("Received values message while not resetting.");
            return false;
        }
        
        var list = ImmutableList<T>.Empty.AddRange(_resettingList!);

        // Reset the state manually.
        _itemList.ResetTo(list, _resettingListVersion);
        
        _resettingList!.Clear();
        _resettingList.Capacity = 0;
        _resettingList = null;
        _resettingListVersion = -1;

        var bufferedCount = _resettingMessageBuffer?.Count ?? 0;
        if (bufferedCount > 0)
        {
            Client.Logger?.LogDebug($"Processing {bufferedCount} buffered messages.");
            // Process any buffered Messages.
            foreach (var bufferedMessage in _resettingMessageBuffer!)
            {
                var result = OnProcess(bufferedMessage, null, ct);

                if (result.Disconnect)
                {
                    Client.Logger?.LogDebug($"Processing {bufferedMessage} buffered message failed and disconnected.");
                    return false;
                }
            }
        }

        _resettingMessageBuffer.Clear();
        _resettingMessageBuffer.Capacity = 0;
        _resettingMessageBuffer = null;
        
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

    public bool Contains(T item) => _itemList.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _itemList.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _itemList.IndexOf(item);

    public async Task<bool> RemoveAsync(T item)
    {
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
        var message = NexusCollectionListClearMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> InsertAsync(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var message = NexusCollectionListInsertMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = MemoryPackSerializer.Serialize(item);

        return await ProcessMessage(message).ConfigureAwait(false);

    }

    public async Task<bool> MoveAsync(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);

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

        var message = NexusCollectionListReplaceMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;
        message.Value = MemoryPackSerializer.Serialize(value);

        return await ProcessMessage(message).ConfigureAwait(false);
   
    }

    public async Task<bool> RemoveAtAsync(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var message = NexusCollectionListRemoveMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        message.Version = _itemList.Version;
        message.Index = index;

        return await ProcessMessage(message).ConfigureAwait(false);
    }

    public async Task<bool> AddAsync(T item)
    {
        var message = NexusCollectionListInsertMessage.Rent();
        using var _ = await OperationLock().ConfigureAwait(false);
        var state = _itemList.State;
        message.Version = state.Version;
        message.Index = state.List.Count;
        message.Value = MemoryPackSerializer.Serialize(item);

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
