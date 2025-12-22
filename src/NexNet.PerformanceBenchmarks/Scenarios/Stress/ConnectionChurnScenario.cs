using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Stress;

/// <summary>
/// Measures connection churn performance with rapid connect/disconnect cycles.
/// Tests connection establishment and teardown overhead.
/// </summary>
public sealed class ConnectionChurnScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ConnectionChurn";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Stress;

    /// <inheritdoc />
    public override string Description => "Rapid connect/disconnect cycles measuring connection overhead";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Small payload for connection test.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Number of churn cycles for full test.
    /// </summary>
    private const int FullChurnCycles = 500;

    /// <summary>
    /// Number of churn cycles for quick test.
    /// </summary>
    private const int QuickChurnCycles = 50;

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var churnCycles = context.Settings.MeasuredIterations <= 5 ? QuickChurnCycles : FullChurnCycles;
            var connectLatencies = new List<double>(churnCycles);
            var disconnectLatencies = new List<double>(churnCycles);
            var errors = 0;

            // Record initial memory state
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var initialMemory = GC.GetTotalMemory(false);
            var initialGen0 = GC.CollectionCount(0);
            var initialGen1 = GC.CollectionCount(1);
            var initialGen2 = GC.CollectionCount(2);

            var totalSw = Stopwatch.StartNew();

            // Warmup - a few connect/disconnect cycles
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var warmupNexus = new BenchmarkClientNexus();
                    var warmupClient = BenchmarkClientNexus.CreateClient(context.ClientConfig, warmupNexus);
                    await warmupClient.ConnectAsync(cancellationToken);
                    await warmupClient.DisconnectAsync();
                }
                catch
                {
                    // Ignore warmup errors
                }
            }

            // Measured churn cycles
            for (int i = 0; i < churnCycles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var clientNexus = new BenchmarkClientNexus();
                    var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);

                    // Measure connect time
                    var connectSw = Stopwatch.StartNew();
                    await client.ConnectAsync(cancellationToken);
                    connectSw.Stop();
                    connectLatencies.Add(connectSw.Elapsed.TotalMicroseconds);

                    // Measure disconnect time
                    var disconnectSw = Stopwatch.StartNew();
                    await client.DisconnectAsync();
                    disconnectSw.Stop();
                    disconnectLatencies.Add(disconnectSw.Elapsed.TotalMicroseconds);
                }
                catch (Exception)
                {
                    errors++;
                }
            }

            totalSw.Stop();

            // Record final memory state
            var finalMemory = GC.GetTotalMemory(false);
            var finalGen0 = GC.CollectionCount(0) - initialGen0;
            var finalGen1 = GC.CollectionCount(1) - initialGen1;
            var finalGen2 = GC.CollectionCount(2) - initialGen2;

            // Calculate statistics
            var connectStats = StatisticsCalculator.Calculate(connectLatencies);
            var disconnectStats = StatisticsCalculator.Calculate(disconnectLatencies);

            var totalOperations = churnCycles * 2; // connect + disconnect
            var opsPerSecond = totalOperations / totalSw.Elapsed.TotalSeconds;

            var customMetrics = new Dictionary<string, object>
            {
                ["Connect_MeanLatency_us"] = connectStats.Mean,
                ["Connect_P50Latency_us"] = connectStats.P50,
                ["Connect_P95Latency_us"] = connectStats.P95,
                ["Connect_P99Latency_us"] = connectStats.P99,
                ["Disconnect_MeanLatency_us"] = disconnectStats.Mean,
                ["Disconnect_P50Latency_us"] = disconnectStats.P50,
                ["Disconnect_P95Latency_us"] = disconnectStats.P95,
                ["Disconnect_P99Latency_us"] = disconnectStats.P99,
                ["TotalCycles"] = churnCycles,
                ["Errors"] = errors,
                ["MemoryDelta_bytes"] = finalMemory - initialMemory
            };

            return ScenarioResult.FromStressMeasurement(
                Name,
                Category,
                context.Transport,
                context.PayloadSize,
                new StressMetrics
                {
                    TotalOperations = totalOperations,
                    OperationsPerSecond = opsPerSecond,
                    Duration = totalSw.Elapsed,
                    ErrorCount = errors,
                    ConnectLatency = new LatencyMetrics
                    {
                        MeanMicroseconds = connectStats.Mean,
                        StdDevMicroseconds = connectStats.StdDev,
                        MinMicroseconds = connectStats.Min,
                        MaxMicroseconds = connectStats.Max,
                        P50Microseconds = connectStats.P50,
                        P95Microseconds = connectStats.P95,
                        P99Microseconds = connectStats.P99,
                        IterationCount = connectLatencies.Count
                    },
                    DisconnectLatency = new LatencyMetrics
                    {
                        MeanMicroseconds = disconnectStats.Mean,
                        StdDevMicroseconds = disconnectStats.StdDev,
                        MinMicroseconds = disconnectStats.Min,
                        MaxMicroseconds = disconnectStats.Max,
                        P50Microseconds = disconnectStats.P50,
                        P95Microseconds = disconnectStats.P95,
                        P99Microseconds = disconnectStats.P99,
                        IterationCount = disconnectLatencies.Count
                    },
                    MemoryMetrics = new MemoryMetrics
                    {
                        AllocatedBytes = finalMemory - initialMemory,
                        Gen0Collections = finalGen0,
                        Gen1Collections = finalGen1,
                        Gen2Collections = finalGen2
                    }
                },
                customMetrics);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
