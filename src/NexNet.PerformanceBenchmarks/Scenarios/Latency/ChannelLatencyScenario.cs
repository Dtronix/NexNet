using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Latency;

/// <summary>
/// Measures round-trip latency for typed channel communication.
/// Uses both managed channels (SmallPayload) and unmanaged channels (long).
/// </summary>
public sealed class ChannelLatencyScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ChannelLatency";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Latency;

    /// <inheritdoc />
    public override string Description => "Typed channel single-item round-trip latency";

    /// <summary>
    /// Channel latency uses fixed payloads (SmallPayload for managed, long for unmanaged).
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Small]; // SmallPayload is ~1KB

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            // Create and connect client
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Create warmup manager
                var warmup = new WarmupManager(context.Settings);

                // Create SmallPayload for managed channel tests
                var payload = PayloadFactory.CreateSmall(context.Settings.RandomSeed);

                // Warmup managed channel
                await warmup.WarmupAsync(async () =>
                {
                    await using var channel = client.CreateChannel<SmallPayload>();
                    _ = client.Proxy.ChannelBidirectional(channel);

                    var writer = await channel.GetWriterAsync();
                    var reader = await channel.GetReaderAsync();

                    await writer.WriteAsync(payload);

                    await foreach (var _ in reader)
                    {
                        break;
                    }

                    await writer.CompleteAsync();
                }, "Managed Channel Echo");

                // Measure managed channel
                var managedMeasurements = await warmup.MeasureAsync(async () =>
                {
                    await using var channel = client.CreateChannel<SmallPayload>();

                    var sw = Stopwatch.StartNew();

                    _ = client.Proxy.ChannelBidirectional(channel);

                    var writer = await channel.GetWriterAsync();
                    var reader = await channel.GetReaderAsync();

                    await writer.WriteAsync(payload);

                    await foreach (var _ in reader)
                    {
                        sw.Stop();
                        break;
                    }

                    await writer.CompleteAsync();

                    return sw.Elapsed.TotalMicroseconds;
                }, "Managed Channel Echo");

                // Warmup unmanaged channel
                await warmup.WarmupAsync(async () =>
                {
                    await using var channel = client.CreateUnmanagedChannel<long>();
                    _ = client.Proxy.UnmanagedChannelBidirectional(channel);

                    var writer = await channel.GetWriterAsync();
                    var reader = await channel.GetReaderAsync();

                    await writer.WriteAsync(42L);

                    await foreach (var _ in reader)
                    {
                        break;
                    }

                    await writer.CompleteAsync();
                }, "Unmanaged Channel Echo");

                // Measure unmanaged channel
                var unmanagedMeasurements = await warmup.MeasureAsync(async () =>
                {
                    await using var channel = client.CreateUnmanagedChannel<long>();

                    var sw = Stopwatch.StartNew();

                    _ = client.Proxy.UnmanagedChannelBidirectional(channel);

                    var writer = await channel.GetWriterAsync();
                    var reader = await channel.GetReaderAsync();

                    await writer.WriteAsync(42L);

                    await foreach (var _ in reader)
                    {
                        sw.Stop();
                        break;
                    }

                    await writer.CompleteAsync();

                    return sw.Elapsed.TotalMicroseconds;
                }, "Unmanaged Channel Echo");

                // Calculate statistics for both channel types
                var managedStats = StatisticsCalculator.Calculate(managedMeasurements);
                var unmanagedStats = StatisticsCalculator.Calculate(unmanagedMeasurements);

                // Create result with managed channel metrics as primary and unmanaged as custom metrics
                return new ScenarioResult
                {
                    ScenarioName = Name,
                    Category = Category,
                    Transport = context.Transport,
                    PayloadSize = context.PayloadSize,
                    LatencyMetrics = new LatencyMetrics
                    {
                        MeanMicroseconds = managedStats.Mean,
                        StdDevMicroseconds = managedStats.StdDev,
                        MinMicroseconds = managedStats.Min,
                        MaxMicroseconds = managedStats.Max,
                        P50Microseconds = managedStats.P50,
                        P95Microseconds = managedStats.P95,
                        P99Microseconds = managedStats.P99,
                        IterationCount = managedMeasurements.Count
                    },
                    CustomMetrics = new Dictionary<string, object>
                    {
                        ["UnmanagedChannel_P50_us"] = unmanagedStats.P50,
                        ["UnmanagedChannel_P95_us"] = unmanagedStats.P95,
                        ["UnmanagedChannel_P99_us"] = unmanagedStats.P99,
                        ["UnmanagedChannel_Mean_us"] = unmanagedStats.Mean
                    }
                };
            }
            finally
            {
                await client.DisconnectAsync();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
