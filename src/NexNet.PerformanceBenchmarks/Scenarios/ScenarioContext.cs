using NexNet.PerformanceBenchmarks.Config;
using NexNet.Transports;

namespace NexNet.PerformanceBenchmarks.Scenarios;

/// <summary>
/// Execution context for a benchmark scenario.
/// </summary>
public sealed class ScenarioContext : IDisposable
{
    private readonly Random _random;
    private bool _disposed;

    /// <summary>
    /// The transport type being benchmarked.
    /// </summary>
    public TransportType Transport { get; }

    /// <summary>
    /// The payload size being used.
    /// </summary>
    public PayloadSize PayloadSize { get; }

    /// <summary>
    /// The benchmark settings.
    /// </summary>
    public BenchmarkSettings Settings { get; }

    /// <summary>
    /// The server configuration for this transport.
    /// </summary>
    public ServerConfig ServerConfig { get; }

    /// <summary>
    /// The client configuration for this transport.
    /// </summary>
    public ClientConfig ClientConfig { get; }

    /// <summary>
    /// Number of clients for multi-client scenarios.
    /// </summary>
    public int ClientCount { get; init; } = 1;

    /// <summary>
    /// Creates a new scenario context.
    /// </summary>
    public ScenarioContext(
        TransportType transport,
        PayloadSize payloadSize,
        BenchmarkSettings settings,
        ServerConfig serverConfig,
        ClientConfig clientConfig)
    {
        Transport = transport;
        PayloadSize = payloadSize;
        Settings = settings;
        ServerConfig = serverConfig;
        ClientConfig = clientConfig;
        _random = new Random(settings.RandomSeed);
    }

    /// <summary>
    /// Generates a payload of the current size.
    /// </summary>
    public byte[] GeneratePayload()
    {
        var size = GetPayloadSizeBytes();
        var data = new byte[size];
        _random.NextBytes(data);
        return data;
    }

    /// <summary>
    /// Generates a payload of the specified size.
    /// </summary>
    public byte[] GeneratePayload(PayloadSize size)
    {
        var bytes = GetPayloadSizeBytes(size);
        var data = new byte[bytes];
        _random.NextBytes(data);
        return data;
    }

    /// <summary>
    /// Gets the byte size for the current payload size.
    /// </summary>
    public int GetPayloadSizeBytes()
    {
        return GetPayloadSizeBytes(PayloadSize);
    }

    /// <summary>
    /// Gets the byte size for a specific payload size.
    /// </summary>
    public static int GetPayloadSizeBytes(PayloadSize size)
    {
        return size switch
        {
            PayloadSize.Tiny => 1,
            PayloadSize.Small => 1024, // 1 KB
            PayloadSize.Medium => 64 * 1024, // 64 KB
            PayloadSize.Large => 1024 * 1024, // 1 MB
            PayloadSize.XLarge => 10 * 1024 * 1024, // 10 MB
            _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
        };
    }

    /// <summary>
    /// Gets the display string for the current payload size.
    /// </summary>
    public string GetPayloadSizeDisplay()
    {
        return PayloadSize switch
        {
            PayloadSize.Tiny => "1B",
            PayloadSize.Small => "1KB",
            PayloadSize.Medium => "64KB",
            PayloadSize.Large => "1MB",
            PayloadSize.XLarge => "10MB",
            _ => PayloadSize.ToString()
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Clean up any resources if needed
    }
}
