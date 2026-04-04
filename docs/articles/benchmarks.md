# Benchmarks

NexNet is benchmarked using [BenchmarkDotNet](https://benchmarkdotnet.org/) to measure invocation overhead, throughput, and memory allocation.

## Environment

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7462/24H2/2024Update/HudsonValley)
Intel Core i7-10700 CPU 2.90GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 9.0.305
  [Host]     : .NET 9.0.9 (9.0.9, 9.0.925.41916), X64 RyuJIT x86-64-v3
  Job-FJSVHN : .NET 9.0.9 (9.0.9, 9.0.925.41916), X64 RyuJIT x86-64-v3

Platform=X64  Runtime=.NET 9.0
```

## Results

| Method | Mean | Error | StdDev | Op/s | Allocated |
|--------|-----:|------:|-------:|-----:|----------:|
| InvocationNoArgument | 36.7 us | 0.33 us | 0.31 us | 27,241.7 | 595 B |
| InvocationUnmanagedArgument | 37.4 us | 0.52 us | 0.48 us | 26,769.8 | 649 B |
| InvocationUnmanagedMultipleArguments | 37.3 us | 0.28 us | 0.25 us | 26,800.8 | 700 B |
| InvocationNoArgumentWithResult | 36.9 us | 0.35 us | 0.32 us | 27,095.2 | 633 B |
| InvocationWithDuplexPipe_Upload | 51.8 us | 0.60 us | 0.51 us | 19,295.4 | 14,951 B |

## Key Observations

- Basic invocations achieve approximately 27,000 operations per second
- Adding arguments increases allocation modestly (595 B → 700 B) with negligible latency impact
- Return values add minimal overhead compared to void invocations
- Duplex pipe operations are slower due to the additional pipe setup and streaming infrastructure, but still achieve ~19,000 ops/sec

## Running Benchmarks

The benchmark project is located at `src/NexNetBenchmarks/`. Run with:

```bash
dotnet run -c Release --project src/NexNetBenchmarks/NexNetBenchmarks.csproj
```

## See Also

- [Hub Invocations](hub-invocations.md) — Method patterns that affect performance characteristics
- [Duplex Pipes](duplex-pipes.md) — Streaming performance context
