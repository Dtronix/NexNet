using System.Text.Json;
using System.Text.Json.Serialization;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Scenarios;

namespace NexNet.PerformanceBenchmarks.Reporting;

/// <summary>
/// Generates JSON reports from benchmark runs.
/// </summary>
public sealed class JsonReporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Generates a JSON report from a benchmark run.
    /// </summary>
    public static async Task WriteReportAsync(BenchmarkRun run, string outputPath, CancellationToken cancellationToken = default)
    {
        var report = CreateReport(run);
        var json = JsonSerializer.Serialize(report, SerializerOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
    }

    /// <summary>
    /// Generates a JSON string from a benchmark run.
    /// </summary>
    public static string GenerateReport(BenchmarkRun run)
    {
        var report = CreateReport(run);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    private static JsonBenchmarkReport CreateReport(BenchmarkRun run)
    {
        return new JsonBenchmarkReport
        {
            Metadata = new JsonMetadata
            {
                CommitHash = run.CommitHash,
                CommitMessage = run.CommitMessage,
                Branch = run.Branch,
                Timestamp = run.StartTime,
                Runtime = run.RuntimeVersion,
                Os = run.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                TotalDurationSeconds = run.Duration.TotalSeconds
            },
            Settings = new JsonSettings
            {
                WarmupIterations = run.Settings.WarmupIterations,
                MeasuredIterations = run.Settings.MeasuredIterations,
                ThroughputDurationSeconds = run.Settings.ThroughputDuration.TotalSeconds,
                ForceGcBetweenIterations = run.Settings.ForceGCBetweenIterations
            },
            Summary = new JsonSummary
            {
                TotalScenarios = run.Results.Count,
                SuccessfulScenarios = run.Results.Count(r => r.Success),
                FailedScenarios = run.Results.Count(r => !r.Success),
                ByCategory = run.Results
                    .GroupBy(r => r.Category)
                    .ToDictionary(
                        g => g.Key.ToString(),
                        g => new JsonCategorySummary
                        {
                            Total = g.Count(),
                            Passed = g.Count(r => r.Success),
                            Failed = g.Count(r => !r.Success)
                        })
            },
            Scenarios = run.Results.Select(r => new JsonScenarioResult
            {
                Name = r.ScenarioName,
                Category = r.Category.ToString(),
                Transport = r.Transport.ToString(),
                PayloadSize = r.PayloadSize?.ToString(),
                Success = r.Success,
                ErrorMessage = r.ErrorMessage,
                Latency = r.LatencyMetrics != null ? new JsonLatencyMetrics
                {
                    MeanMicroseconds = r.LatencyMetrics.MeanMicroseconds,
                    StdDevMicroseconds = r.LatencyMetrics.StdDevMicroseconds,
                    MinMicroseconds = r.LatencyMetrics.MinMicroseconds,
                    MaxMicroseconds = r.LatencyMetrics.MaxMicroseconds,
                    P50Microseconds = r.LatencyMetrics.P50Microseconds,
                    P95Microseconds = r.LatencyMetrics.P95Microseconds,
                    P99Microseconds = r.LatencyMetrics.P99Microseconds,
                    IterationCount = r.LatencyMetrics.IterationCount
                } : null,
                Throughput = r.ThroughputMetrics != null ? new JsonThroughputMetrics
                {
                    MegabytesPerSecond = r.ThroughputMetrics.MegabytesPerSecond,
                    MessagesPerSecond = r.ThroughputMetrics.MessagesPerSecond,
                    OperationsPerSecond = r.ThroughputMetrics.OperationsPerSecond,
                    TotalBytes = r.ThroughputMetrics.TotalBytes,
                    TotalMessages = r.ThroughputMetrics.TotalMessages,
                    DurationSeconds = r.ThroughputMetrics.Duration.TotalSeconds
                } : null,
                Scalability = r.ScalabilityMetrics != null ? new JsonScalabilityMetrics
                {
                    ClientCount = r.ScalabilityMetrics.ClientCount,
                    FirstDeliveryMicroseconds = r.ScalabilityMetrics.FirstDeliveryMicroseconds,
                    LastDeliveryMicroseconds = r.ScalabilityMetrics.LastDeliveryMicroseconds,
                    DeliverySpreadMicroseconds = r.ScalabilityMetrics.DeliverySpreadMicroseconds,
                    PerClientLatency = r.ScalabilityMetrics.PerClientLatency != null ? new JsonLatencyMetrics
                    {
                        MeanMicroseconds = r.ScalabilityMetrics.PerClientLatency.MeanMicroseconds,
                        StdDevMicroseconds = r.ScalabilityMetrics.PerClientLatency.StdDevMicroseconds,
                        MinMicroseconds = r.ScalabilityMetrics.PerClientLatency.MinMicroseconds,
                        MaxMicroseconds = r.ScalabilityMetrics.PerClientLatency.MaxMicroseconds,
                        P50Microseconds = r.ScalabilityMetrics.PerClientLatency.P50Microseconds,
                        P95Microseconds = r.ScalabilityMetrics.PerClientLatency.P95Microseconds,
                        P99Microseconds = r.ScalabilityMetrics.PerClientLatency.P99Microseconds,
                        IterationCount = r.ScalabilityMetrics.PerClientLatency.IterationCount
                    } : null
                } : null,
                Stress = r.StressMetrics != null ? new JsonStressMetrics
                {
                    TotalOperations = r.StressMetrics.TotalOperations,
                    OperationsPerSecond = r.StressMetrics.OperationsPerSecond,
                    DurationSeconds = r.StressMetrics.Duration.TotalSeconds,
                    ErrorCount = r.StressMetrics.ErrorCount,
                    PauseCount = r.StressMetrics.PauseCount,
                    AveragePauseMicroseconds = r.StressMetrics.AveragePauseMicroseconds
                } : null,
                Collection = r.CollectionMetrics != null ? new JsonCollectionMetrics
                {
                    ItemCount = r.CollectionMetrics.ItemCount,
                    SyncTimeMs = r.CollectionMetrics.SyncTimeMs,
                    OperationsPerSecond = r.CollectionMetrics.OperationsPerSecond,
                    TotalOperations = r.CollectionMetrics.TotalOperations,
                    DurationSeconds = r.CollectionMetrics.Duration.TotalSeconds,
                    ErrorCount = r.CollectionMetrics.ErrorCount
                } : null,
                Memory = r.MemoryMetrics != null ? new JsonMemoryMetrics
                {
                    AllocatedBytes = r.MemoryMetrics.AllocatedBytes,
                    Gen0Collections = r.MemoryMetrics.Gen0Collections,
                    Gen1Collections = r.MemoryMetrics.Gen1Collections,
                    Gen2Collections = r.MemoryMetrics.Gen2Collections,
                    PeakWorkingSetBytes = r.MemoryMetrics.PeakWorkingSetBytes
                } : null,
                CustomMetrics = r.CustomMetrics
            }).ToList()
        };
    }
}

#region JSON Models

internal sealed class JsonBenchmarkReport
{
    public required JsonMetadata Metadata { get; init; }
    public required JsonSettings Settings { get; init; }
    public required JsonSummary Summary { get; init; }
    public required List<JsonScenarioResult> Scenarios { get; init; }
}

internal sealed class JsonMetadata
{
    public string? CommitHash { get; init; }
    public string? CommitMessage { get; init; }
    public string? Branch { get; init; }
    public DateTime Timestamp { get; init; }
    public string? Runtime { get; init; }
    public string? Os { get; init; }
    public int ProcessorCount { get; init; }
    public double TotalDurationSeconds { get; init; }
}

internal sealed class JsonSettings
{
    public int WarmupIterations { get; init; }
    public int MeasuredIterations { get; init; }
    public double ThroughputDurationSeconds { get; init; }
    public bool ForceGcBetweenIterations { get; init; }
}

internal sealed class JsonSummary
{
    public int TotalScenarios { get; init; }
    public int SuccessfulScenarios { get; init; }
    public int FailedScenarios { get; init; }
    public Dictionary<string, JsonCategorySummary>? ByCategory { get; init; }
}

internal sealed class JsonCategorySummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
}

internal sealed class JsonScenarioResult
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Transport { get; init; }
    public string? PayloadSize { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonLatencyMetrics? Latency { get; init; }
    public JsonThroughputMetrics? Throughput { get; init; }
    public JsonScalabilityMetrics? Scalability { get; init; }
    public JsonStressMetrics? Stress { get; init; }
    public JsonCollectionMetrics? Collection { get; init; }
    public JsonMemoryMetrics? Memory { get; init; }
    public Dictionary<string, object>? CustomMetrics { get; init; }
}

internal sealed class JsonLatencyMetrics
{
    public double MeanMicroseconds { get; init; }
    public double StdDevMicroseconds { get; init; }
    public double MinMicroseconds { get; init; }
    public double MaxMicroseconds { get; init; }
    public double P50Microseconds { get; init; }
    public double P95Microseconds { get; init; }
    public double P99Microseconds { get; init; }
    public int IterationCount { get; init; }
}

internal sealed class JsonThroughputMetrics
{
    public double MegabytesPerSecond { get; init; }
    public double MessagesPerSecond { get; init; }
    public double OperationsPerSecond { get; init; }
    public long TotalBytes { get; init; }
    public long TotalMessages { get; init; }
    public double DurationSeconds { get; init; }
}

internal sealed class JsonScalabilityMetrics
{
    public int ClientCount { get; init; }
    public double FirstDeliveryMicroseconds { get; init; }
    public double LastDeliveryMicroseconds { get; init; }
    public double DeliverySpreadMicroseconds { get; init; }
    public JsonLatencyMetrics? PerClientLatency { get; init; }
}

internal sealed class JsonStressMetrics
{
    public long TotalOperations { get; init; }
    public double OperationsPerSecond { get; init; }
    public double DurationSeconds { get; init; }
    public int ErrorCount { get; init; }
    public int PauseCount { get; init; }
    public double AveragePauseMicroseconds { get; init; }
}

internal sealed class JsonCollectionMetrics
{
    public int ItemCount { get; init; }
    public double SyncTimeMs { get; init; }
    public double OperationsPerSecond { get; init; }
    public long TotalOperations { get; init; }
    public double DurationSeconds { get; init; }
    public int ErrorCount { get; init; }
}

internal sealed class JsonMemoryMetrics
{
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long PeakWorkingSetBytes { get; init; }
}

#endregion
