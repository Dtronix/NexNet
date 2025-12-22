# NexNet Comprehensive Performance Benchmark Suite

## Overview

This document proposes a new benchmark project (`NexNet.PerformanceBenchmarks`) designed for **real-world scenario testing** with **exact repeatability** to enable performance tracking across versions. The benchmark suite targets a **~30 minute runtime** and outputs results in **JSON + Markdown** format for automated comparison and graphing.

---

## Goals

1. **Version-to-Version Tracking**: Compare performance across git commits using commit hashes as version identifiers
2. **Real-World Scenarios**: Test actual usage patterns, not micro-benchmarks
3. **Exact Repeatability**: Deterministic test conditions for reliable comparison
4. **Automated Regression Detection**: Auto-compare against baseline with configurable thresholds
5. **Comprehensive Transport Coverage**: All 6 transport types (UDS, TCP, TLS, WebSocket, HttpSocket, QUIC)

---

## Architecture

### Project Structure

```
src/
└── NexNet.PerformanceBenchmarks/
    ├── NexNet.PerformanceBenchmarks.csproj
    ├── Program.cs                      # Entry point with CLI options
    ├── Config/
    │   ├── BenchmarkSettings.cs        # Runtime configuration
    │   └── TransportFactory.cs         # Transport configuration factory
    ├── Scenarios/
    │   ├── IScenario.cs                # Base scenario interface
    │   ├── Latency/
    │   │   ├── InvocationLatencyScenario.cs
    │   │   ├── PipeLatencyScenario.cs
    │   │   └── ChannelLatencyScenario.cs
    │   ├── Throughput/
    │   │   ├── PipeThroughputScenario.cs
    │   │   ├── ChannelThroughputScenario.cs
    │   │   └── InvocationThroughputScenario.cs
    │   ├── Scalability/
    │   │   ├── MultiClientScenario.cs
    │   │   ├── BroadcastScenario.cs
    │   │   └── GroupMessagingScenario.cs
    │   ├── Stress/
    │   │   ├── ConnectionChurnScenario.cs
    │   │   ├── MemoryPressureScenario.cs
    │   │   └── BackpressureScenario.cs
    │   ├── Collections/
    │   │   ├── LargeListSyncScenario.cs
    │   │   └── HighFrequencyUpdateScenario.cs
    │   └── Overhead/
    │       ├── AuthenticationOverheadScenario.cs
    │       ├── CancellationOverheadScenario.cs
    │       └── ReconnectionScenario.cs
    ├── Nexuses/
    │   ├── BenchmarkNexuses.cs         # Nexus definitions for benchmarks
    │   └── BenchmarkInterfaces.cs      # Interface contracts
    ├── Reporting/
    │   ├── JsonReporter.cs             # JSON output for graphing
    │   ├── MarkdownReporter.cs         # Human-readable summary
    │   └── ComparisonEngine.cs         # Baseline comparison logic
    ├── Infrastructure/
    │   ├── ScenarioRunner.cs           # Orchestrates scenario execution
    │   ├── MetricsCollector.cs         # Collects timing, memory, GC stats
    │   ├── WarmupManager.cs            # Handles warmup iterations
    │   └── StatisticsCalculator.cs     # Mean, StdDev, percentiles
    └── Results/                        # Output directory (gitignored)
        ├── {commit-hash}/
        │   ├── results.json
        │   ├── summary.md
        │   └── comparison.md
        └── baseline.json               # Reference baseline
```

---

## Benchmark Scenarios

### Category 1: Latency Benchmarks (~5 minutes)

Measures round-trip time for individual operations.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Invocation Latency** | Single method invocation round-trip | P50, P95, P99, Mean (μs) |
| **Pipe Latency** | Time to send/receive single pipe message | P50, P95, P99, Mean (μs) |
| **Channel Latency** | Typed channel single-item round-trip | P50, P95, P99, Mean (μs) |

**Payload Variants per Scenario:**
- Tiny: 1 byte
- Small: 1 KB
- Medium: 64 KB
- Large: 1 MB
- XLarge: 10 MB

**Transports:** All 6 (UDS, TCP, TLS, WebSocket, HttpSocket, QUIC)

---

### Category 2: Throughput Benchmarks (~8 minutes)

Measures sustained data transfer rates.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Pipe Throughput** | Sustained duplex pipe transfer | MB/s, Messages/s |
| **Channel Throughput** | Typed channel streaming rate | Items/s, MB/s |
| **Invocation Throughput** | Rapid-fire RPC calls | Invocations/s |

**Test Parameters:**
- Duration: 10 seconds per transport/payload combination
- Payload sizes: 1KB, 64KB, 1MB
- Concurrent streams: 1, 4, 8

---

### Category 3: Scalability Benchmarks (~6 minutes)

Measures performance under concurrent load.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Multi-Client Load** | N clients invoking server simultaneously | Latency degradation, Throughput |
| **Broadcast All Clients** | Server broadcasting to 10/50/100 clients | Delivery time, Messages/s |
| **Group Messaging** | Targeted group broadcasts with dynamic membership | Group ops/s, Delivery time |

**Client Counts:** 10, 25, 50, 100

---

### Category 4: Stress Benchmarks (~5 minutes)

Measures behavior under adverse conditions.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Connection Churn** | Rapid connect/disconnect cycles | Connections/s, Memory stability |
| **Memory Pressure** | Long-running operations measuring GC | Gen0/1/2 collections, Heap size |
| **Backpressure** | Slow consumer with fast producer | Pause frequency, Recovery time |

**Stress Parameters:**
- Connection churn: 1000 connect/disconnect cycles
- Memory pressure: 5-minute sustained load
- Backpressure: Producer 10x faster than consumer

---

### Category 5: Collection Synchronization (~4 minutes)

Measures INexusList<T> synchronization performance.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Large List Sync** | Sync lists with 1K/10K/100K items | Sync time, Memory |
| **High-Frequency Updates** | 100+ operations/second | Ops/s throughput, Latency |

**Operations:** Add, Insert, Remove, RemoveAt, Replace, Move, Clear

---

### Category 6: Overhead Benchmarks (~2 minutes)

Measures cost of optional features.

| Scenario | Description | Metrics |
|----------|-------------|---------|
| **Authentication** | Authenticated vs unauthenticated connections | Connection time delta |
| **Cancellation** | CancellationToken propagation cost | Invocation overhead (μs) |
| **Reconnection** | Time to reconnect after disconnect | Recovery time (ms) |

---

## Payload Definitions

```csharp
// Tiny payload - 1 byte
public readonly struct TinyPayload { public byte Value; }

// Small payload - ~1 KB
[MemoryPackable]
public partial class SmallPayload
{
    public int Id;
    public long Timestamp;
    public byte[] Data; // 1000 bytes
}

// Medium payload - ~64 KB
[MemoryPackable]
public partial class MediumPayload
{
    public Guid CorrelationId;
    public string Metadata;
    public byte[] Data; // 65000 bytes
}

// Large payload - ~1 MB
[MemoryPackable]
public partial class LargePayload
{
    public byte[] Data; // 1MB
    public Dictionary<string, string> Headers;
}

// XLarge payload - ~10 MB
[MemoryPackable]
public partial class XLargePayload
{
    public byte[] Data; // 10MB
}
```

---

## Execution Model

### Warmup & Iterations

```csharp
public class ScenarioExecutionConfig
{
    public int WarmupIterations { get; set; } = 3;
    public int MeasuredIterations { get; set; } = 15;
    public TimeSpan IterationTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public bool ForceGCBetweenIterations { get; set; } = true;
}
```

### Determinism Requirements

1. **Fixed Random Seeds**: All random data generation uses deterministic seeds
2. **Sequential Transport Testing**: Transports tested one at a time to avoid port conflicts
3. **GC Stabilization**: Force GC between scenarios for consistent baseline
4. **Process Isolation**: Optional mode to run each scenario in separate process

---

## Output Format

### JSON Results (`results.json`)

```json
{
  "metadata": {
    "commitHash": "a1b2c3d4e5f6",
    "commitMessage": "Fix memory leak in pipe handler",
    "branch": "master",
    "timestamp": "2025-12-21T10:30:00Z",
    "runtime": ".NET 9.0.0",
    "os": "Windows 11",
    "cpuModel": "AMD Ryzen 9 5900X",
    "totalDurationSeconds": 1847
  },
  "scenarios": [
    {
      "name": "InvocationLatency",
      "category": "Latency",
      "transport": "Uds",
      "payloadSize": "1KB",
      "metrics": {
        "meanMicroseconds": 45.2,
        "stdDevMicroseconds": 3.1,
        "p50Microseconds": 44.0,
        "p95Microseconds": 51.2,
        "p99Microseconds": 58.7,
        "minMicroseconds": 41.3,
        "maxMicroseconds": 67.8,
        "iterationCount": 15
      },
      "memoryMetrics": {
        "allocatedBytes": 2048,
        "gen0Collections": 0,
        "gen1Collections": 0,
        "gen2Collections": 0
      }
    }
  ]
}
```

### Markdown Summary (`summary.md`)

```markdown
# Benchmark Results - a1b2c3d

**Commit**: a1b2c3d4e5f6 - Fix memory leak in pipe handler
**Date**: 2025-12-21 10:30:00 UTC
**Duration**: 30m 47s

## Latency Benchmarks

| Scenario | Transport | Payload | P50 (μs) | P95 (μs) | P99 (μs) |
|----------|-----------|---------|----------|----------|----------|
| Invocation | UDS | 1KB | 44.0 | 51.2 | 58.7 |
| Invocation | TCP | 1KB | 89.3 | 102.1 | 118.4 |
| Invocation | QUIC | 1KB | 156.2 | 178.9 | 201.3 |
...

## Throughput Benchmarks

| Scenario | Transport | Payload | Rate | Unit |
|----------|-----------|---------|------|------|
| Pipe | UDS | 64KB | 2,847 | MB/s |
| Channel | UDS | 64KB | 185,421 | items/s |
...
```

### Comparison Report (`comparison.md`)

```markdown
# Performance Comparison

**Current**: a1b2c3d4 vs **Baseline**: 9f8e7d6c

## Regressions (>5% slower)

| Scenario | Transport | Metric | Baseline | Current | Delta |
|----------|-----------|--------|----------|---------|-------|
| PipeThroughput | TCP | MB/s | 1,234 | 1,109 | -10.1% |

## Improvements (>5% faster)

| Scenario | Transport | Metric | Baseline | Current | Delta |
|----------|-----------|--------|----------|---------|-------|
| InvocationLatency | UDS | P99 | 67.2μs | 58.7μs | +12.6% |

## Within Tolerance (±5%)

| Scenario | Transport | Metric | Delta |
|----------|-----------|--------|-------|
| ChannelThroughput | QUIC | items/s | +2.1% |
...
```

---

## CLI Interface

```bash
# Run full benchmark suite
dotnet run --project src/NexNet.PerformanceBenchmarks -- --full

# Run specific category
dotnet run --project src/NexNet.PerformanceBenchmarks -- --category Latency

# Run specific scenario
dotnet run --project src/NexNet.PerformanceBenchmarks -- --scenario InvocationLatency

# Run specific transport only
dotnet run --project src/NexNet.PerformanceBenchmarks -- --transport Uds,Tcp

# Compare against baseline
dotnet run --project src/NexNet.PerformanceBenchmarks -- --compare baseline.json

# Set new baseline
dotnet run --project src/NexNet.PerformanceBenchmarks -- --set-baseline

# Quick validation run (fewer iterations)
dotnet run --project src/NexNet.PerformanceBenchmarks -- --quick

# Output to specific directory
dotnet run --project src/NexNet.PerformanceBenchmarks -- --output ./my-results
```

---

## Comparison Engine

### Regression Detection

```csharp
public class ComparisonConfig
{
    // Threshold for flagging regression
    public double RegressionThresholdPercent { get; set; } = 5.0;

    // Threshold for flagging improvement
    public double ImprovementThresholdPercent { get; set; } = 5.0;

    // Minimum iterations for statistical significance
    public int MinIterationsForComparison { get; set; } = 10;

    // Use P95 instead of mean for comparison (more stable)
    public bool UsePercentileForComparison { get; set; } = true;
    public int ComparisonPercentile { get; set; } = 95;
}
```

### Automatic Baseline Selection

1. If `--compare` specified, use that file
2. If `baseline.json` exists in Results folder, use that
3. If no baseline, output results-only (no comparison)

---

## Scenario Details

### 1. Invocation Latency Scenario

```csharp
public class InvocationLatencyScenario : IScenario
{
    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        // Setup: Single client connected to server
        var (client, server, serverNexus) = await SetupAsync(context.Transport);

        var measurements = new List<double>();

        // Warmup
        for (int i = 0; i < context.Config.WarmupIterations; i++)
            await client.Proxy.Echo(context.Payload);

        // Measured iterations
        for (int i = 0; i < context.Config.MeasuredIterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await client.Proxy.Echo(context.Payload);
            sw.Stop();
            measurements.Add(sw.Elapsed.TotalMicroseconds);
        }

        return ScenarioResult.FromMeasurements(measurements);
    }
}
```

### 2. Pipe Throughput Scenario

```csharp
public class PipeThroughputScenario : IScenario
{
    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var (client, server, serverNexus) = await SetupAsync(context.Transport);

        var pipe = await client.Proxy.CreatePipe();
        var data = context.GeneratePayload();

        var sw = Stopwatch.StartNew();
        long totalBytes = 0;

        // Stream for fixed duration
        var cts = new CancellationTokenSource(context.Config.ThroughputDuration);

        while (!cts.IsCancellationRequested)
        {
            await pipe.Writer.WriteAsync(data);
            totalBytes += data.Length;
        }

        sw.Stop();

        return new ScenarioResult
        {
            ThroughputMBps = totalBytes / sw.Elapsed.TotalSeconds / (1024 * 1024),
            TotalBytes = totalBytes
        };
    }
}
```

### 3. Multi-Client Load Scenario

```csharp
public class MultiClientScenario : IScenario
{
    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var server = await StartServerAsync(context.Transport);
        var clients = new List<NexusClient>();

        // Connect N clients
        for (int i = 0; i < context.ClientCount; i++)
            clients.Add(await CreateClientAsync(context.Transport));

        // All clients invoke simultaneously
        var tasks = clients.Select(async c =>
        {
            var sw = Stopwatch.StartNew();
            await c.Proxy.Echo(context.Payload);
            return sw.Elapsed.TotalMicroseconds;
        });

        var latencies = await Task.WhenAll(tasks);

        return ScenarioResult.FromMeasurements(latencies);
    }
}
```

### 4. Connection Churn Scenario

```csharp
public class ConnectionChurnScenario : IScenario
{
    private const int ChurnCycles = 1000;

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var server = await StartServerAsync(context.Transport);

        var sw = Stopwatch.StartNew();
        var connectTimes = new List<double>();
        var disconnectTimes = new List<double>();

        for (int i = 0; i < ChurnCycles; i++)
        {
            var connectSw = Stopwatch.StartNew();
            var client = await CreateClientAsync(context.Transport);
            connectSw.Stop();
            connectTimes.Add(connectSw.Elapsed.TotalMicroseconds);

            var disconnectSw = Stopwatch.StartNew();
            await client.DisconnectAsync();
            disconnectSw.Stop();
            disconnectTimes.Add(disconnectSw.Elapsed.TotalMicroseconds);
        }

        sw.Stop();

        return new ScenarioResult
        {
            ConnectionsPerSecond = ChurnCycles / sw.Elapsed.TotalSeconds,
            ConnectLatency = StatisticsCalculator.Calculate(connectTimes),
            DisconnectLatency = StatisticsCalculator.Calculate(disconnectTimes)
        };
    }
}
```

### 5. Backpressure Scenario

```csharp
public class BackpressureScenario : IScenario
{
    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var (client, server, serverNexus) = await SetupAsync(context.Transport);
        var pipe = await client.Proxy.CreatePipe();

        long producedBytes = 0;
        long consumedBytes = 0;
        int pauseCount = 0;

        // Fast producer (10x consumer speed)
        var producer = Task.Run(async () =>
        {
            var data = new byte[64 * 1024]; // 64KB chunks
            while (producedBytes < 100 * 1024 * 1024) // 100MB total
            {
                var result = await pipe.Writer.WriteAsync(data);
                if (result.IsPaused)
                    Interlocked.Increment(ref pauseCount);
                Interlocked.Add(ref producedBytes, data.Length);
            }
        });

        // Slow consumer
        var consumer = Task.Run(async () =>
        {
            while (consumedBytes < 100 * 1024 * 1024)
            {
                var result = await pipe.Reader.ReadAsync();
                Interlocked.Add(ref consumedBytes, result.Buffer.Length);
                await Task.Delay(10); // Artificial slowdown
            }
        });

        await Task.WhenAll(producer, consumer);

        return new ScenarioResult
        {
            PauseCount = pauseCount,
            ProducerThroughput = producedBytes,
            ConsumerThroughput = consumedBytes
        };
    }
}
```

### 6. Large List Sync Scenario

```csharp
public class LargeListSyncScenario : IScenario
{
    private readonly int[] _listSizes = { 1_000, 10_000, 100_000 };

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var results = new Dictionary<int, double>();

        foreach (var size in _listSizes)
        {
            var (client, server, serverNexus) = await SetupAsync(context.Transport);

            // Populate server list
            for (int i = 0; i < size; i++)
                serverNexus.SyncList.Add(new TestItem { Id = i, Value = $"Item_{i}" });

            // Measure sync time
            var sw = Stopwatch.StartNew();
            await client.Proxy.SyncList.ConnectAsync();
            await client.Proxy.SyncList.ReadyTask;
            sw.Stop();

            results[size] = sw.Elapsed.TotalMilliseconds;

            await CleanupAsync(client, server);
        }

        return new ScenarioResult { ListSyncTimes = results };
    }
}
```

---

## Transport Matrix

Each scenario runs across all transports unless specified otherwise:

| Transport | Latency | Throughput | Scalability | Stress | Collections | Overhead |
|-----------|---------|------------|-------------|--------|-------------|----------|
| UDS       | ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |
| TCP       | ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |
| TLS       | ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |
| WebSocket | ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |
| HttpSocket| ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |
| QUIC      | ✅      | ✅         | ✅          | ✅     | ✅          | ✅       |

---

## Time Budget

| Category | Scenarios | Time per Transport | Transports | Total |
|----------|-----------|-------------------|------------|-------|
| Latency | 3 × 5 payloads | ~10s | 6 | ~5 min |
| Throughput | 3 × 3 payloads | ~15s | 6 | ~8 min |
| Scalability | 3 × 4 client counts | ~15s | 6 | ~6 min |
| Stress | 3 scenarios | ~30s | 6 | ~5 min |
| Collections | 2 × 3 sizes | ~20s | 6 | ~4 min |
| Overhead | 3 scenarios | ~15s | 6 | ~2 min |
| **Total** | | | | **~30 min** |

---

## Implementation Phases

### Phase 1: Core Infrastructure
- [ ] Project setup with CLI parsing
- [ ] Transport factory for all 6 transports
- [ ] Base scenario interface and runner
- [ ] Metrics collector (timing, memory, GC)
- [ ] Statistics calculator (mean, stddev, percentiles)

### Phase 2: Latency & Throughput Scenarios
- [ ] Invocation latency (all payloads)
- [ ] Pipe latency and throughput
- [ ] Channel latency and throughput
- [ ] Payload generators

### Phase 3: Scalability Scenarios
- [ ] Multi-client load scenario
- [ ] Broadcast all clients
- [ ] Group messaging

### Phase 4: Stress Scenarios
- [ ] Connection churn
- [ ] Memory pressure
- [ ] Backpressure

### Phase 5: Collections & Overhead
- [ ] Large list sync
- [ ] High-frequency updates
- [ ] Authentication overhead
- [ ] Cancellation overhead
- [ ] Reconnection scenario

### Phase 6: Reporting
- [ ] JSON reporter
- [ ] Markdown reporter
- [ ] Comparison engine
- [ ] Regression detection

---

## Dependencies

```xml
<PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
<PackageReference Include="System.Diagnostics.DiagnosticSource" Version="9.0.0" />
```

No BenchmarkDotNet - this is a custom harness for real-world scenarios with longer execution times.

---

## Usage Examples

### CI Integration

```yaml
# GitHub Actions example
benchmark:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4

    - name: Run benchmarks
      run: dotnet run --project src/NexNet.PerformanceBenchmarks -- --full --output ./results

    - name: Compare with baseline
      run: dotnet run --project src/NexNet.PerformanceBenchmarks -- --compare ./baseline.json --output ./results

    - name: Upload results
      uses: actions/upload-artifact@v4
      with:
        name: benchmark-results-${{ github.sha }}
        path: ./results/
```

### Local Development

```bash
# Quick validation during development
dotnet run --project src/NexNet.PerformanceBenchmarks -- --quick --scenario InvocationLatency --transport Uds

# Full run before merge
dotnet run --project src/NexNet.PerformanceBenchmarks -- --full --compare baseline.json

# Update baseline after optimization
dotnet run --project src/NexNet.PerformanceBenchmarks -- --full --set-baseline
```

---

## Open Questions

1. **Process Isolation**: Should scenarios run in separate processes for maximum isolation, or is GC between scenarios sufficient?

2. **Historical Storage**: Should results be automatically committed to a `benchmarks/` branch, or stored externally?

3. **Alert Thresholds**: What regression percentage should trigger CI failure? (Currently proposed: 10%)

4. **QUIC Availability**: QUIC requires specific OS/runtime support. Should it be optional with graceful skip?

5. **Parallelization**: Some scenarios (different transports) could run in parallel. Worth the complexity for faster total time?

---

## Summary

This benchmark suite provides:

- **30 distinct scenarios** across 6 categories
- **All 6 transports** tested comprehensively
- **5 payload sizes** from 1 byte to 10 MB
- **Automatic regression detection** with configurable thresholds
- **JSON + Markdown output** for graphing and human review
- **~30 minute runtime** for full suite
- **Git commit hash tracking** for version-to-version comparison

The design prioritizes **real-world scenarios** over micro-benchmarks, **repeatability** over raw speed, and **actionable insights** over raw numbers.
