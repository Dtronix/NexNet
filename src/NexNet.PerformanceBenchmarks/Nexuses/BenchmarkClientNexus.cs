using System.Collections.Concurrent;
using System.Diagnostics;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.PerformanceBenchmarks.Nexuses;

/// <summary>
/// Client-side nexus implementation for benchmark scenarios.
/// Tracks received messages and metrics for scalability and stress testing.
/// </summary>
[Nexus<IBenchmarkClientNexus, IBenchmarkServerNexus>(NexusType = NexusType.Client)]
public sealed partial class BenchmarkClientNexus : IBenchmarkClientNexus
{
    #region Metrics Tracking

    /// <summary>
    /// Count of broadcast messages received.
    /// </summary>
    private long _broadcastCount;

    /// <summary>
    /// Count of group messages received.
    /// </summary>
    private long _groupMessageCount;

    /// <summary>
    /// Count of notification messages received.
    /// </summary>
    private long _notificationCount;

    /// <summary>
    /// Timestamp of first broadcast message received (for latency calculation).
    /// </summary>
    private long _firstBroadcastTimestamp;

    /// <summary>
    /// Timestamp of last broadcast message received (for delivery time calculation).
    /// </summary>
    private long _lastBroadcastTimestamp;

    /// <summary>
    /// Highest sequence number received in broadcasts.
    /// </summary>
    private long _maxBroadcastSequence;

    /// <summary>
    /// Reconnection event tracking.
    /// </summary>
    private int _reconnectionCount;

    /// <summary>
    /// Last disconnect timestamp.
    /// </summary>
    private long _lastDisconnectTimestamp;

    /// <summary>
    /// Last reconnect timestamp.
    /// </summary>
    private long _lastReconnectTimestamp;

    /// <summary>
    /// Received group messages by group name.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _groupMessageCounts = new();

    /// <summary>
    /// Task completion source for waiting on specific message count.
    /// </summary>
    private TaskCompletionSource<bool>? _broadcastWaitTcs;

    /// <summary>
    /// Target message count for broadcast wait.
    /// </summary>
    private long _broadcastWaitTarget;

    #endregion

    #region Public Metrics Properties

    /// <summary>
    /// Gets the count of broadcast messages received.
    /// </summary>
    public long BroadcastCount => Interlocked.Read(ref _broadcastCount);

    /// <summary>
    /// Gets the count of group messages received.
    /// </summary>
    public long GroupMessageCount => Interlocked.Read(ref _groupMessageCount);

    /// <summary>
    /// Gets the count of notification messages received.
    /// </summary>
    public long NotificationCount => Interlocked.Read(ref _notificationCount);

    /// <summary>
    /// Gets the timestamp of the first broadcast message.
    /// </summary>
    public long FirstBroadcastTimestamp => Interlocked.Read(ref _firstBroadcastTimestamp);

    /// <summary>
    /// Gets the timestamp of the last broadcast message.
    /// </summary>
    public long LastBroadcastTimestamp => Interlocked.Read(ref _lastBroadcastTimestamp);

    /// <summary>
    /// Gets the highest sequence number seen in broadcasts.
    /// </summary>
    public long MaxBroadcastSequence => Interlocked.Read(ref _maxBroadcastSequence);

    /// <summary>
    /// Gets the number of reconnections that occurred.
    /// </summary>
    public int ReconnectionCount => Volatile.Read(ref _reconnectionCount);

    /// <summary>
    /// Gets the message counts per group.
    /// </summary>
    public IReadOnlyDictionary<string, long> GroupMessageCounts => _groupMessageCounts;

    /// <summary>
    /// Gets the reconnection time in milliseconds (time between disconnect and reconnect).
    /// Returns 0 if no reconnection has occurred.
    /// </summary>
    public long ReconnectionTimeMs
    {
        get
        {
            var disconnect = Interlocked.Read(ref _lastDisconnectTimestamp);
            var reconnect = Interlocked.Read(ref _lastReconnectTimestamp);
            return reconnect > disconnect ? reconnect - disconnect : 0;
        }
    }

    #endregion

    #region Metrics Methods

    /// <summary>
    /// Resets all metrics to zero.
    /// </summary>
    public void ResetMetrics()
    {
        Interlocked.Exchange(ref _broadcastCount, 0);
        Interlocked.Exchange(ref _groupMessageCount, 0);
        Interlocked.Exchange(ref _notificationCount, 0);
        Interlocked.Exchange(ref _firstBroadcastTimestamp, 0);
        Interlocked.Exchange(ref _lastBroadcastTimestamp, 0);
        Interlocked.Exchange(ref _maxBroadcastSequence, 0);
        Volatile.Write(ref _reconnectionCount, 0);
        Interlocked.Exchange(ref _lastDisconnectTimestamp, 0);
        Interlocked.Exchange(ref _lastReconnectTimestamp, 0);
        _groupMessageCounts.Clear();
        _broadcastWaitTcs = null;
    }

    /// <summary>
    /// Waits until the specified number of broadcasts have been received.
    /// </summary>
    public Task WaitForBroadcastsAsync(long count, CancellationToken cancellationToken = default)
    {
        if (BroadcastCount >= count)
            return Task.CompletedTask;

        _broadcastWaitTarget = count;
        _broadcastWaitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Re-check in case messages arrived between check and TCS creation
        if (BroadcastCount >= count)
        {
            _broadcastWaitTcs.TrySetResult(true);
        }

        return _broadcastWaitTcs.Task.WaitAsync(cancellationToken);
    }

    #endregion

    #region Broadcast Callbacks

    public ValueTask ReceiveBroadcast(byte[] data)
    {
        var timestamp = Stopwatch.GetTimestamp();

        var count = Interlocked.Increment(ref _broadcastCount);

        // Track first message timestamp
        if (count == 1)
        {
            Interlocked.Exchange(ref _firstBroadcastTimestamp, timestamp);
        }

        Interlocked.Exchange(ref _lastBroadcastTimestamp, timestamp);

        // Check if waiting for specific count
        if (_broadcastWaitTcs != null && count >= _broadcastWaitTarget)
        {
            _broadcastWaitTcs.TrySetResult(true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReceiveBroadcastWithSequence(long sequence, byte[] data)
    {
        var timestamp = Stopwatch.GetTimestamp();

        var count = Interlocked.Increment(ref _broadcastCount);

        // Track first message timestamp
        if (count == 1)
        {
            Interlocked.Exchange(ref _firstBroadcastTimestamp, timestamp);
        }

        Interlocked.Exchange(ref _lastBroadcastTimestamp, timestamp);

        // Update max sequence
        long currentMax;
        do
        {
            currentMax = Interlocked.Read(ref _maxBroadcastSequence);
            if (sequence <= currentMax)
                break;
        } while (Interlocked.CompareExchange(ref _maxBroadcastSequence, sequence, currentMax) != currentMax);

        // Check if waiting for specific count
        if (_broadcastWaitTcs != null && count >= _broadcastWaitTarget)
        {
            _broadcastWaitTcs.TrySetResult(true);
        }

        return ValueTask.CompletedTask;
    }

    #endregion

    #region Group Messaging Callbacks

    public ValueTask ReceiveGroupMessage(string groupName, byte[] data)
    {
        Interlocked.Increment(ref _groupMessageCount);
        _groupMessageCounts.AddOrUpdate(groupName, 1, (_, c) => c + 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReceiveGroupMessageWithSequence(string groupName, long sequence, byte[] data)
    {
        Interlocked.Increment(ref _groupMessageCount);
        _groupMessageCounts.AddOrUpdate(groupName, 1, (_, c) => c + 1);
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Pipe/Channel Callbacks

    public async ValueTask ReceivePipeStream(INexusDuplexPipe pipe)
    {
        // Read and discard all incoming data
        try
        {
            while (true)
            {
                var result = await pipe.Input.ReadAsync();

                if (result.IsCompleted || result.IsCanceled)
                    break;

                pipe.Input.AdvanceTo(result.Buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when pipe is completed
        }
    }

    public async ValueTask ReceiveChannelStream(INexusDuplexChannel<SmallPayload> channel)
    {
        var reader = await channel.GetReaderAsync();

        // Read and discard all items
        await foreach (var _ in reader)
        {
            // Discard
        }
    }

    #endregion

    #region Notification Callbacks

    public void Notify(byte[] data)
    {
        Interlocked.Increment(ref _notificationCount);
    }

    public void NotifyWithSequence(long sequence)
    {
        Interlocked.Increment(ref _notificationCount);
    }

    #endregion

    #region Lifecycle Methods

    protected override ValueTask OnConnected(bool isReconnected)
    {
        if (isReconnected)
        {
            Interlocked.Increment(ref _reconnectionCount);
            Interlocked.Exchange(ref _lastReconnectTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDisconnected(DisconnectReason reason)
    {
        Interlocked.Exchange(ref _lastDisconnectTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnReconnecting()
    {
        // Reconnection attempt started
        return ValueTask.CompletedTask;
    }

    #endregion
}
