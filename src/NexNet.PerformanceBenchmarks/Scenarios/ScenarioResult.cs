using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;

namespace NexNet.PerformanceBenchmarks.Scenarios;

/// <summary>
/// Result from running a benchmark scenario.
/// </summary>
public sealed class ScenarioResult
{
    /// <summary>
    /// Name of the scenario that was run.
    /// </summary>
    public required string ScenarioName { get; init; }

    /// <summary>
    /// Category of the scenario.
    /// </summary>
    public required BenchmarkCategory Category { get; init; }

    /// <summary>
    /// Transport type used.
    /// </summary>
    public required TransportType Transport { get; init; }

    /// <summary>
    /// Payload size used, if applicable.
    /// </summary>
    public PayloadSize? PayloadSize { get; init; }

    /// <summary>
    /// Whether the scenario completed successfully.
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Error message if the scenario failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception if the scenario threw an error.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Latency metrics if this is a latency benchmark.
    /// </summary>
    public LatencyMetrics? LatencyMetrics { get; init; }

    /// <summary>
    /// Throughput metrics if this is a throughput benchmark.
    /// </summary>
    public ThroughputMetrics? ThroughputMetrics { get; init; }

    /// <summary>
    /// Scalability metrics if this is a scalability benchmark.
    /// </summary>
    public ScalabilityMetrics? ScalabilityMetrics { get; init; }

    /// <summary>
    /// Stress metrics if this is a stress benchmark.
    /// </summary>
    public StressMetrics? StressMetrics { get; init; }

    /// <summary>
    /// Collection metrics if this is a collection benchmark.
    /// </summary>
    public CollectionMetrics? CollectionMetrics { get; init; }

    /// <summary>
    /// Memory metrics from the run.
    /// </summary>
    public MemoryMetrics? MemoryMetrics { get; init; }

    /// <summary>
    /// Additional custom metrics.
    /// </summary>
    public Dictionary<string, object>? CustomMetrics { get; init; }

    /// <summary>
    /// Creates a result from latency measurements (in microseconds).
    /// </summary>
    public static ScenarioResult FromLatencyMeasurements(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize payloadSize,
        IReadOnlyList<double> measurementsMicroseconds)
    {
        var stats = StatisticsCalculator.Calculate(measurementsMicroseconds);
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            LatencyMetrics = new LatencyMetrics
            {
                MeanMicroseconds = stats.Mean,
                StdDevMicroseconds = stats.StdDev,
                MinMicroseconds = stats.Min,
                MaxMicroseconds = stats.Max,
                P50Microseconds = stats.P50,
                P95Microseconds = stats.P95,
                P99Microseconds = stats.P99,
                IterationCount = measurementsMicroseconds.Count
            }
        };
    }

    /// <summary>
    /// Creates a result from throughput measurements.
    /// </summary>
    public static ScenarioResult FromThroughputMeasurement(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize? payloadSize,
        long totalBytes,
        long totalMessages,
        TimeSpan duration,
        Dictionary<string, object>? customMetrics = null)
    {
        var durationSeconds = duration.TotalSeconds;
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            ThroughputMetrics = new ThroughputMetrics
            {
                MegabytesPerSecond = totalBytes / (1024.0 * 1024.0) / durationSeconds,
                MessagesPerSecond = totalMessages / durationSeconds,
                OperationsPerSecond = totalMessages / durationSeconds,
                TotalBytes = totalBytes,
                TotalMessages = totalMessages,
                Duration = duration
            },
            CustomMetrics = customMetrics
        };
    }

    /// <summary>
    /// Creates a result from scalability measurements.
    /// </summary>
    public static ScenarioResult FromScalabilityMeasurement(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize? payloadSize,
        int clientCount,
        IReadOnlyList<double> latencyMeasurementsMicroseconds,
        double firstDeliveryMicroseconds = 0,
        double lastDeliveryMicroseconds = 0,
        Dictionary<string, object>? customMetrics = null)
    {
        var stats = StatisticsCalculator.Calculate(latencyMeasurementsMicroseconds);
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            ScalabilityMetrics = new ScalabilityMetrics
            {
                ClientCount = clientCount,
                PerClientLatency = new LatencyMetrics
                {
                    MeanMicroseconds = stats.Mean,
                    StdDevMicroseconds = stats.StdDev,
                    MinMicroseconds = stats.Min,
                    MaxMicroseconds = stats.Max,
                    P50Microseconds = stats.P50,
                    P95Microseconds = stats.P95,
                    P99Microseconds = stats.P99,
                    IterationCount = latencyMeasurementsMicroseconds.Count
                },
                FirstDeliveryMicroseconds = firstDeliveryMicroseconds,
                LastDeliveryMicroseconds = lastDeliveryMicroseconds
            },
            CustomMetrics = customMetrics
        };
    }

    /// <summary>
    /// Creates a result from stress test measurements.
    /// </summary>
    public static ScenarioResult FromStressMeasurement(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize? payloadSize,
        StressMetrics stressMetrics,
        Dictionary<string, object>? customMetrics = null)
    {
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            StressMetrics = stressMetrics,
            CustomMetrics = customMetrics
        };
    }

    /// <summary>
    /// Creates a result from collection sync measurements.
    /// </summary>
    public static ScenarioResult FromCollectionMeasurement(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize? payloadSize,
        CollectionMetrics collectionMetrics,
        Dictionary<string, object>? customMetrics = null)
    {
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            CollectionMetrics = collectionMetrics,
            CustomMetrics = customMetrics
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ScenarioResult Failed(
        string scenarioName,
        BenchmarkCategory category,
        TransportType transport,
        PayloadSize? payloadSize,
        string errorMessage,
        Exception? exception = null)
    {
        return new ScenarioResult
        {
            ScenarioName = scenarioName,
            Category = category,
            Transport = transport,
            PayloadSize = payloadSize,
            Success = false,
            ErrorMessage = errorMessage,
            Exception = exception
        };
    }
}

/// <summary>
/// Latency metrics from a benchmark run.
/// </summary>
public sealed class LatencyMetrics
{
    /// <summary>Mean latency in microseconds.</summary>
    public double MeanMicroseconds { get; init; }

    /// <summary>Standard deviation in microseconds.</summary>
    public double StdDevMicroseconds { get; init; }

    /// <summary>Minimum latency in microseconds.</summary>
    public double MinMicroseconds { get; init; }

    /// <summary>Maximum latency in microseconds.</summary>
    public double MaxMicroseconds { get; init; }

    /// <summary>50th percentile (median) in microseconds.</summary>
    public double P50Microseconds { get; init; }

    /// <summary>95th percentile in microseconds.</summary>
    public double P95Microseconds { get; init; }

    /// <summary>99th percentile in microseconds.</summary>
    public double P99Microseconds { get; init; }

    /// <summary>Number of iterations measured.</summary>
    public int IterationCount { get; init; }
}

/// <summary>
/// Throughput metrics from a benchmark run.
/// </summary>
public sealed class ThroughputMetrics
{
    /// <summary>Throughput in megabytes per second.</summary>
    public double MegabytesPerSecond { get; init; }

    /// <summary>Throughput in messages per second.</summary>
    public double MessagesPerSecond { get; init; }

    /// <summary>Throughput in operations per second (invocations, items).</summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>Total bytes transferred.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Total messages transferred.</summary>
    public long TotalMessages { get; init; }

    /// <summary>Duration of the throughput test.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Scalability metrics from a benchmark run.
/// </summary>
public sealed class ScalabilityMetrics
{
    /// <summary>Number of clients in the test.</summary>
    public int ClientCount { get; init; }

    /// <summary>Per-client latency statistics.</summary>
    public LatencyMetrics? PerClientLatency { get; init; }

    /// <summary>Aggregate throughput across all clients.</summary>
    public ThroughputMetrics? AggregateThroughput { get; init; }

    /// <summary>Time for first client to receive message.</summary>
    public double FirstDeliveryMicroseconds { get; init; }

    /// <summary>Time for last client to receive message.</summary>
    public double LastDeliveryMicroseconds { get; init; }

    /// <summary>Spread between first and last delivery.</summary>
    public double DeliverySpreadMicroseconds => LastDeliveryMicroseconds - FirstDeliveryMicroseconds;
}

/// <summary>
/// Memory and GC metrics from a benchmark run.
/// </summary>
public sealed class MemoryMetrics
{
    /// <summary>Bytes allocated during the run.</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Gen0 garbage collections.</summary>
    public int Gen0Collections { get; init; }

    /// <summary>Gen1 garbage collections.</summary>
    public int Gen1Collections { get; init; }

    /// <summary>Gen2 garbage collections.</summary>
    public int Gen2Collections { get; init; }

    /// <summary>Peak working set in bytes.</summary>
    public long PeakWorkingSetBytes { get; init; }
}

/// <summary>
/// Stress test metrics from a benchmark run.
/// </summary>
public sealed class StressMetrics
{
    /// <summary>Total operations performed.</summary>
    public long TotalOperations { get; init; }

    /// <summary>Operations per second.</summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>Total duration of the stress test.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of errors encountered.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Connection latency metrics (for churn tests).</summary>
    public LatencyMetrics? ConnectLatency { get; init; }

    /// <summary>Disconnect latency metrics (for churn tests).</summary>
    public LatencyMetrics? DisconnectLatency { get; init; }

    /// <summary>Memory metrics during stress test.</summary>
    public MemoryMetrics? MemoryMetrics { get; init; }

    /// <summary>Pause count (for backpressure tests).</summary>
    public int PauseCount { get; init; }

    /// <summary>Average pause duration in microseconds.</summary>
    public double AveragePauseMicroseconds { get; init; }
}

/// <summary>
/// Collection synchronization metrics from a benchmark run.
/// </summary>
public sealed class CollectionMetrics
{
    /// <summary>Number of items in the collection.</summary>
    public int ItemCount { get; init; }

    /// <summary>Time to sync the initial collection (milliseconds).</summary>
    public double SyncTimeMs { get; init; }

    /// <summary>Operations per second during update tests.</summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>Total operations performed.</summary>
    public long TotalOperations { get; init; }

    /// <summary>Duration of the test.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Latency for update propagation (server change → client notification).</summary>
    public LatencyMetrics? UpdateLatency { get; init; }

    /// <summary>Memory metrics during collection operations.</summary>
    public MemoryMetrics? MemoryMetrics { get; init; }

    /// <summary>Number of errors encountered.</summary>
    public int ErrorCount { get; init; }
}
