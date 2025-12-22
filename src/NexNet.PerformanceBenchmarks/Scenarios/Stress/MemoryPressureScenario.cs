using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Stress;

/// <summary>
/// Measures memory behavior under sustained load.
/// Tracks GC collections, heap size, and allocation rate to detect memory leaks.
/// </summary>
public sealed class MemoryPressureScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "MemoryPressure";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Stress;

    /// <inheritdoc />
    public override string Description => "Sustained load measuring GC and memory behavior";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Small payload for memory pressure test.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Duration for full memory pressure test.
    /// </summary>
    private static readonly TimeSpan FullDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Duration for quick memory pressure test.
    /// </summary>
    private static readonly TimeSpan QuickDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Interval for sampling memory metrics.
    /// </summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var payload = context.GeneratePayload();
            var duration = context.Settings.MeasuredIterations <= 5 ? QuickDuration : FullDuration;

            // Create and connect a single client for sustained operations
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Force GC before starting to establish baseline
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var initialMemory = GC.GetTotalMemory(false);
                var initialGen0 = GC.CollectionCount(0);
                var initialGen1 = GC.CollectionCount(1);
                var initialGen2 = GC.CollectionCount(2);
                var initialAllocated = GC.GetTotalAllocatedBytes();

                var memorySamples = new List<long>();
                var operationCount = 0L;
                var errors = 0;

                using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                durationCts.CancelAfter(duration);

                var totalSw = Stopwatch.StartNew();
                var sampleSw = Stopwatch.StartNew();

                // Warmup
                for (int i = 0; i < 10; i++)
                {
                    await client.Proxy.Echo(payload);
                }

                try
                {
                    while (!durationCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Perform sustained operations
                            await client.Proxy.Echo(payload);
                            Interlocked.Increment(ref operationCount);
                        }
                        catch (OperationCanceledException) when (durationCts.Token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }

                        // Sample memory at intervals
                        if (sampleSw.Elapsed >= SampleInterval)
                        {
                            memorySamples.Add(GC.GetTotalMemory(false));
                            sampleSw.Restart();
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Expected timeout
                }

                totalSw.Stop();

                // Capture final metrics
                var finalMemory = GC.GetTotalMemory(false);
                var finalGen0 = GC.CollectionCount(0) - initialGen0;
                var finalGen1 = GC.CollectionCount(1) - initialGen1;
                var finalGen2 = GC.CollectionCount(2) - initialGen2;
                var totalAllocated = GC.GetTotalAllocatedBytes() - initialAllocated;

                // Calculate memory statistics
                memorySamples.Add(finalMemory);
                var peakMemory = memorySamples.Count > 0 ? memorySamples.Max() : finalMemory;
                var avgMemory = memorySamples.Count > 0 ? memorySamples.Average() : finalMemory;
                var memoryDelta = finalMemory - initialMemory;
                var allocationRate = totalAllocated / totalSw.Elapsed.TotalSeconds;
                var opsPerSecond = operationCount / totalSw.Elapsed.TotalSeconds;

                // Detect potential memory leak (memory grew significantly over time)
                var leakDetected = memorySamples.Count >= 3 &&
                    memorySamples[^1] > memorySamples[0] * 1.5 &&
                    memorySamples[^1] > memorySamples[memorySamples.Count / 2] * 1.2;

                var customMetrics = new Dictionary<string, object>
                {
                    ["TotalOperations"] = operationCount,
                    ["OpsPerSecond"] = opsPerSecond,
                    ["Duration_seconds"] = totalSw.Elapsed.TotalSeconds,
                    ["InitialMemory_bytes"] = initialMemory,
                    ["FinalMemory_bytes"] = finalMemory,
                    ["PeakMemory_bytes"] = peakMemory,
                    ["AvgMemory_bytes"] = avgMemory,
                    ["MemoryDelta_bytes"] = memoryDelta,
                    ["TotalAllocated_bytes"] = totalAllocated,
                    ["AllocationRate_bytesPerSec"] = allocationRate,
                    ["Gen0Collections"] = finalGen0,
                    ["Gen1Collections"] = finalGen1,
                    ["Gen2Collections"] = finalGen2,
                    ["LeakDetected"] = leakDetected,
                    ["Errors"] = errors
                };

                return ScenarioResult.FromStressMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    new StressMetrics
                    {
                        TotalOperations = operationCount,
                        OperationsPerSecond = opsPerSecond,
                        Duration = totalSw.Elapsed,
                        ErrorCount = errors,
                        MemoryMetrics = new MemoryMetrics
                        {
                            AllocatedBytes = totalAllocated,
                            Gen0Collections = finalGen0,
                            Gen1Collections = finalGen1,
                            Gen2Collections = finalGen2,
                            PeakWorkingSetBytes = peakMemory
                        }
                    },
                    customMetrics);
            }
            finally
            {
                try
                {
                    await client.DisconnectAsync();
                }
                catch
                {
                    // Ignore disconnect errors
                }
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
