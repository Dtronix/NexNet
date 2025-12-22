using System.Diagnostics;
using MemoryPack;

namespace NexNet.PerformanceBenchmarks.Nexuses;

/// <summary>
/// Payload size categories for benchmark scenarios.
/// </summary>
public enum PayloadSizeCategory
{
    /// <summary>1 byte payload</summary>
    Tiny = 1,
    /// <summary>~1 KB payload</summary>
    Small = 1024,
    /// <summary>~64 KB payload</summary>
    Medium = 65536,
    /// <summary>~1 MB payload</summary>
    Large = 1048576,
    /// <summary>~10 MB payload</summary>
    XLarge = 10485760
}

/// <summary>
/// Factory for creating deterministic payloads for benchmarks.
/// Uses fixed random seeds for reproducibility.
/// </summary>
public static class PayloadFactory
{
    private const int DefaultSeed = 42;

    /// <summary>
    /// Creates a tiny payload (1 byte).
    /// </summary>
    public static TinyPayload CreateTiny(int seed = DefaultSeed)
    {
        return new TinyPayload { Value = (byte)(seed & 0xFF) };
    }

    /// <summary>
    /// Creates a small payload (~1 KB).
    /// </summary>
    public static SmallPayload CreateSmall(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var data = new byte[1000];
        random.NextBytes(data);

        return new SmallPayload
        {
            Id = seed,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = data
        };
    }

    /// <summary>
    /// Creates a medium payload (~64 KB).
    /// </summary>
    public static MediumPayload CreateMedium(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var data = new byte[65000];
        random.NextBytes(data);

        return new MediumPayload
        {
            CorrelationId = new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Metadata = $"Benchmark payload generated with seed {seed}",
            Data = data
        };
    }

    /// <summary>
    /// Creates a large payload (~1 MB).
    /// </summary>
    public static LargePayload CreateLarge(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var data = new byte[1024 * 1024]; // 1 MB
        random.NextBytes(data);

        return new LargePayload
        {
            Data = data,
            Headers = new Dictionary<string, string>
            {
                ["X-Benchmark-Seed"] = seed.ToString(),
                ["X-Benchmark-Size"] = "1MB",
                ["X-Benchmark-Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };
    }

    /// <summary>
    /// Creates an extra-large payload (~10 MB).
    /// </summary>
    public static XLargePayload CreateXLarge(int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        random.NextBytes(data);

        return new XLargePayload
        {
            Data = data
        };
    }

    /// <summary>
    /// Creates raw byte array payload of specified size.
    /// </summary>
    public static byte[] CreateRawPayload(int size, int seed = DefaultSeed)
    {
        var random = new Random(seed);
        var data = new byte[size];
        random.NextBytes(data);
        return data;
    }

    /// <summary>
    /// Creates raw byte array payload for a given payload size category.
    /// </summary>
    public static byte[] CreateRawPayload(PayloadSizeCategory category, int seed = DefaultSeed)
    {
        return CreateRawPayload((int)category, seed);
    }

    /// <summary>
    /// Gets the approximate size in bytes for a payload size category.
    /// </summary>
    public static int GetPayloadSize(PayloadSizeCategory category) => (int)category;
}

/// <summary>
/// Tiny payload - 1 byte. Used for measuring minimal overhead.
/// </summary>
[MemoryPackable]
public partial struct TinyPayload
{
    /// <summary>Single byte value.</summary>
    public byte Value;
}

/// <summary>
/// Small payload - approximately 1 KB.
/// Represents typical small message payloads.
/// </summary>
[MemoryPackable]
public partial class SmallPayload
{
    /// <summary>Unique identifier.</summary>
    public int Id { get; set; }

    /// <summary>Timestamp in Unix milliseconds.</summary>
    public long Timestamp { get; set; }

    /// <summary>Payload data (~1000 bytes).</summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Medium payload - approximately 64 KB.
/// Represents typical document or file chunk payloads.
/// </summary>
[MemoryPackable]
public partial class MediumPayload
{
    /// <summary>Correlation identifier for tracking.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Optional metadata string.</summary>
    public string Metadata { get; set; } = string.Empty;

    /// <summary>Payload data (~65000 bytes).</summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Large payload - approximately 1 MB.
/// Represents larger file transfers or batch data.
/// </summary>
[MemoryPackable]
public partial class LargePayload
{
    /// <summary>Payload data (~1 MB).</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Optional headers/metadata.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}

/// <summary>
/// Extra-large payload - approximately 10 MB.
/// Used for testing large data transfer scenarios.
/// </summary>
[MemoryPackable]
public partial class XLargePayload
{
    /// <summary>Payload data (~10 MB).</summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Test item used in collection synchronization benchmarks.
/// </summary>
[MemoryPackable]
public partial class TestItem
{
    /// <summary>Unique item identifier.</summary>
    public int Id { get; set; }

    /// <summary>Item value/name.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Creation timestamp.</summary>
    public long Timestamp { get; set; }

    /// <summary>Optional additional data.</summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// Creates a test item with the specified id.
    /// </summary>
    public static TestItem Create(int id, int dataSize = 0)
    {
        var item = new TestItem
        {
            Id = id,
            Value = $"Item_{id}",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (dataSize > 0)
        {
            item.Data = new byte[dataSize];
            new Random(id).NextBytes(item.Data);
        }

        return item;
    }
}

/// <summary>
/// Channel item for throughput benchmarks with unmanaged types.
/// </summary>
[MemoryPackable]
public partial struct ChannelItem
{
    /// <summary>Sequence number.</summary>
    public long Sequence;

    /// <summary>Timestamp for latency measurement.</summary>
    public long TimestampTicks;

    /// <summary>
    /// Creates a new channel item with current timestamp.
    /// </summary>
    public static ChannelItem Create(long sequence)
    {
        return new ChannelItem
        {
            Sequence = sequence,
            TimestampTicks = Stopwatch.GetTimestamp()
        };
    }
}
