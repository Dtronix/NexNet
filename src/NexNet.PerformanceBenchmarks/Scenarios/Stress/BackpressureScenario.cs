using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Stress;

/// <summary>
/// Measures backpressure behavior with a fast producer and slow consumer.
/// Tests how the system handles flow control when the consumer can't keep up.
/// </summary>
public sealed class BackpressureScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "Backpressure";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Stress;

    /// <inheritdoc />
    public override string Description => "Fast producer with slow consumer measuring flow control";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Medium payload for backpressure test.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Medium];

    /// <summary>
    /// Total bytes to transfer in full test.
    /// </summary>
    private const long FullTransferBytes = 50 * 1024 * 1024; // 50 MB

    /// <summary>
    /// Total bytes to transfer in quick test.
    /// </summary>
    private const long QuickTransferBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>
    /// Chunk size for writing.
    /// </summary>
    private const int ChunkSize = 64 * 1024; // 64 KB

    /// <summary>
    /// Consumer delay to simulate slow processing.
    /// </summary>
    private static readonly TimeSpan ConsumerDelay = TimeSpan.FromMilliseconds(5);

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var targetBytes = context.Settings.MeasuredIterations <= 5 ? QuickTransferBytes : FullTransferBytes;
            var writeBuffer = new byte[ChunkSize];
            new Random(42).NextBytes(writeBuffer);

            // Create and connect client
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Create pipe for streaming
                var pipe = client.CreatePipe();
                await client.Proxy.StreamBidirectional(pipe);
                await pipe.ReadyTask;

                long producedBytes = 0;
                long consumedBytes = 0;
                var pauseCount = 0;
                var pauseDurations = new List<double>();
                var errors = 0;

                var totalSw = Stopwatch.StartNew();

                // Producer task - writes as fast as possible
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        while (Interlocked.Read(ref producedBytes) < targetBytes && !cancellationToken.IsCancellationRequested)
                        {
                            var pauseSw = Stopwatch.StartNew();
                            var result = await pipe.Output.WriteAsync(writeBuffer, cancellationToken);
                            pauseSw.Stop();

                            // Track if write was delayed (indicating backpressure)
                            if (pauseSw.ElapsedMilliseconds > 10)
                            {
                                Interlocked.Increment(ref pauseCount);
                                lock (pauseDurations)
                                {
                                    pauseDurations.Add(pauseSw.Elapsed.TotalMicroseconds);
                                }
                            }

                            if (result.IsCompleted || result.IsCanceled)
                                break;

                            Interlocked.Add(ref producedBytes, writeBuffer.Length);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }, cancellationToken);

                // Consumer task - reads slowly
                var consumerTask = Task.Run(async () =>
                {
                    try
                    {
                        while (Interlocked.Read(ref consumedBytes) < targetBytes && !cancellationToken.IsCancellationRequested)
                        {
                            var result = await pipe.Input.ReadAsync(cancellationToken);

                            if (result.IsCompleted || result.IsCanceled)
                                break;

                            var bytesRead = result.Buffer.Length;
                            pipe.Input.AdvanceTo(result.Buffer.End);
                            Interlocked.Add(ref consumedBytes, bytesRead);

                            // Simulate slow consumer
                            await Task.Delay(ConsumerDelay, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }, cancellationToken);

                // Wait for producer to finish
                await producerTask;

                // Signal completion to consumer
                try
                {
                    await pipe.CompleteAsync();
                }
                catch
                {
                    // Ignore completion errors
                }

                // Wait for consumer with timeout
                using var consumerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumerTimeout.Token);

                try
                {
                    await consumerTask.WaitAsync(combined.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout waiting for consumer
                }

                totalSw.Stop();

                // Calculate statistics
                var throughputMBps = consumedBytes / (1024.0 * 1024.0) / totalSw.Elapsed.TotalSeconds;
                var avgPauseMicroseconds = pauseDurations.Count > 0 ? pauseDurations.Average() : 0;
                var maxPauseMicroseconds = pauseDurations.Count > 0 ? pauseDurations.Max() : 0;

                var customMetrics = new Dictionary<string, object>
                {
                    ["ProducedBytes"] = producedBytes,
                    ["ConsumedBytes"] = consumedBytes,
                    ["TargetBytes"] = targetBytes,
                    ["Duration_seconds"] = totalSw.Elapsed.TotalSeconds,
                    ["Throughput_MBps"] = throughputMBps,
                    ["PauseCount"] = pauseCount,
                    ["AvgPause_us"] = avgPauseMicroseconds,
                    ["MaxPause_us"] = maxPauseMicroseconds,
                    ["Errors"] = errors,
                    ["CompletionRatio"] = (double)consumedBytes / targetBytes
                };

                LatencyMetrics? pauseLatency = null;
                if (pauseDurations.Count > 0)
                {
                    var stats = StatisticsCalculator.Calculate(pauseDurations);
                    pauseLatency = new LatencyMetrics
                    {
                        MeanMicroseconds = stats.Mean,
                        StdDevMicroseconds = stats.StdDev,
                        MinMicroseconds = stats.Min,
                        MaxMicroseconds = stats.Max,
                        P50Microseconds = stats.P50,
                        P95Microseconds = stats.P95,
                        P99Microseconds = stats.P99,
                        IterationCount = pauseDurations.Count
                    };
                }

                return ScenarioResult.FromStressMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    new StressMetrics
                    {
                        TotalOperations = consumedBytes / ChunkSize,
                        OperationsPerSecond = (consumedBytes / ChunkSize) / totalSw.Elapsed.TotalSeconds,
                        Duration = totalSw.Elapsed,
                        ErrorCount = errors,
                        PauseCount = pauseCount,
                        AveragePauseMicroseconds = avgPauseMicroseconds
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
