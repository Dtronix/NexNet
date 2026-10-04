# Benchmark results: NexNet MessagePack vs MemoryPack (Phase 7)

Branch `worktree-messagepack-eval`, after the optimization pass (commit `56ae92c` plus this document). Both backends
were built from the same tree with `-p:NexNetSerializer=MessagePack|MemoryPack`. A third column, **master**, is the
release baseline (57fef36) built from a separate detached worktree.

**Verdict: gate items 1–3 pass. Item 4 passes, except that fragmented IntArrays256 is 2% behind MemoryPack by minimum
(1% ahead by median), which is inside the noise. Item 5 (24-hour fuzzing) is in progress.** It started on
2026-10-04 at 15:30. Every performance criterion is met or within noise. Details are in
[Gate evaluation](#gate-evaluation). The previous (failed) evaluation is kept under
[Before optimization](#before-optimization).

## Method

- **Full BenchmarkDotNet jobs** (the default job: pilot, auto warmup, 15–100 iterations), run **in-process**
  (`NEXNET_BENCH_INPROC=1`) so that the backend built into `bin` is the one measured. An out-of-process job would
  rebuild with the default backend. The in-process toolchain timeout was raised to 30 minutes, because the default
  5 minutes killed the slower channel cases.
- **Two runs per backend, alternating,** with a rebuild before every backend switch:
  MessagePack 1 → MemoryPack 1 → master 1 → serializer 1 → MemoryPack 2 → MessagePack 2 → master 2 → serializer 2
  (00:31–03:43, 2026-10-04).
- **min** is the minimum of the two run means. **med** is the median of the two runs, which with two runs is their
  mean. A ratio is MessagePack / reference, so a value below 1.00 means MessagePack is faster.
- **The machine was not idle.** Other projects' test runs and MSBuild nodes were active, and the load drifted between
  runs. For example, MemoryPack's run-1 invocations came out 25–45% slower than its run 2. Back-to-back pairs and
  the minimum are the most reliable views; the median is pulled by whichever run was loaded.
- Environment: AMD Ryzen 9 3900X (12 cores / 24 threads), Windows 11 25H2, .NET 10.0.12, BenchmarkDotNet 0.15.8,
  High performance power plan.
- The serializer benchmarks run NexNet and the MemoryPack library in the same process, so their ratios are reliable.
  They need the MessagePack build, so they were run on that build only.
- The master worktree has the branch's `InvocationBenchmarks` (without the `Security` parameter, which master
  lacks) and `ChannelThroughputBenchmarks`, ported with MemoryPack-only versions of the same benchmark types.
  `SerializerBenchmarks` needs NexNet formatters and was not ported.

## Optimizations

Measured with an in-process stopwatch harness (minimum of 25 rounds) before the full runs. That harness had about
±15% drift between runs on this machine, so treat single numbers as approximate. "Before" is the code at the start
of this pass. The wire format is unchanged: golden vectors and wire sizes are identical.

| # | Change | Measured effect | Files |
|---|---|---|---|
| 1 | `MsgPackReader` cursor rewritten as current-segment span + index with out-of-line segment slow paths (no `SequenceReader`) | Int32 deserialize 30 → 19 ns; Poco 95 → 63 ns; Union 45 → 29 ns; channel item (Persons) 140 → 95 ns; `TrySkip` per Person 35 → 22 ns | `NexNet.Serialization/MsgPackReader.cs` |
| 2 | `ReadOnlyMemory` constructor via `MemoryMarshal.TryGetArray` (no out-of-line `ReadOnlySequence` ctor) | Int32 19 → 14 ns | `MsgPackReader.cs` |
| 3 | UTF-8 decode: `Ascii.IsValid` + widen into `string.Create`; non-ASCII in one `Utf8.ToUtf16` pass (stack ≤ 256 B, else pooled) | Unicode 2600 → 1650 ns (MemoryPack ~1600); AsciiShort 34 → 25 ns (faster than MemoryPack); AsciiLong 350 → 230 ns | `MsgPackReader.cs` |
| 4 | Generated formatters read members in a straight line when `count` == expected length (loop kept for older/newer peers) | Poco 66 → 51 ns; NestedGraph 300 → 250 ns; PersonList100 5700 → 3650 ns | `NexNet.Generator/Serialization/SerializationBuilder.cs` |
| 5 | `ReadDouble`/`ReadSingle` inline float64/float32 fast path | Included in #4 (Person has a double) | `MsgPackReader.cs` |
| 6 | `DateTime` Utc/Unspecified decoded directly (no `try`/`catch` around `FromBinary`) | Poco 51 → 41 ns; PersonList100 3650 → ~3300 ns | `NexNet.Serialization/Formatters/PrimitiveFormatters.cs` |
| 7 | ext 78: header + kind + data in one `GetSpan` for ≤ 4 KB; kind byte read directly | Doubles16 S+D 1.47–1.57 → 1.12 (micro), 1.04 (full job) | `Ext/PrimitiveArrayCodec.cs`, `MsgPackWriter.cs` |
| 8 | `TrySkip` over the current span with locals (`SkipInSpan`), no reader copies; non-mutating `TryGetNextValueLength` for the channel probe | Channel reader CPU for Ints 58 → 28 µs per 2000 items; Persons 218 → 189 µs | `MsgPackReader.cs`, `NexNet/Pipes/NexusChannelReader.cs` |
| 9 | `ReadInt32` slow path decodes uint8/16 and int8/16/32 from the span | `ReadInt32` (uint16 form) 6 → 4 ns | `MsgPackReader.cs` |
| 10 | Channel reads optimistic away from the buffer end (probe only the tail; restore and probe on failure) | Channel reader CPU for Ints 28 → 17 µs; Persons 189 → ~175 µs | `NexusChannelReader.cs` |

Tried and reverted: a 256-entry size table for `TrySkip` (28 ns vs 21–23 ns for the switch per Person).

**The Persons "slower non-fragmented" anomaly** from the ShortRun results was a warmup artifact. `Fragmented=False`
always runs first in the process, and three ShortRun warmups were not enough for tiering. With 1500 warmup ops in a
harness, or a full job, it disappears: Persons is 321–382 µs non-fragmented and 329–339 µs fragmented (MessagePack,
full job). There was no quadratic probing. The reader handled about 3 buffers of 18–28 KB per op, and every item was
read once by the probe and once by the formatter. Change #10 removes that double pass for all but the tail items.

**Why the earlier span fast path measured slower** (`impl-notes.md`): it wasn't kept, so this can't be confirmed. It
most likely added a span alongside `SequenceReader` rather than replacing it, which made the struct bigger and added
a branch to every read. Change #1 replaces the cursor entirely. A related finding: copying the reader struct through
`this` (as the old `TrySkip` did, three copies per call) goes through GC write-barrier helpers, because the struct
holds several object references. Change #8 removes those copies.

## Wire sizes

Bytes per value, from `NexNetBenchmarks --sizes`. These are unchanged by the optimization pass.

| Payload | NexNet compact | NexNet fixed-width | MemoryPack | Compact vs MemoryPack |
|---|---:|---:|---:|---:|
| Int32 | 5 | 5 | 4 | +25% |
| AsciiShort | 12 | 12 | 19 | −37% |
| AsciiLong | 2003 | 2003 | 2008 | −0.2% |
| Unicode | 2503 | 2503 | 2508 | −0.2% |
| Poco | 31 | 35 | 39 | −21% |
| NestedGraph | 97 | 113 | 160 | −39% |
| Doubles16 | 132 | 132 | 132 | 0% |
| Doubles1K | 8197 | 8197 | 8196 | 0% |
| Doubles64K | 524295 | 524295 | 524292 | 0% |
| PersonList100 | 3093 | 3493 | 3894 | −21% |
| Union | 12 | 14 | 10 | +20% |

## Serializer (`SerializerBenchmarks`, full job, 2 runs)

Mean ns, min / med over the two runs, compact mode. "S+D" is (NexNet serialize + deserialize) / (MemoryPack
serialize + deserialize).

| Payload | NexNet serialize | MemoryPack serialize | NexNet deserialize | MemoryPack deserialize | Deserialize ratio (min) | S+D ratio min / med | Fixed-width serialize |
|---|---|---|---|---|---:|---:|---|
| Int32 | 9.8 / 9.8 | 6.5 / 6.6 | 14.0 / 14.0 | 2.1 / 2.2 | 6.62 | 2.75 / 2.72 | 12.8 / 13.2 |
| AsciiShort | 28.2 / 31.5 | 36.3 / 36.3 | 25.5 / 26.1 | 29.8 / 30.2 | 0.86 | 0.81 / 0.87 | 29.4 / 32.5 |
| AsciiLong | 64.9 / 65.1 | 78.1 / 79.9 | 246.7 / 248.8 | 231.5 / 232.4 | 1.07 | 1.01 / 1.00 | 67.3 / 67.6 |
| Unicode | 1498 / 1499 | 1498 / 1499 | 1673 / 1703 | 1633 / 1635 | 1.02 | 1.01 / 1.02 | 1493 / 1501 |
| **Poco** | 32.7 / 32.7 | 43.6 / 43.7 | 43.3 / 47.4 | 45.6 / 46.1 | 0.95 | **0.85 / 0.89** | 33.9 / 34.7 |
| NestedGraph | 173.4 / 174.6 | 172.0 / 172.4 | 251.1 / 254.3 | 220.7 / 225.8 | 1.14 | 1.08 / 1.08 | 182.2 / 183.0 |
| **Doubles16** | 18.7 / 19.0 | 19.4 / 20.1 | 30.8 / 31.1 | 28.2 / 28.8 | 1.09 | **1.04 / 1.03** | 19.9 / 20.2 |
| **Doubles1K** | 97.3 / 98.4 | 86.6 / 87.1 | 302.9 / 303.7 | 303.6 / 304.2 | 1.00 | **1.03 / 1.03** | 94.1 / 98.6 |
| **Doubles64K** | 10361 / 10373 | 10286 / 10314 | 58676 / 59093 | 59512 / 60243 | 0.99 | **0.99 / 0.98** | 10292 / 10304 |
| PersonList100 | 2454 / 2468 | 1870 / 1960 | 3480 / 3481 | 2572 / 2594 | 1.35 | 1.34 / 1.31 | 2438 / 2456 |
| Union | 16.9 / 17.2 | 44.2 / 47.9 | 20.4 / 21.5 | 22.3 / 22.6 | 0.92 | 0.56 / 0.55 | 19.7 / 19.9 |

- **Gate items 2 and 3 now pass with margin.** POCO S+D is 0.85–0.89 (it was 1.25–1.30), and Doubles16 is 1.03–1.04
  (it was 1.47–1.57).
- Strings, the POCO, the union and the primitive arrays are at parity or faster. NestedGraph is +8%.
- **PersonList100 is the remaining outlier** (S+D 1.31–1.34). Serialization is 1.25–1.31× MemoryPack. The likely
  cause is the per-element virtual call through `ListFormatter<T>` and the writer's per-member calls, but it was
  not profiled.
- **Int32 is still 2.7×.** MemoryPack reads an unmanaged `int` as a fixed 4-byte copy with almost no setup (about
  2 ns). NexNet builds a reader and decodes a variable-length integer (about 14 ns, down from 28–30). This is fixed
  per-call cost and doesn't show up in invocations or channels.
- Allocations are identical to MemoryPack in every deserialize case, as before.

## Invocations (`InvocationBenchmarks`, full job, 2 runs per backend)

Mean µs per round trip over UDS. Allocations are per run (run 1 / run 2).

| Benchmark | Security | MessagePack runs | MemoryPack runs | master runs | Ratio vs MemoryPack min / med | Ratio vs master min / med | Alloc MessagePack | Alloc MemoryPack | Alloc master |
|---|---|---|---|---|---:|---:|---|---|---|
| NoArgument | Untrusted | 60.2 / 66.9 | 86.5 / 66.3 | 74.1 / 67.8 | 0.91 / 0.83 | 0.89 / 0.90 | 624–625 B | 625 B | 609 B |
| UnmanagedArgument | Untrusted | 64.5 / 72.0 | 83.0 / 67.3 | 77.8 / 71.7 | 0.96 / 0.91 | 0.90 / 0.91 | 625 B | 657 B | 641 B |
| UnmanagedMultipleArguments | Untrusted | 55.2 / 69.4 | 81.9 / 67.9 | 74.2 / 69.2 | 0.81 / 0.83 | 0.80 / 0.87 | 624–625 B | 681 B | 665 B |
| PocoArgument | Untrusted | 65.4 / 71.2 | 106.1 / 70.5 | 76.8 / 70.3 | 0.93 / 0.77 | 0.93 / 0.93 | 713 B | 777 B | 761 B |
| NestedRoundTrip | Untrusted | 67.2 / 81.7 | 114.0 / 82.5 | 85.0 / 83.3 | 0.81 / 0.76 | 0.81 / 0.89 | 2193 B | 2265 B | 2433 B |
| NoArgumentWithResult | Untrusted | 59.8 / 72.6 | 99.7 / 68.6 | 75.6 / 69.3 | 0.87 / 0.79 | 0.86 / 0.91 | 633 B | 633 B | 649 B |
| WithDuplexPipe_Upload | Untrusted | 88.9 / 93.7 | 115.8 / 91.4 | 100.5 / 94.7 | 0.97 / 0.88 | 0.94 / 0.94 | 16503 / 17899 B | 16872 / 16683 B | 17050 / 16977 B |
| NoArgument | Trusted | 65.8 / 70.5 | 95.0 / 66.2 | (as above) | 0.99 / 0.85 | 0.97 / 0.96 | 625 B | 625 B | 609 B |
| UnmanagedArgument | Trusted | 69.7 / 71.1 | 96.8 / 69.4 | | 1.00 / 0.85 | 0.97 / 0.94 | 2674 / 625 B | 657 B | 641 B |
| UnmanagedMultipleArguments | Trusted | 74.9 / 70.1 | 84.7 / 68.1 | | 1.03 / 0.95 | 1.01 / 1.01 | 625 B | 681 B | 665 B |
| PocoArgument | Trusted | 72.0 / 70.5 | 101.2 / 70.8 | | 1.00 / 0.83 | 1.00 / 0.97 | 713 B | 777 B | 761 B |
| NestedRoundTrip | Trusted | 80.3 / 81.8 | 113.8 / 80.6 | | 1.00 / 0.83 | 0.96 / 0.96 | 2193 B | 2265 B | 2433 B |
| NoArgumentWithResult | Trusted | 72.0 / 71.5 | 92.2 / 69.2 | | 1.03 / 0.89 | 1.03 / 0.99 | 633–634 B | 633 B | 649 B |
| WithDuplexPipe_Upload | Trusted | 101.7 / 93.6 | 113.9 / 91.1 | | 1.03 / 0.95 | 0.99 / 1.00 | 16401 / 16561 B | 17883 / 16563 B | 17050 / 16977 B |

master has no `Security` parameter, so its single set of runs is compared against both rows.

- **Time:** by minimum, MessagePack is 0.81–1.03 of branch-MemoryPack and 0.80–1.03 of master. The largest
  disadvantage is +3%, well inside the 5% gate. MemoryPack's run 1 was loaded (66–116 µs), so the medians favour
  MessagePack by more than the real difference.
  - **The back-to-back run-2 pair is the cleanest comparison:** MessagePack is 0.99–1.07 of MemoryPack (Untrusted
    1.01, 1.07, 1.02, 1.01, 0.99, 1.06, 1.03; Trusted 1.06, 1.02, 1.03, 1.00, 1.01, 1.03, 1.03). Taken alone, three
    of the 14 rows exceed 5% by 1–2 points. That gap is smaller than the run-to-run spread of either backend (up to
    20 µs), so it can't be resolved on this machine.
- **Allocations** are equal or lower for every argument-carrying call: 625 vs 657–681 B, Poco 713 vs 777 B, nested
  2193 vs 2265 B (master 2433 B).
  - The two exceptions are run-level noise, not systematic. One MessagePack sample allocated 2674 B
    (`UnmanagedArgument` Trusted, run 1; run 2 was 625 B). `WithDuplexPipe_Upload` varies by up to 1.4 KB between
    runs on every backend (pipe buffer growth).
- **Untrusted vs Trusted:** there is no consistent cost. The Untrusted rows are not slower than the Trusted ones on
  either backend (the depth, UTF-8 and length checks are a few ns against a 60–100 µs round trip).

## Channels (`ChannelThroughputBenchmarks`, full job, 2 runs per backend)

Mean µs per 2000 items over UDS. "Fragmented" forces 1 KB pipe flush chunks instead of 64 KB.

| Benchmark | Fragmented | MessagePack runs | MemoryPack runs | master runs | Ratio vs MemoryPack min / med | Ratio vs master min / med | Alloc MessagePack / MemoryPack / master (run 2) |
|---|---|---|---|---|---:|---:|---|
| Persons | False | 381.7 / 321.2 | 333.5 / 312.3 | 346.0 / 318.3 | 1.03 / 1.09 | 1.01 / 1.06 | 234.0 / 222.7 / 222.7 KB |
| IntArrays256 | False | 240.3 / 200.7 | 204.3 / 203.0 | 233.4 / 200.8 | 0.99 / 1.08 | 1.00 / 1.02 | 129.0 / 129.0 / 129.5 KB |
| Ints | False | 171.1 / 165.9 | 179.5 / 174.3 | 195.5 / 183.7 | 0.95 / 0.95 | 0.90 / 0.89 | 37.1 / 33.4 / 26.9 KB |
| Persons | True | 339.2 / 329.2 | 365.0 / 337.4 | 362.3 / 336.0 | **0.98 / 0.95** | 0.98 / 0.96 | 222.1 / 223.7 / 224.0 KB |
| IntArrays256 | True | 262.6 / 232.1 | 274.5 / 227.2 | 269.2 / 235.2 | **1.02 / 0.99** | 0.99 / 0.98 | 120.2 / 121.5 / 121.5 KB |
| Ints | True | 191.1 / 166.9 | 200.0 / 175.8 | 196.9 / 185.5 | **0.95 / 0.95** | 0.90 / 0.94 | 37.2 / 33.8 / 26.9 KB |

- **Every case is within 15%** of branch-MemoryPack: +3% at most by minimum and +9% by median (non-fragmented
  Persons, whose run 1 was the outlier at 382 µs).
- **Fragmented is better than MemoryPack** for Persons (0.98 / 0.95) and Ints (0.95 / 0.95). IntArrays256 fragmented
  is 1.02 by minimum and 0.99 by median, inside the noise.
- Compared with master, MessagePack is 0.89–1.06.
- **Allocations:**
  - Ints allocates about 3.7 KB more per op than MemoryPack and 10 KB more than master.
  - Persons non-fragmented allocated 234 KB in run 2 against 228 KB in run 1. MemoryPack and master stayed at 223 KB.
  - Neither difference was investigated. They are not in the gate, which only covers invocation allocations.
- **Earlier comparisons were misleading.** The channel benchmarks are very sensitive to machine load: MemoryPack Ints
  measured 154, 161, 175–180 and 198 µs at different times in this session. The early "Ints +43%" result came from
  pairing runs taken hours apart. A back-to-back medium-job pair taken during the investigation showed MessagePack
  at 0.87–1.02 of MemoryPack.

## Gate evaluation

The gate is from `impl-plan.md` §11.3. The change proceeds to Phase 9 only if all five items hold.

| # | Criterion | Measured (min / med) | Result |
|---|---|---|---|
| 1 | Invocation round trip within **5%** of branch-MemoryPack, allocations no higher | Time 0.81–1.03 / 0.76–0.95; vs master 0.80–1.03. Back-to-back run-2 pair 0.99–1.07. Allocations equal or lower for all argument calls; two run-level outliers (see above). | **Pass.** By minimum and by median every row is within 5%. The run-2 pair alone has 3 of 14 rows at +6–7%, below the run-to-run noise. |
| 2 | POCO serialize + deserialize within **25%** of MemoryPack | 0.85 / 0.89 (76.0 vs 89.2 ns min). NestedGraph 1.08; PersonList100 1.34 / 1.31. | **Pass.** |
| 3 | Primitive arrays (ext 78) within **15%** | Doubles16 1.04 / 1.03, Doubles1K 1.03 / 1.03, Doubles64K 0.99 / 0.98. Channel IntArrays256 0.99 / 1.08 non-fragmented, 1.02 / 0.99 fragmented. | **Pass.** |
| 4 | Channel throughput within **15%**, and fragmented **better** than branch-MemoryPack | Within 15%: all cases (max 1.03 / 1.09). Fragmented: Persons 0.98 / 0.95, Ints 0.95 / 0.95, IntArrays256 1.02 / 0.99. | **Pass, with one marginal case.** IntArrays256 fragmented is 2% behind by minimum and 1% ahead by median; that is noise. |
| 5 | Fuzzing (§10.4) clean for 24 hours | All six libFuzzer harnesses, including the new session receive-loop harness, started 2026-10-04 15:30. Building the session harness found and fixed five session bugs that are also on master (impl-notes deviation 22). | **In progress.** |

**Overall:** the performance criteria (1–4) are met, with item 4 marginal in one case that is within noise. The gate
is not formally passed until item 5 (the 24-hour fuzz run, started 2026-10-04 15:30) completes clean. That, and
confirming items 1 and 4 on a quieter machine if the margins matter, are the remaining steps before a go decision. Phase 8 has not been started.

## Caveats

- The machine was under background load throughout. Full jobs reduce, but don't remove, run-to-run drift; see the
  MemoryPack run-1 invocations.
- Two runs per backend. "med" with two runs is their mean.
- `CollectionBenchmarks` is not implemented.
- Bytes on the wire per invocation and per channel item (with framing) were not recorded, only per-value sizes.
- The serializer benchmarks were run on the MessagePack build only. Their MemoryPack column is the MemoryPack
  library in-process, not the branch MemoryPack backend.

## Raw output

In the session scratchpad (`C:\Users\djgos\AppData\Local\Temp\claude\Z--Projects-NexNet-NexNet-master\8fe9e157-a87b-4239-b9fd-954a98ad8323\scratchpad\full\`):
`msgpack-run1/2.txt`, `mempack-run1/2.txt`, `master-run1/2.txt`, `ser-run1/2.txt`, `sizes.txt`, `steps.log`
(timestamps), `agg.md` (aggregated tables). The master worktree is `scratchpad\master-baseline` (detached at
57fef36, with the ported benchmark files uncommitted).

## Before optimization

The original Phase 7 write-up, kept for comparison. It used in-process ShortRun jobs on the code before the optimization pass (reader on `SequenceReader`), with no master baseline.

Branch `worktree-messagepack-eval`, final code (MsgPackReader on `SequenceReader`; the span fast path is reverted).
Both backends were built from the same tree with `-p:NexNetSerializer=MessagePack|MemoryPack`. All runs are
in-process BenchmarkDotNet `ShortRun` (3 warmups, 3 iterations, 1 launch) with `NEXNET_BENCH_SHORT=1`, on .NET 10.0.12,
Windows 11 x64 (AVX2), on 2026-10-03.

**Verdict: the §11.3 gate fails.** Serialization speed and wire size are good. Deserialization, channel throughput
and the argument-carrying invocations miss their thresholds. Details are in [Gate evaluation](#gate-evaluation-before-optimization).

### Read this first: noise

The machine was not idle. Another project's test runs and MSBuild nodes were active during every run, and CPU load
measured 50–75% before the final pass. ShortRun error bars were often ±50–180% of the mean. To compensate:

- Invocation and channel benchmarks were run **2× for MessagePack and 3× for MemoryPack**, interleaved by backend
  (MemoryPack run 1 → MessagePack run 1 → MemoryPack run 2 → MessagePack run 2 → MemoryPack run 3), with a rebuild
  before each switch.
- The tables report every run plus the **minimum** across runs. Minimum is the best estimate of the uncontended cost
  under background load; the median is given too. A ratio is MessagePack / MemoryPack, so >1.00 means MessagePack is
  slower.
- The serializer benchmarks run both serializers in the same process and iteration, so their ratios are much more
  reliable than the absolute numbers. Two runs of the final code are shown (A at 18:12, B at 18:40).

Treat differences under about 10% as noise, except in the serializer table.

### Wire sizes

Payload bytes per value, from `NexNetBenchmarks --sizes` (final code, unchanged from the earlier run).

| Payload | NexNet compact | NexNet fixed-width | MemoryPack | Compact vs MemoryPack |
|---|---:|---:|---:|---:|
| Int32 | 5 | 5 | 4 | +25% |
| AsciiShort | 12 | 12 | 19 | −37% |
| AsciiLong | 2003 | 2003 | 2008 | −0.2% |
| Unicode | 2503 | 2503 | 2508 | −0.2% |
| Poco | 31 | 35 | 39 | −21% |
| NestedGraph | 97 | 113 | 160 | −39% |
| Doubles16 | 132 | 132 | 132 | 0% |
| Doubles1K | 8197 | 8197 | 8196 | 0% |
| Doubles64K | 524295 | 524295 | 524292 | 0% |
| PersonList100 | 3093 | 3493 | 3894 | −21% |
| Union | 12 | 14 | 10 | +20% |

Object graphs are 20–40% smaller on the wire. Primitive arrays (ext 78) are the same size. A single small integer
and a union cost 1–2 bytes more. Bytes on the wire per invocation (including framing) were not recorded separately.

### Serializer (`SerializerBenchmarks`)

Mean ns. Serialize / deserialize, compact mode. "S+D ratio" is (NexNet serialize + deserialize) / (MemoryPack
serialize + deserialize).

| Payload | NexNet A | MemoryPack A | S+D ratio A | NexNet B | MemoryPack B | S+D ratio B |
|---|---|---|---:|---|---|---:|
| Int32 | 9.3 / 27.6 | 6.4 / 2.0 | 4.38 | 9.7 / 29.9 | 6.8 / 2.1 | 4.47 |
| AsciiShort | 27.6 / 41.0 | 34.5 / 30.5 | 1.06 | 29.0 / 42.9 | 36.9 / 30.6 | 1.07 |
| AsciiLong | 62.5 / 269 | 78.7 / 227 | 1.08 | 65.2 / 274 | 78.8 / 243 | 1.05 |
| Unicode | 1292 / 2597 | 1291 / 1701 | 1.30 | 1373 / 2498 | 1383 / 1650 | 1.28 |
| **Poco** | 35.1 / 78.2 | 41.5 / 45.8 | **1.30** | 32.9 / 80.5 | 43.4 / 47.2 | **1.25** |
| NestedGraph | 166 / 382 | 150 / 215 | 1.50 | 189 / 386 | 180 / 239 | 1.37 |
| **Doubles16** | 19.0 / 47.2 | 17.4 / 27.5 | **1.47** | 20.8 / 51.8 | 18.6 / 27.6 | **1.57** |
| **Doubles1K** | 87.5 / 315 | 83.0 / 291 | **1.07** | 87.1 / 324 | 86.9 / 301 | **1.06** |
| **Doubles64K** | 9993 / 45380 | 10031 / 46599 | **0.98** | 10595 / 52453 | 10591 / 50785 | **1.03** |
| PersonList100 | 2884 / 6036 | 1777 / 2529 | 2.07 | 2543 / 5675 | 1887 / 2557 | 1.85 |
| Union | 16.1 / 42.9 | 39.5 / 21.9 | 0.96 | 16.8 / 43.8 | 57.0 / 22.4 | 0.76 |

Fixed-width serialization ranges from 8% faster to 36% slower than compact. Int32 is the worst case
(12.4–13.2 vs 9.3–9.7 ns). PersonList100 is 2–20% faster in fixed-width. Allocations are identical between the two
serializers in every deserialize case. NexNet serialization allocates 56 B on NestedGraph; MemoryPack allocates
nothing.

**Reading:** serialization is faster for strings, the POCO and the union. It is within about 10% for the nested
graph and the arrays, and slower for Int32 (+3 ns) and PersonList100 (1.35–1.6×). **Deserialization is the
problem.** It is 1.5–2.4× slower for objects and strings and carries a fixed overhead of about 25 ns per call,
visible in Int32 and Doubles16. Large primitive arrays are at parity in both directions.

### Invocations (`InvocationBenchmarks`)

Mean µs per round trip over UDS. MessagePack has 2 runs and MemoryPack has 3; "min" is the minimum across runs.

| Benchmark | Security | MessagePack runs | MemoryPack runs | Min ratio | Median ratio | Alloc MsgPack / MemPack |
|---|---|---|---|---:|---:|---|
| NoArgument | Untrusted | 54.3 / 53.6 | 55.0 / 56.1 / 54.8 | 0.98 | 0.98 | 624 B / 624 B |
| UnmanagedArgument | Untrusted | 68.6 / 57.5 | 49.4 / 54.5 / 57.0 | 1.16 | 1.16 | 624 B / 656 B |
| UnmanagedMultipleArguments | Untrusted | 70.7 / 60.4 | 55.0 / 52.4 / 57.3 | 1.15 | 1.19 | 624 B / 680 B |
| PocoArgument | Untrusted | 64.4 / 55.7 | 66.8 / 53.5 / 55.8 | 1.04 | 1.08 | 712 B / 776 B |
| NestedRoundTrip | Untrusted | 77.5 / 66.1 | 57.5 / 63.3 / 63.9 | 1.15 | 1.13 | 2193 B / 2265 B |
| NoArgumentWithResult | Untrusted | 58.5 / 56.7 | 53.2 / 50.6 / 71.0 | 1.12 | 1.08 | 632 B / 633 B |
| WithDuplexPipe_Upload | Untrusted | 81.3 / 78.8 | 76.0 / 73.1 / 93.3 | 1.08 | 1.05 | 16.4 KB / 16.7–17.2 KB |
| NoArgument | Trusted | 55.1 / 57.0 | 53.2 / 49.8 / 67.3 | 1.11 | 1.05 | 624 B / 624 B |
| UnmanagedArgument | Trusted | 59.4 / 54.2 | 49.3 / 52.1 / 71.8 | 1.10 | 1.09 | 624 B / 656 B |
| UnmanagedMultipleArguments | Trusted | 56.9 / 54.8 | 93.3 / 52.2 / 71.7 | 1.05 | 0.78 | 624 B / 680 B |
| PocoArgument | Trusted | 57.7 / 56.1 | 106.3 / 63.3 / 75.4 | 0.89 | 0.75 | 712 B / 776 B |
| NestedRoundTrip | Trusted | 68.7 / 66.4 | 100.1 / 66.2 / 84.7 | 1.00 | 0.80 | 2193 B / 2265 B |
| NoArgumentWithResult | Trusted | 57.3 / 57.5 | 101.4 / 55.6 / 72.8 | 1.03 | 0.79 | 632 B / 633 B |
| WithDuplexPipe_Upload | Trusted | 73.7 / 74.3 | 118.0 / 75.9 / 94.5 | 0.97 | 0.78 | 16.1 KB / 16.0–17.0 KB |

- **Allocations** are equal or lower with MessagePack in every case: 624 B vs 656–680 B for argument calls, 712 vs
  776 B for a POCO argument, and 2193 vs 2265 B for the nested round trip. This comes from the pooled
  argument buffer (`InvocationMessage.ArgumentsOwner`).
- **Time:** no-argument calls are at parity. The Untrusted argument-carrying calls come out 4–16% slower by min, and
  the ordering held in both MessagePack runs. The Trusted rows are dominated by MemoryPack outliers (run 1 was hit
  by background load), so their medians favour MessagePack, and that should not be read as a real win.
- Earlier, on a quieter machine, a MessagePack build with the reader span fast path (since reverted) measured
  48.2 / 50.0 / 50.8 / 50.9 / 64.0 / 52.1 µs (Untrusted, same order as the first six rows). That is within noise of
  MemoryPack. The fast path was reverted because it measured slower in the serializer benchmarks. So it is unclear
  whether the current 10–16% gap is real or noise; a full (non-short) run on an idle machine is needed.

### Channels (`ChannelThroughputBenchmarks`)

Mean µs per 2000 items over UDS. "Fragmented" forces 1 KB pipe flush chunks instead of 64 KB, so items are split
across reads and `TrySkip` probing hits incomplete items.

| Benchmark | Fragmented | MessagePack runs | MemoryPack runs | Min ratio | Median ratio | Alloc MsgPack / MemPack |
|---|---|---|---|---:|---:|---|
| Persons | False | 736.1 / 585.5 | 326.3 / 322.7 / 310.8 | 1.88 | 2.05 | 249 KB / 223 KB |
| Persons | True | 472.8 / 459.6 | 327.5 / 309.5 / 327.3 | 1.48 | 1.42 | 245 KB / 222 KB |
| Ints | False | 252.1 / 214.6 | 175.1 / 159.4 / 153.6 | 1.40 | 1.46 | 37.7 KB / 30.8 KB |
| Ints | True | 208.3 / 239.7 | 151.6 / 162.3 / 160.2 | 1.37 | 1.40 | 37.7 KB / 31.9 KB |
| IntArrays256 | False | 252.5 / 175.0 | 204.3 / 168.2 / 174.5 | 1.04 | 1.22 | 129 KB / 129 KB |
| IntArrays256 | True | 183.4 / 186.2 | 204.1 / 218.1 / 198.0 | **0.93** | **0.91** | 120 KB / 121 KB |

- **Persons and Ints are 40–90% slower** with MessagePack. The causes are object and string deserialization speed,
  the second pass over every item from `TrySkip` probing, and the ~25 ns fixed per-item deserialize overhead
  (2000 items × 25 ns is about 50 µs, roughly the Ints gap).
- **Anomaly:** MessagePack Persons is consistently **slower non-fragmented than fragmented** (586–740 µs vs
  460–473 µs, over three runs including the earlier one). MemoryPack shows no such difference. This points to
  something in the large-buffer path, for example a writer flush or segment pattern with 64 KB chunks, rather than
  to the serializer itself. It was not profiled.
- **IntArrays256 (ext 78)** is at parity non-fragmented and the only case where fragmented is better than MemoryPack.
- MessagePack allocates 10–20% more for Persons and Ints. The cause was not investigated.

### Gate evaluation (before optimization)

The gate is from `impl-plan.md` §11.3. The change proceeds to Phase 9 only if all five items hold.

| # | Criterion | Measured | Result |
|---|---|---|---|
| 1 | Invocation round trip within **5%** of branch-MemoryPack, allocations no higher | Allocations: **pass** (equal or lower in all 14 cases). Time: no-argument calls at parity (0.98). Untrusted argument calls 1.04–1.16 by min (5 of 7 over 5%). Trusted 0.89–1.11, but MemoryPack's Trusted samples are contaminated. | **Fail (provisional).** Allocations pass. The time gap is close to the noise floor of this setup, so confirming either way needs a full run on an idle machine. |
| 2 | POCO serialize + deserialize within **25%** of MemoryPack | 113.3 vs 87.3 ns (**+30%**, run A); 113.5 vs 90.6 ns (**+25.2%**, run B). Serialize is 15–24% faster; deserialize is 1.7× slower. | **Fail** (marginal). NestedGraph is +37–50% and PersonList100 +85–107%, which are well outside. |
| 3 | Primitive arrays (ext 78) within **15%** | Doubles1K +6–7%: pass. Doubles64K −2% to +3%: pass. Doubles16 **+47–57%**: fail, from the fixed per-call deserialize overhead (47–52 vs 27.5 ns). Channel IntArrays256: +4% non-fragmented, −7% fragmented (min). `int[]` was not benchmarked in `SerializerBenchmarks`. | **Partial.** Passes for arrays of 256 elements or more; fails for small arrays. |
| 4 | Channel throughput within **15%**, and fragmented **better** than branch-MemoryPack | Persons +88% / +48%, Ints +40% / +37% (non-fragmented / fragmented, min). IntArrays256 +4% / −7%. Fragmented is better only for IntArrays256. | **Fail.** |
| 5 | Fuzzing (§10.4) clean for 24 hours | Only the smoke run in `NexNet.Serialization.Tests` (about 45 s, clean). No 24-hour run; harness 3 (the session receive loop) is not implemented. | **Not measured.** |

**Overall: fail.** Items 2 and 4 fail clearly, 1 fails provisionally, 3 is partial and 5 was not measured. As §11.3
allows, the serializer-independent work (framing and header v2, hand-written protocol messages, the unmanaged-channel
removal and the security options) can still ship on its own. The decision belongs to the user, and Phase 8 has not
been started.

#### What would move the gate

All of this is the deserialize path. Serialize and wire size are already at or ahead of MemoryPack.

1. **Fixed per-call reader overhead (~25 ns).** `MsgPackReader` construction over `SequenceReader`, plus the
   `NexusFormatterRegistry.Get<T>()` virtual call per non-primitive member. This affects items 1, 3 (small arrays)
   and 4 (Ints). Candidates: a reader that works directly on a contiguous span with a `SequenceReader` fallback
   (tried once and measured slower; that may have been the implementation rather than the idea), and direct calls to
   generated formatters instead of registry lookups (deviation 6 in `impl-notes.md`).
2. **String and object decode.** Strict UTF-8 decoding, `DateTime.FromBinary`, and the `ReadDouble` peek + switch.
   This affects items 2 and 4 (Persons).
3. **Channel double pass.** Length-prefixed channel items instead of `TrySkip` probing (§7.2) remove the second
   walk over each item. This affects item 4.
4. **The non-fragmented Persons anomaly** should be profiled first, because it is the single largest gap.

### Caveats

- **ShortRun only, and the machine was under background load.** 3 iterations per benchmark; error bars are often
  larger than the differences being judged. The 5% invocation threshold is below the noise floor of this setup.
  Before a final decision, run a full (default job) BenchmarkDotNet pass on an idle machine.
- **No `master` baseline.** All comparisons are branch-MessagePack vs branch-MemoryPack from the same tree, so both
  sides include the v2 framing and the hand-written protocol messages. The plan's `master` column is missing.
- **`CollectionBenchmarks` was not implemented**, so collection fan-out was not measured.
- **Bytes on the wire per invocation and per channel item** (with framing) were not recorded; only per-value payload
  sizes were.
- **Fixed-width mode** and **Untrusted vs Trusted** were measured, but only inside the serializer and invocation
  tables above. The Trusted/Untrusted difference for MessagePack is within noise.
- The serializer benchmarks need the MessagePack build; their MemoryPack column is the MemoryPack library itself,
  not the branch MemoryPack backend.

### Raw output

Raw BenchmarkDotNet output is in the session scratchpad (not in the repository):

| File | Contents |
|---|---|
| `bench-memorypack-run1.txt`, `bench-memorypack-run2.txt`, `bench-MemoryPack-run3.txt` | MemoryPack invocation + channel |
| `bench-msgpack-final.txt`, `bench-MessagePack-run3.txt` | MessagePack invocation + channel, final code |
| `bench-msgpack-ser.txt` (A), `bench-msgpack-ser-final.txt` (B) | Serializer, final code |
| `bench-sizes-final.txt` | Wire sizes |
| `bench-msgpack.txt` | Superseded: MessagePack with the reverted reader fast path |
