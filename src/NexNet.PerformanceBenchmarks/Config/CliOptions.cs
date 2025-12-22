namespace NexNet.PerformanceBenchmarks.Config;

/// <summary>
/// Command-line options for the benchmark runner.
/// </summary>
public class CliOptions
{
    /// <summary>
    /// Run the complete benchmark suite.
    /// </summary>
    public bool Full { get; set; }

    /// <summary>
    /// Run with fewer iterations for quick validation.
    /// </summary>
    public bool Quick { get; set; }

    /// <summary>
    /// Run specific category (Latency, Throughput, Scalability, Stress, Collections, Overhead).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Run specific scenario by name.
    /// </summary>
    public string? Scenario { get; set; }

    /// <summary>
    /// Comma-separated list of transports to test (Uds, Tcp, Tls, WebSocket, HttpSocket, Quic).
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>
    /// Output directory path for results.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// Comma-separated payload sizes to test (Tiny, Small, Medium, Large, XLarge).
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// List scenarios without running them.
    /// </summary>
    public bool List { get; set; }

    /// <summary>
    /// Gets the parsed transport types from the Transport option.
    /// </summary>
    public IReadOnlyList<TransportType> GetTransportTypes()
    {
        if (string.IsNullOrWhiteSpace(Transport))
            return Enum.GetValues<TransportType>();

        var result = new List<TransportType>();
        foreach (var part in Transport.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TransportType>(part, ignoreCase: true, out var type))
                result.Add(type);
        }

        return result.Count > 0 ? result : Enum.GetValues<TransportType>();
    }

    /// <summary>
    /// Gets the parsed payload sizes from the Payload option.
    /// </summary>
    public IReadOnlyList<PayloadSize> GetPayloadSizes()
    {
        if (string.IsNullOrWhiteSpace(Payload))
            return Enum.GetValues<PayloadSize>();

        var result = new List<PayloadSize>();
        foreach (var part in Payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<PayloadSize>(part, ignoreCase: true, out var size))
                result.Add(size);
        }

        return result.Count > 0 ? result : Enum.GetValues<PayloadSize>();
    }
}

/// <summary>
/// Available transport types for benchmarking.
/// </summary>
public enum TransportType
{
    Uds,
    Tcp,
    Tls,
    WebSocket,
    HttpSocket,
    Quic
}

/// <summary>
/// Payload sizes for benchmarking.
/// </summary>
public enum PayloadSize
{
    /// <summary>1 byte</summary>
    Tiny,
    /// <summary>~1 KB</summary>
    Small,
    /// <summary>~64 KB</summary>
    Medium,
    /// <summary>~1 MB</summary>
    Large,
    /// <summary>~10 MB</summary>
    XLarge
}

/// <summary>
/// Benchmark categories.
/// </summary>
public enum BenchmarkCategory
{
    Latency,
    Throughput,
    Scalability,
    Stress,
    Collections,
    Overhead
}
