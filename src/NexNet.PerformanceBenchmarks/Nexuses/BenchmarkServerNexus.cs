using System.Buffers;
using System.IO.Pipelines;
using NexNet.Collections.Lists;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.PerformanceBenchmarks.Nexuses;

/// <summary>
/// Server-side nexus implementation for benchmark scenarios.
/// Implements all server methods for latency, throughput, scalability, and stress testing.
/// </summary>
[Nexus<IBenchmarkServerNexus, IBenchmarkClientNexus>(NexusType = NexusType.Server)]
public sealed partial class BenchmarkServerNexus : IBenchmarkServerNexus
{
    private const string BroadcastGroup = "broadcast";

    /// <summary>
    /// Counter for fire-and-forget messages received.
    /// Thread-safe via Interlocked.
    /// </summary>
    private long _fireAndForgetCount;

    /// <summary>
    /// Buffer for download streaming tests.
    /// </summary>
    private static readonly byte[] DownloadBuffer = new byte[64 * 1024]; // 64 KB chunks

    static BenchmarkServerNexus()
    {
        // Initialize download buffer with random data
        new Random(42).NextBytes(DownloadBuffer);
    }

    #region Latency Benchmarks - Echo Methods

    public ValueTask<byte[]> Echo(byte[] data)
    {
        return new ValueTask<byte[]>(data);
    }

    public ValueTask<TinyPayload> EchoTiny(TinyPayload data)
    {
        return new ValueTask<TinyPayload>(data);
    }

    public ValueTask<SmallPayload> EchoSmall(SmallPayload data)
    {
        return new ValueTask<SmallPayload>(data);
    }

    public ValueTask<MediumPayload> EchoMedium(MediumPayload data)
    {
        return new ValueTask<MediumPayload>(data);
    }

    public ValueTask<LargePayload> EchoLarge(LargePayload data)
    {
        return new ValueTask<LargePayload>(data);
    }

    public ValueTask<XLargePayload> EchoXLarge(XLargePayload data)
    {
        return new ValueTask<XLargePayload>(data);
    }

    #endregion

    #region Throughput Benchmarks - Pipe Streaming

    public async ValueTask StreamUpload(INexusDuplexPipe pipe)
    {
        // Read and discard all incoming data
        try
        {
            while (true)
            {
                var result = await pipe.Input.ReadAsync();

                if (result.IsCompleted || result.IsCanceled)
                    break;

                // Consume all data without processing
                pipe.Input.AdvanceTo(result.Buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }
    }

    public async ValueTask StreamDownload(INexusDuplexPipe pipe)
    {
        await pipe.ReadyTask;

        // Write data continuously until the pipe is completed
        try
        {
            while (!pipe.CompleteTask.IsCompleted)
            {
                var result = await pipe.Output.WriteAsync(DownloadBuffer);

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await pipe.CompleteAsync();
    }

    public async ValueTask StreamBidirectional(INexusDuplexPipe pipe)
    {
        // Echo mode: read data and write it back
        try
        {
            while (true)
            {
                var result = await pipe.Input.ReadAsync();

                if (result.IsCompleted || result.IsCanceled)
                    break;

                // Echo back the received data
                foreach (var segment in result.Buffer)
                {
                    await pipe.Output.WriteAsync(segment);
                }

                pipe.Input.AdvanceTo(result.Buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await pipe.CompleteAsync();
    }

    #endregion

    #region Throughput Benchmarks - Channel Streaming

    public async ValueTask ChannelUpload(INexusDuplexChannel<SmallPayload> channel)
    {
        var reader = await channel.GetReaderAsync();

        // Read and discard all items
        await foreach (var _ in reader)
        {
            // Discard
        }
    }

    public async ValueTask ChannelDownload(INexusDuplexChannel<SmallPayload> channel)
    {
        var writer = await channel.GetWriterAsync();
        var payload = PayloadFactory.CreateSmall();

        // Write items continuously until channel is completed
        try
        {
            while (!writer.IsComplete)
            {
                if (!await writer.WriteAsync(payload))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await writer.CompleteAsync();
    }

    public async ValueTask UnmanagedChannelUpload(INexusDuplexUnmanagedChannel<long> channel)
    {
        var reader = await channel.GetReaderAsync();

        // Read and discard all items
        await foreach (var _ in reader)
        {
            // Discard
        }
    }

    public async ValueTask UnmanagedChannelDownload(INexusDuplexUnmanagedChannel<long> channel)
    {
        var writer = await channel.GetWriterAsync();
        long sequence = 0;

        // Write items continuously until channel is completed
        try
        {
            while (!writer.IsComplete)
            {
                if (!await writer.WriteAsync(sequence++))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await writer.CompleteAsync();
    }

    public async ValueTask ChannelBidirectional(INexusDuplexChannel<SmallPayload> channel)
    {
        var reader = await channel.GetReaderAsync();
        var writer = await channel.GetWriterAsync();

        // Echo mode: read items and write them back
        try
        {
            await foreach (var item in reader)
            {
                if (!await writer.WriteAsync(item))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await writer.CompleteAsync();
    }

    public async ValueTask UnmanagedChannelBidirectional(INexusDuplexUnmanagedChannel<long> channel)
    {
        var reader = await channel.GetReaderAsync();
        var writer = await channel.GetWriterAsync();

        // Echo mode: read items and write them back
        try
        {
            await foreach (var item in reader)
            {
                if (!await writer.WriteAsync(item))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when client completes
        }

        await writer.CompleteAsync();
    }

    #endregion

    #region Throughput Benchmarks - Fire-and-Forget

    public void FireAndForget(byte[] data)
    {
        // No-op, just receive the data
    }

    public void FireAndForgetCounted(byte[] data)
    {
        Interlocked.Increment(ref _fireAndForgetCount);
    }

    public ValueTask<long> GetFireAndForgetCount()
    {
        return new ValueTask<long>(Interlocked.Read(ref _fireAndForgetCount));
    }

    public ValueTask ResetFireAndForgetCount()
    {
        Interlocked.Exchange(ref _fireAndForgetCount, 0);
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Scalability Benchmarks

    public async ValueTask RegisterForBroadcast()
    {
        await Context.Groups.AddAsync(BroadcastGroup);
    }

    public async ValueTask UnregisterFromBroadcast()
    {
        await Context.Groups.RemoveAsync(BroadcastGroup);
    }

    public async ValueTask JoinGroup(string groupName)
    {
        await Context.Groups.AddAsync(groupName);
    }

    public async ValueTask LeaveGroup(string groupName)
    {
        await Context.Groups.RemoveAsync(groupName);
    }

    public async ValueTask RequestBroadcast(byte[] data)
    {
        await Context.Clients.Group(BroadcastGroup).ReceiveBroadcast(data);
    }

    public async ValueTask RequestGroupMessage(string groupName, byte[] data)
    {
        await Context.Clients.Group(groupName).ReceiveGroupMessage(groupName, data);
    }

    #endregion

    #region Overhead Benchmarks

    public ValueTask<byte[]> EchoWithCancellation(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<byte[]>(data);
    }

    public ValueTask Ping()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> GetServerTimestamp()
    {
        return new ValueTask<long>(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    #endregion

    #region Lifecycle Methods

    protected override ValueTask OnConnected(bool isReconnected)
    {
        // Could add connection metrics here
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDisconnected(DisconnectReason reason)
    {
        // Could add disconnection metrics here
        return ValueTask.CompletedTask;
    }

    #endregion
}
