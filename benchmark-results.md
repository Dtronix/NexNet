# Benchmark results: NexNet MessagePack vs MemoryPack (Phase 7)

Branch `worktree-messagepack-eval`, final code (MsgPackReader on `SequenceReader`; the span fast path is reverted).
Both backends were built from the same tree with `-p:NexNetSerializer=MessagePack|MemoryPack`. All runs are
in-process BenchmarkDotNet `ShortRun` (3 warmups, 3 iterations, 1 launch) with `NEXNET_BENCH_SHORT=1`, on .NET 10.0.12,
Windows 11 x64 (AVX2), on 2026-10-03.

**Verdict: the §11.3 gate fails.** Serialization speed and wire size are good. Deserialization, channel throughput
and the argument-carrying invocations miss their thresholds. Details are in [Gate evaluation](#gate-evaluation).

## Read this first: noise

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

## Wire sizes

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

## Serializer (`SerializerBenchmarks`)

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

## Invocations (`InvocationBenchmarks`)

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

## Channels (`ChannelThroughputBenchmarks`)

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

## Gate evaluation

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

### What would move the gate

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

## Caveats

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

## Raw output

Raw BenchmarkDotNet output is in the session scratchpad (not in the repository):

| File | Contents |
|---|---|
| `bench-memorypack-run1.txt`, `bench-memorypack-run2.txt`, `bench-MemoryPack-run3.txt` | MemoryPack invocation + channel |
| `bench-msgpack-final.txt`, `bench-MessagePack-run3.txt` | MessagePack invocation + channel, final code |
| `bench-msgpack-ser.txt` (A), `bench-msgpack-ser-final.txt` (B) | Serializer, final code |
| `bench-sizes-final.txt` | Wire sizes |
| `bench-msgpack.txt` | Superseded: MessagePack with the reverted reader fast path |
