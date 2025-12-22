using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Scenarios;

namespace NexNet.PerformanceBenchmarks.Infrastructure;

/// <summary>
/// Collects timing, memory, and GC metrics during benchmark execution.
/// </summary>
public sealed class MetricsCollector
{
    private readonly Stopwatch _stopwatch = new();
    private long _startAllocatedBytes;
    private int _startGen0;
    private int _startGen1;
    private int _startGen2;

    /// <summary>
    /// Starts a new measurement session.
    /// </summary>
    public void Start()
    {
        // Force GC to get clean baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _startGen0 = GC.CollectionCount(0);
        _startGen1 = GC.CollectionCount(1);
        _startGen2 = GC.CollectionCount(2);

        _stopwatch.Restart();
    }

    /// <summary>
    /// Stops the measurement and returns elapsed time in microseconds.
    /// </summary>
    public double StopMicroseconds()
    {
        _stopwatch.Stop();
        return _stopwatch.Elapsed.TotalMicroseconds;
    }

    /// <summary>
    /// Stops the measurement and returns elapsed time in milliseconds.
    /// </summary>
    public double StopMilliseconds()
    {
        _stopwatch.Stop();
        return _stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Gets the elapsed time without stopping.
    /// </summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>
    /// Gets memory metrics since Start() was called.
    /// </summary>
    public MemoryMetrics GetMemoryMetrics()
    {
        var currentAllocated = GC.GetAllocatedBytesForCurrentThread();
        using var process = Process.GetCurrentProcess();

        return new MemoryMetrics
        {
            AllocatedBytes = currentAllocated - _startAllocatedBytes,
            Gen0Collections = GC.CollectionCount(0) - _startGen0,
            Gen1Collections = GC.CollectionCount(1) - _startGen1,
            Gen2Collections = GC.CollectionCount(2) - _startGen2,
            PeakWorkingSetBytes = process.PeakWorkingSet64
        };
    }

    /// <summary>
    /// Forces a full GC collection for clean measurement starts.
    /// </summary>
    public static void ForceGC()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    /// <summary>
    /// Takes a memory snapshot for later comparison.
    /// </summary>
    public static MemorySnapshot TakeSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new MemorySnapshot
        {
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread(),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            WorkingSetBytes = process.WorkingSet64,
            PrivateMemoryBytes = process.PrivateMemorySize64,
            Timestamp = Stopwatch.GetTimestamp()
        };
    }

    /// <summary>
    /// Calculates the difference between two memory snapshots.
    /// </summary>
    public static MemoryMetrics Diff(MemorySnapshot before, MemorySnapshot after)
    {
        using var process = Process.GetCurrentProcess();
        return new MemoryMetrics
        {
            AllocatedBytes = after.AllocatedBytes - before.AllocatedBytes,
            Gen0Collections = after.Gen0Collections - before.Gen0Collections,
            Gen1Collections = after.Gen1Collections - before.Gen1Collections,
            Gen2Collections = after.Gen2Collections - before.Gen2Collections,
            PeakWorkingSetBytes = process.PeakWorkingSet64
        };
    }
}

/// <summary>
/// A snapshot of memory state at a point in time.
/// </summary>
public readonly struct MemorySnapshot
{
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long Timestamp { get; init; }

    public TimeSpan ElapsedSince(MemorySnapshot other)
    {
        var ticks = Timestamp - other.Timestamp;
        return TimeSpan.FromTicks(ticks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }
}

/// <summary>
/// A high-precision timer for individual measurements.
/// </summary>
public struct PrecisionTimer
{
    private long _startTimestamp;

    /// <summary>
    /// Starts the timer.
    /// </summary>
    public void Start()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Gets the elapsed time in microseconds since Start().
    /// </summary>
    public readonly double ElapsedMicroseconds
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
            return (double)elapsed * 1_000_000 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// Gets the elapsed time in milliseconds since Start().
    /// </summary>
    public readonly double ElapsedMilliseconds
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
            return (double)elapsed * 1_000 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// Gets the elapsed TimeSpan since Start().
    /// </summary>
    public readonly TimeSpan Elapsed
    {
        get
        {
            var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
            return TimeSpan.FromTicks(elapsed * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Creates and starts a new timer.
    /// </summary>
    public static PrecisionTimer StartNew()
    {
        var timer = new PrecisionTimer();
        timer.Start();
        return timer;
    }
}
