# NexNet Performance Benchmarks - Implementation Plan

## Document Information

| Field | Value |
|-------|-------|
| **Based On** | `benchmark1.md` |
| **Created** | 2025-12-21 |
| **Status** | Ready for Implementation |

---

## Clarified Requirements

### Project Setup
| Decision | Value |
|----------|-------|
| **Project Location** | `src/NexNet.PerformanceBenchmarks/` (in this repo) |
| **NexNet Reference** | Project reference to local `src/NexNet/` |
| **Target Framework** | .NET 9.0 |

### Execution Model
| Decision | Value |
|----------|-------|
| **Process Isolation** | GC between scenarios (no separate processes) |
| **Transport Parallelization** | Sequential (one transport at a time) |
| **Historical Storage** | External (not auto-committed) |
| **CI Integration** | Manual execution only (no CI thresholds) |
| **Platform** | Windows-only for now |

### Scope Exclusions
| Excluded | Reason |
|----------|--------|
| Authentication Overhead Scenario | Per user request |
| CI Regression Detection | Benchmarks run manually |
| Cross-platform support | Future enhancement |

### Transport Support
| Transport | Status |
|-----------|--------|
| UDS (Unix Domain Sockets) | Included |
| TCP | Included |
| TLS | Included |
| WebSocket | Included |
| HttpSocket | Included |
| QUIC | Included (graceful skip if unavailable) |

---

## NexNet API Reference Summary

Based on codebase exploration, here are the key APIs used in benchmarks:

### Nexus Definition Pattern
```csharp
// Shared interfaces
public interface IClientNexus { ValueTask ReceiveMessage(string msg); }
public interface IServerNexus { ValueTask<byte[]> Echo(byte[] data); }

// Client implementation
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class BenchmarkClientNexus : IClientNexus { }

// Server implementation
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class BenchmarkServerNexus : IServerNexus { }
```

### Transport Configuration
```csharp
// UDS
var udsConfig = new UdsServerConfig { Path = "benchmark.sock" };

// TCP
var tcpConfig = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, port) };

// TLS
var tlsConfig = new TcpTlsServerConfig {
    EndPoint = endpoint,
    SslServerAuthenticationOptions = new() { ServerCertificate = cert }
};

// QUIC
var quicConfig = new QuicServerConfig { EndPoint = endpoint, Certificate = cert };
```

### Reconnection API
```csharp
clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(
    new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1) },
    continuousRetry: false
);

// Lifecycle callbacks
protected override ValueTask OnReconnecting() { }
protected override ValueTask OnConnected(bool isReconnected) { }
```

### Pipe and Channel APIs
```csharp
// Duplex Pipe (byte streaming)
var pipe = client.CreatePipe();
await client.Proxy.StreamData(pipe);
await stream.CopyToAsync(pipe.Output);

// Typed Channel (MemoryPack serialized)
var channel = client.CreateChannel<MyData>();
var writer = await channel.GetWriterAsync();
await writer.WriteAsync(item);

// Unmanaged Channel (faster for primitives)
var channel = client.CreateUnmanagedChannel<int>();
```

### Group Messaging
```csharp
// Server-side broadcasting
await Context.Clients.All.Notify();
await Context.Clients.Group("room").Notify();
await Context.Groups.AddAsync("room");
```

### INexusList<T> Synchronization
```csharp
// Server-side
serverNexus.SyncList.Add(item);

// Client-side
await client.Proxy.SyncList.ConnectAsync();
await client.Proxy.SyncList.ReadyTask;
foreach (var item in client.Proxy.SyncList) { }
```

---

## Project Structure

```
src/
└── NexNet.PerformanceBenchmarks/
    ├── NexNet.PerformanceBenchmarks.csproj
    ├── Program.cs                          # CLI entry point
    │
    ├── Config/
    │   ├── BenchmarkSettings.cs            # Runtime configuration
    │   ├── CliOptions.cs                   # Command-line parsing
    │   └── TransportFactory.cs             # Transport configuration factory
    │
    ├── Scenarios/
    │   ├── IScenario.cs                    # Base scenario interface
    │   ├── ScenarioContext.cs              # Execution context
    │   ├── ScenarioResult.cs               # Result data structures
    │   │
    │   ├── Latency/
    │   │   ├── InvocationLatencyScenario.cs
    │   │   ├── PipeLatencyScenario.cs
    │   │   └── ChannelLatencyScenario.cs
    │   │
    │   ├── Throughput/
    │   │   ├── PipeThroughputScenario.cs
    │   │   ├── ChannelThroughputScenario.cs
    │   │   └── InvocationThroughputScenario.cs
    │   │
    │   ├── Scalability/
    │   │   ├── MultiClientScenario.cs
    │   │   ├── BroadcastScenario.cs
    │   │   └── GroupMessagingScenario.cs
    │   │
    │   ├── Stress/
    │   │   ├── ConnectionChurnScenario.cs
    │   │   ├── MemoryPressureScenario.cs
    │   │   └── BackpressureScenario.cs
    │   │
    │   ├── Collections/
    │   │   ├── LargeListSyncScenario.cs
    │   │   └── HighFrequencyUpdateScenario.cs
    │   │
    │   └── Overhead/
    │       ├── CancellationOverheadScenario.cs
    │       └── ReconnectionScenario.cs
    │
    ├── Nexuses/
    │   ├── BenchmarkInterfaces.cs          # IClientNexus, IServerNexus contracts
    │   ├── BenchmarkClientNexus.cs         # Client nexus implementation
    │   ├── BenchmarkServerNexus.cs         # Server nexus implementation
    │   └── Payloads.cs                     # Payload type definitions
    │
    ├── Reporting/
    │   ├── JsonReporter.cs                 # JSON output
    │   ├── MarkdownReporter.cs             # Markdown summary
    │   └── ResultModels.cs                 # Output data structures
    │
    ├── Infrastructure/
    │   ├── ScenarioRunner.cs               # Orchestrates execution
    │   ├── MetricsCollector.cs             # Timing, memory, GC stats
    │   ├── WarmupManager.cs                # Warmup iteration handling
    │   └── StatisticsCalculator.cs         # Mean, StdDev, percentiles
    │
    └── Results/                            # Output directory (gitignored)
        └── {commit-hash}/
            ├── results.json
            └── summary.md
```

---

## Implementation Phases

### Phase 1: Core Infrastructure (Foundation)

**Goal**: Establish project structure, CLI, and basic execution framework.

#### 1.1 Project Setup
- [ ] Create `NexNet.PerformanceBenchmarks.csproj`
  - Target: `net9.0`
  - Project references: `NexNet`, `NexNet.Generator`, `NexNet.Quic`
  - Package: `System.CommandLine` (CLI parsing)
- [ ] Add to solution (`NexNet.slnx`)
- [ ] Create `.gitignore` entries for `Results/` directory

#### 1.2 CLI Interface (`Program.cs`, `Config/CliOptions.cs`)
- [ ] Implement command-line parsing with `System.CommandLine`
  ```
  Options:
    --full              Run complete benchmark suite
    --quick             Run with fewer iterations (validation mode)
    --category <name>   Run specific category (Latency, Throughput, etc.)
    --scenario <name>   Run specific scenario
    --transport <list>  Comma-separated transports (Uds,Tcp,Tls,etc.)
    --output <path>     Output directory path
    --payload <sizes>   Comma-separated payload sizes (Tiny,Small,Medium,Large,XLarge)
  ```

#### 1.3 Configuration (`Config/BenchmarkSettings.cs`)
- [ ] Define execution configuration
  ```csharp
  public class BenchmarkSettings
  {
      public int WarmupIterations { get; set; } = 3;
      public int MeasuredIterations { get; set; } = 15;
      public TimeSpan IterationTimeout { get; set; } = TimeSpan.FromMinutes(2);
      public bool ForceGCBetweenIterations { get; set; } = true;
      public TimeSpan ThroughputDuration { get; set; } = TimeSpan.FromSeconds(10);
  }
  ```

#### 1.4 Transport Factory (`Config/TransportFactory.cs`)
- [ ] Create factory for all 6 transports
- [ ] Implement QUIC availability detection with graceful skip
- [ ] Generate unique ports/paths per scenario to avoid conflicts
  ```csharp
  public static class TransportFactory
  {
      public static (TServerConfig, TClientConfig) Create<TServerConfig, TClientConfig>(
          TransportType type, int portOffset = 0);
      public static bool IsTransportAvailable(TransportType type);
  }
  ```

#### 1.5 Scenario Framework (`Scenarios/`)
- [ ] Define `IScenario` interface
  ```csharp
  public interface IScenario
  {
      string Name { get; }
      string Category { get; }
      IReadOnlyList<PayloadSize> SupportedPayloads { get; }
      Task<ScenarioResult> RunAsync(ScenarioContext context);
  }
  ```
- [ ] Implement `ScenarioContext` with transport, payload, settings
- [ ] Implement `ScenarioResult` with metrics containers

#### 1.6 Statistics Calculator (`Infrastructure/StatisticsCalculator.cs`)
- [ ] Mean, StdDev, Min, Max calculations
- [ ] Percentile calculations (P50, P95, P99)
- [ ] Outlier detection (optional trimming)

#### 1.7 Metrics Collector (`Infrastructure/MetricsCollector.cs`)
- [ ] High-resolution timing with `Stopwatch`
- [ ] GC generation collection counts
- [ ] Memory allocation tracking via `GC.GetAllocatedBytesForCurrentThread()`

#### 1.8 Scenario Runner (`Infrastructure/ScenarioRunner.cs`)
- [ ] Orchestrate warmup and measured iterations
- [ ] Force GC between scenarios
- [ ] Handle timeouts gracefully
- [ ] Collect and aggregate results

**Deliverables Phase 1:**
- Runnable CLI application
- `--help` working
- Empty scenario execution (no actual benchmarks yet)
- Statistics and metrics infrastructure tested

---

### Phase 2: Nexus Definitions & Payloads

**Goal**: Create benchmark-specific nexus implementations and payload types.

#### 2.1 Payload Definitions (`Nexuses/Payloads.cs`)
- [ ] Define all payload sizes with MemoryPack serialization
  ```csharp
  // Tiny: 1 byte
  public readonly struct TinyPayload { public byte Value; }

  // Small: ~1 KB
  [MemoryPackable]
  public partial class SmallPayload
  {
      public int Id;
      public long Timestamp;
      public byte[] Data; // 1000 bytes
  }

  // Medium: ~64 KB
  [MemoryPackable]
  public partial class MediumPayload { /* ... */ }

  // Large: ~1 MB
  [MemoryPackable]
  public partial class LargePayload { /* ... */ }

  // XLarge: ~10 MB
  [MemoryPackable]
  public partial class XLargePayload { /* ... */ }
  ```
- [ ] Create deterministic payload generators with fixed seeds

#### 2.2 Benchmark Interfaces (`Nexuses/BenchmarkInterfaces.cs`)
- [ ] Define comprehensive interface contracts
  ```csharp
  public interface IBenchmarkServerNexus
  {
      // Latency benchmarks
      ValueTask<byte[]> Echo(byte[] data);
      ValueTask<TinyPayload> EchoTiny(TinyPayload data);
      ValueTask<SmallPayload> EchoSmall(SmallPayload data);
      ValueTask<MediumPayload> EchoMedium(MediumPayload data);
      ValueTask<LargePayload> EchoLarge(LargePayload data);
      ValueTask<XLargePayload> EchoXLarge(XLargePayload data);

      // Throughput benchmarks
      ValueTask StreamUpload(INexusDuplexPipe pipe);
      ValueTask StreamDownload(INexusDuplexPipe pipe);
      ValueTask ChannelUpload(INexusDuplexChannel<SmallPayload> channel);
      ValueTask ChannelDownload(INexusDuplexChannel<SmallPayload> channel);
      ValueTask UnmanagedChannelUpload(INexusDuplexUnmanagedChannel<long> channel);

      // Fire-and-forget throughput
      void FireAndForget(byte[] data);

      // Collections (INexusList<T>)
      INexusList<TestItem> SyncList { get; }
  }

  public interface IBenchmarkClientNexus
  {
      // For broadcast scenarios
      ValueTask ReceiveBroadcast(byte[] data);
      ValueTask ReceiveGroupMessage(string group, byte[] data);

      // Collections
      INexusList<TestItem> SyncList { get; }
  }
  ```

#### 2.3 Server Nexus (`Nexuses/BenchmarkServerNexus.cs`)
- [ ] Implement all server-side methods
- [ ] Implement broadcast helpers for scalability scenarios
- [ ] Configure INexusList with ServerToClient sync mode

#### 2.4 Client Nexus (`Nexuses/BenchmarkClientNexus.cs`)
- [ ] Implement client-side receive handlers
- [ ] Add metrics collection hooks (message counts, timestamps)
- [ ] Implement reconnection callbacks for reconnection scenario

**Deliverables Phase 2:**
- All payload types with MemoryPack serialization
- Complete nexus implementations
- Basic echo functionality testable

---

### Phase 3: Latency Benchmarks

**Goal**: Implement all latency measurement scenarios.

#### 3.1 Invocation Latency (`Scenarios/Latency/InvocationLatencyScenario.cs`)
- [ ] Round-trip time for `Echo()` methods
- [ ] Support all 5 payload sizes
- [ ] All 6 transports
- [ ] Metrics: P50, P95, P99, Mean, Min, Max (microseconds)

#### 3.2 Pipe Latency (`Scenarios/Latency/PipeLatencyScenario.cs`)
- [ ] Single message round-trip via duplex pipe
- [ ] Measure write → read → response cycle
- [ ] All payload sizes and transports

#### 3.3 Channel Latency (`Scenarios/Latency/ChannelLatencyScenario.cs`)
- [ ] Typed channel single-item round-trip
- [ ] Both managed (`INexusDuplexChannel<T>`) and unmanaged variants
- [ ] All payload sizes and transports

**Deliverables Phase 3:**
- 3 latency scenarios fully functional
- Results output to JSON
- ~5 minutes runtime for full latency suite

---

### Phase 4: Throughput Benchmarks

**Goal**: Implement sustained transfer rate measurements.

#### 4.1 Pipe Throughput (`Scenarios/Throughput/PipeThroughputScenario.cs`)
- [ ] Sustained duplex streaming for fixed duration (10 seconds)
- [ ] Measure MB/s and Messages/s
- [ ] Payload sizes: 1KB, 64KB, 1MB
- [ ] All transports

#### 4.2 Channel Throughput (`Scenarios/Throughput/ChannelThroughputScenario.cs`)
- [ ] Typed channel streaming rate
- [ ] Items/s and MB/s metrics
- [ ] Test concurrent streams: 1, 4, 8

#### 4.3 Invocation Throughput (`Scenarios/Throughput/InvocationThroughputScenario.cs`)
- [ ] Rapid-fire RPC calls (fire-and-forget)
- [ ] Measure invocations/s
- [ ] Test with and without responses

**Deliverables Phase 4:**
- 3 throughput scenarios
- ~8 minutes runtime for full throughput suite

---

### Phase 5: Scalability Benchmarks

**Goal**: Measure performance under concurrent client load.

#### 5.1 Multi-Client Load (`Scenarios/Scalability/MultiClientScenario.cs`)
- [ ] Connect N clients (10, 25, 50, 100)
- [ ] All clients invoke simultaneously
- [ ] Measure latency degradation and aggregate throughput
- [ ] Track per-client metrics

#### 5.2 Broadcast Scenario (`Scenarios/Scalability/BroadcastScenario.cs`)
- [ ] Server broadcasts to all connected clients
- [ ] Client counts: 10, 50, 100
- [ ] Measure:
  - Total delivery time (first to last client)
  - Messages/s broadcast rate
  - Per-client receive latency

#### 5.3 Group Messaging (`Scenarios/Scalability/GroupMessagingScenario.cs`)
- [ ] Dynamic group membership (add/remove)
- [ ] Targeted group broadcasts
- [ ] Measure:
  - Group operations/s
  - Group delivery time
  - Membership change latency

**Deliverables Phase 5:**
- 3 scalability scenarios
- ~6 minutes runtime

---

### Phase 6: Stress Benchmarks

**Goal**: Measure behavior under adverse conditions.

#### 6.1 Connection Churn (`Scenarios/Stress/ConnectionChurnScenario.cs`)
- [ ] 1000 rapid connect/disconnect cycles
- [ ] Measure:
  - Connections/s
  - Connect latency (P50, P95, P99)
  - Disconnect latency
  - Memory stability (no leaks)

#### 6.2 Memory Pressure (`Scenarios/Stress/MemoryPressureScenario.cs`)
- [ ] 5-minute sustained load
- [ ] Track:
  - Gen0/Gen1/Gen2 collections
  - Heap size over time
  - Allocation rate
- [ ] Detect memory leaks

#### 6.3 Backpressure (`Scenarios/Stress/BackpressureScenario.cs`)
- [ ] Fast producer (10x consumer speed)
- [ ] 100MB total transfer
- [ ] Measure:
  - Pause frequency (when writer blocks)
  - Recovery time
  - Final throughput with backpressure

**Deliverables Phase 6:**
- 3 stress scenarios
- ~5 minutes runtime

---

### Phase 7: Collection Synchronization

**Goal**: Benchmark INexusList<T> performance.

#### 7.1 Large List Sync (`Scenarios/Collections/LargeListSyncScenario.cs`)
- [ ] List sizes: 1,000 / 10,000 / 100,000 items
- [ ] Measure initial sync time
- [ ] Measure memory consumption
- [ ] Test with complex item types

#### 7.2 High-Frequency Updates (`Scenarios/Collections/HighFrequencyUpdateScenario.cs`)
- [ ] 100+ operations/second sustained
- [ ] All operation types: Add, Insert, Remove, RemoveAt, Replace, Move, Clear
- [ ] Measure:
  - Operations/s throughput
  - Update latency (server change → client notification)

**Deliverables Phase 7:**
- 2 collection scenarios
- ~4 minutes runtime

---

### Phase 8: Overhead Benchmarks

**Goal**: Measure cost of optional features.

#### 8.1 Cancellation Overhead (`Scenarios/Overhead/CancellationOverheadScenario.cs`)
- [ ] Compare invocations with/without CancellationToken
- [ ] Measure overhead in microseconds
- [ ] Test token propagation across network

#### 8.2 Reconnection Scenario (`Scenarios/Overhead/ReconnectionScenario.cs`)
- [ ] Configure `DefaultReconnectionPolicy`
- [ ] Force disconnect (server restart simulation)
- [ ] Measure:
  - Time to detect disconnect
  - Reconnection attempt latency
  - Total recovery time
  - State restoration time

**Deliverables Phase 8:**
- 2 overhead scenarios (Authentication excluded)
- ~2 minutes runtime

---

### Phase 9: Reporting

**Goal**: Generate human-readable and machine-parseable output.

#### 9.1 JSON Reporter (`Reporting/JsonReporter.cs`)
- [ ] Full results with metadata (commit hash, timestamp, system info)
- [ ] All scenarios with detailed metrics
- [ ] Memory metrics per scenario
- [ ] Schema documentation

#### 9.2 Markdown Reporter (`Reporting/MarkdownReporter.cs`)
- [ ] Summary tables by category
- [ ] Transport comparison matrix
- [ ] Payload size comparison
- [ ] Highlight best/worst performers

#### 9.3 Result Models (`Reporting/ResultModels.cs`)
- [ ] `BenchmarkRun` - Top-level container
- [ ] `ScenarioMetrics` - Per-scenario results
- [ ] `LatencyMetrics` - Percentiles, mean, stddev
- [ ] `ThroughputMetrics` - Rate measurements
- [ ] `MemoryMetrics` - GC and allocation data

**Deliverables Phase 9:**
- `results.json` with complete data
- `summary.md` human-readable report
- Commit hash in output for version tracking

---

## Testing Strategy

### Unit Tests (Optional but Recommended)
- Statistics calculator accuracy
- Payload generator determinism
- Transport factory configuration

### Integration Tests
- Single scenario end-to-end
- Each transport type validation
- Output file generation

### Validation Run
```bash
dotnet run -- --quick --scenario InvocationLatency --transport Uds
```
Should complete in <1 minute with valid output.

---

## Time Budget Summary

| Phase | Category | Estimated Runtime |
|-------|----------|-------------------|
| 3 | Latency (3 scenarios × 5 payloads × 6 transports) | ~5 min |
| 4 | Throughput (3 scenarios × 3 payloads × 6 transports) | ~8 min |
| 5 | Scalability (3 scenarios × 4 client counts × 6 transports) | ~6 min |
| 6 | Stress (3 scenarios × 6 transports) | ~5 min |
| 7 | Collections (2 scenarios × 3 sizes × 6 transports) | ~4 min |
| 8 | Overhead (2 scenarios × 6 transports) | ~2 min |
| **Total** | | **~30 min** |

---

## Dependencies

```xml
<ItemGroup>
  <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
  <PackageReference Include="MemoryPack" Version="1.21.3" />
</ItemGroup>

<ItemGroup>
  <ProjectReference Include="..\NexNet\NexNet.csproj" />
  <ProjectReference Include="..\NexNet.Generator\NexNet.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <ProjectReference Include="..\NexNet.Quic\NexNet.Quic.csproj" />
</ItemGroup>
```

---

## Implementation Order Recommendation

1. **Phase 1** (Core Infrastructure) - Must be first
2. **Phase 2** (Nexuses & Payloads) - Required for all scenarios
3. **Phase 3** (Latency) - Simplest scenarios, validates infrastructure
4. **Phase 9** (Reporting) - Implement early to see results
5. **Phase 4** (Throughput) - Builds on latency patterns
6. **Phase 5** (Scalability) - More complex client management
7. **Phase 7** (Collections) - Independent feature
8. **Phase 6** (Stress) - Requires stable infrastructure
9. **Phase 8** (Overhead) - Final scenarios

---

## CLI Usage Examples

```bash
# Full benchmark suite (~30 minutes)
dotnet run --project src/NexNet.PerformanceBenchmarks -- --full

# Quick validation run
dotnet run --project src/NexNet.PerformanceBenchmarks -- --quick

# Specific category
dotnet run --project src/NexNet.PerformanceBenchmarks -- --category Latency

# Specific scenario and transport
dotnet run --project src/NexNet.PerformanceBenchmarks -- --scenario InvocationLatency --transport Uds,Tcp

# Custom output location
dotnet run --project src/NexNet.PerformanceBenchmarks -- --full --output ./benchmark-results

# Help
dotnet run --project src/NexNet.PerformanceBenchmarks -- --help
```

---

## Success Criteria

- [ ] All 17 scenarios implemented and passing
- [ ] All 6 transports supported (QUIC with graceful skip)
- [ ] JSON and Markdown output generated
- [ ] ~30 minute total runtime for full suite
- [ ] Deterministic results (fixed random seeds)
- [ ] No memory leaks in stress scenarios
- [ ] Clear error messages for failures
