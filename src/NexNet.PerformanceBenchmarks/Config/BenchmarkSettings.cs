namespace NexNet.PerformanceBenchmarks.Config;

/// <summary>
/// Runtime configuration for benchmark execution.
/// </summary>
public class BenchmarkSettings
{
    /// <summary>
    /// Number of warmup iterations before measurements.
    /// </summary>
    public int WarmupIterations { get; set; } = 3;

    /// <summary>
    /// Number of measured iterations for statistics.
    /// </summary>
    public int MeasuredIterations { get; set; } = 15;

    /// <summary>
    /// Timeout for a single iteration.
    /// </summary>
    public TimeSpan IterationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Force garbage collection between iterations.
    /// </summary>
    public bool ForceGCBetweenIterations { get; set; } = true;

    /// <summary>
    /// Duration for throughput measurement scenarios.
    /// </summary>
    public TimeSpan ThroughputDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Base port number for TCP-based transports.
    /// </summary>
    public int BasePort { get; set; } = 15000;

    /// <summary>
    /// Random seed for deterministic payload generation.
    /// </summary>
    public int RandomSeed { get; set; } = 42;

    /// <summary>
    /// Output directory for results.
    /// </summary>
    public string OutputDirectory { get; set; } = "Results";

    /// <summary>
    /// Creates settings configured for quick validation runs.
    /// </summary>
    public static BenchmarkSettings CreateQuick()
    {
        return new BenchmarkSettings
        {
            WarmupIterations = 1,
            MeasuredIterations = 5,
            ThroughputDuration = TimeSpan.FromSeconds(3),
            ForceGCBetweenIterations = false
        };
    }

    /// <summary>
    /// Creates settings configured for full benchmark runs.
    /// </summary>
    public static BenchmarkSettings CreateFull()
    {
        return new BenchmarkSettings
        {
            WarmupIterations = 3,
            MeasuredIterations = 15,
            ThroughputDuration = TimeSpan.FromSeconds(10),
            ForceGCBetweenIterations = true
        };
    }
}
