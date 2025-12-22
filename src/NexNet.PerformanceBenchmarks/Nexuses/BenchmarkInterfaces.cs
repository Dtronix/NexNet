using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.PerformanceBenchmarks.Nexuses;

/// <summary>
/// Server-side nexus interface for benchmark scenarios.
/// Implements all methods needed for latency, throughput, scalability, and stress testing.
/// </summary>
public interface IBenchmarkServerNexus
{
    #region Latency Benchmarks - Echo Methods

    /// <summary>
    /// Echoes raw byte array back to client. Used for latency measurements.
    /// </summary>
    ValueTask<byte[]> Echo(byte[] data);

    /// <summary>
    /// Echoes tiny payload (1 byte) back to client.
    /// </summary>
    ValueTask<TinyPayload> EchoTiny(TinyPayload data);

    /// <summary>
    /// Echoes small payload (~1 KB) back to client.
    /// </summary>
    ValueTask<SmallPayload> EchoSmall(SmallPayload data);

    /// <summary>
    /// Echoes medium payload (~64 KB) back to client.
    /// </summary>
    ValueTask<MediumPayload> EchoMedium(MediumPayload data);

    /// <summary>
    /// Echoes large payload (~1 MB) back to client.
    /// </summary>
    ValueTask<LargePayload> EchoLarge(LargePayload data);

    /// <summary>
    /// Echoes extra-large payload (~10 MB) back to client.
    /// </summary>
    ValueTask<XLargePayload> EchoXLarge(XLargePayload data);

    #endregion

    #region Throughput Benchmarks - Pipe Streaming

    /// <summary>
    /// Receives data from client via duplex pipe (upload test).
    /// Server reads all incoming data and discards it.
    /// </summary>
    ValueTask StreamUpload(INexusDuplexPipe pipe);

    /// <summary>
    /// Sends data to client via duplex pipe (download test).
    /// Server writes data continuously until pipe is completed.
    /// </summary>
    ValueTask StreamDownload(INexusDuplexPipe pipe);

    /// <summary>
    /// Bidirectional pipe streaming (echo mode).
    /// Server echoes back whatever it receives.
    /// </summary>
    ValueTask StreamBidirectional(INexusDuplexPipe pipe);

    #endregion

    #region Throughput Benchmarks - Channel Streaming

    /// <summary>
    /// Receives typed items from client via channel (upload test).
    /// </summary>
    ValueTask ChannelUpload(INexusDuplexChannel<SmallPayload> channel);

    /// <summary>
    /// Sends typed items to client via channel (download test).
    /// </summary>
    ValueTask ChannelDownload(INexusDuplexChannel<SmallPayload> channel);

    /// <summary>
    /// Receives unmanaged items from client via channel (high-performance upload).
    /// </summary>
    ValueTask UnmanagedChannelUpload(INexusDuplexUnmanagedChannel<long> channel);

    /// <summary>
    /// Sends unmanaged items to client via channel (high-performance download).
    /// </summary>
    ValueTask UnmanagedChannelDownload(INexusDuplexUnmanagedChannel<long> channel);

    /// <summary>
    /// Bidirectional channel streaming (echo mode).
    /// Server echoes back whatever SmallPayload items it receives.
    /// </summary>
    ValueTask ChannelBidirectional(INexusDuplexChannel<SmallPayload> channel);

    /// <summary>
    /// Bidirectional unmanaged channel streaming (echo mode).
    /// Server echoes back whatever long values it receives.
    /// </summary>
    ValueTask UnmanagedChannelBidirectional(INexusDuplexUnmanagedChannel<long> channel);

    #endregion

    #region Throughput Benchmarks - Fire-and-Forget

    /// <summary>
    /// Receives data without acknowledgment (fire-and-forget).
    /// Used for maximum throughput testing.
    /// </summary>
    void FireAndForget(byte[] data);

    /// <summary>
    /// Receives data and increments a counter.
    /// Counter can be queried via GetFireAndForgetCount.
    /// </summary>
    void FireAndForgetCounted(byte[] data);

    /// <summary>
    /// Gets the count of fire-and-forget messages received.
    /// </summary>
    ValueTask<long> GetFireAndForgetCount();

    /// <summary>
    /// Resets the fire-and-forget counter.
    /// </summary>
    ValueTask ResetFireAndForgetCount();

    #endregion

    #region Scalability Benchmarks

    /// <summary>
    /// Registers the client for broadcast messages.
    /// </summary>
    ValueTask RegisterForBroadcast();

    /// <summary>
    /// Unregisters the client from broadcast messages.
    /// </summary>
    ValueTask UnregisterFromBroadcast();

    /// <summary>
    /// Joins a named group for group messaging.
    /// </summary>
    ValueTask JoinGroup(string groupName);

    /// <summary>
    /// Leaves a named group.
    /// </summary>
    ValueTask LeaveGroup(string groupName);

    /// <summary>
    /// Requests the server to broadcast a message to all registered clients.
    /// </summary>
    ValueTask RequestBroadcast(byte[] data);

    /// <summary>
    /// Requests the server to send a message to a specific group.
    /// </summary>
    ValueTask RequestGroupMessage(string groupName, byte[] data);

    #endregion

    #region Overhead Benchmarks

    /// <summary>
    /// Echo with cancellation token support for cancellation overhead testing.
    /// </summary>
    ValueTask<byte[]> EchoWithCancellation(byte[] data, CancellationToken cancellationToken);

    /// <summary>
    /// Simple ping for connection testing.
    /// </summary>
    ValueTask Ping();

    /// <summary>
    /// Returns the current server timestamp.
    /// </summary>
    ValueTask<long> GetServerTimestamp();

    #endregion

    #region Collection Synchronization

    /// <summary>
    /// Synchronized list for collection benchmarks.
    /// Server-to-client sync mode.
    /// </summary>
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<TestItem> SyncList { get; }

    /// <summary>
    /// Bidirectional synchronized list.
    /// </summary>
    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<TestItem> BiDirectionalList { get; }

    #endregion
}

/// <summary>
/// Client-side nexus interface for benchmark scenarios.
/// Implements callbacks for broadcast, group messaging, and collection sync.
/// </summary>
public interface IBenchmarkClientNexus
{
    #region Broadcast Callbacks

    /// <summary>
    /// Called when the server broadcasts a message to all clients.
    /// </summary>
    ValueTask ReceiveBroadcast(byte[] data);

    /// <summary>
    /// Called when the server broadcasts a message to all clients with a sequence number.
    /// Used for tracking message delivery order and completeness.
    /// </summary>
    ValueTask ReceiveBroadcastWithSequence(long sequence, byte[] data);

    #endregion

    #region Group Messaging Callbacks

    /// <summary>
    /// Called when the server sends a message to a group the client belongs to.
    /// </summary>
    ValueTask ReceiveGroupMessage(string groupName, byte[] data);

    /// <summary>
    /// Called when the server sends a message to a group with sequence tracking.
    /// </summary>
    ValueTask ReceiveGroupMessageWithSequence(string groupName, long sequence, byte[] data);

    #endregion

    #region Pipe/Channel Callbacks (for bidirectional tests)

    /// <summary>
    /// Server-initiated pipe for reverse streaming tests.
    /// </summary>
    ValueTask ReceivePipeStream(INexusDuplexPipe pipe);

    /// <summary>
    /// Server-initiated channel for reverse streaming tests.
    /// </summary>
    ValueTask ReceiveChannelStream(INexusDuplexChannel<SmallPayload> channel);

    #endregion

    #region Notification Callbacks

    /// <summary>
    /// Simple notification from server (fire-and-forget).
    /// </summary>
    void Notify(byte[] data);

    /// <summary>
    /// Notification with sequence number for ordering verification.
    /// </summary>
    void NotifyWithSequence(long sequence);

    #endregion

    // Note: Collections (SyncList, BiDirectionalList) are defined on IBenchmarkServerNexus.
    // Clients access them via the server proxy: client.Proxy.SyncList
}
