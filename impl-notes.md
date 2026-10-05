# Implementation notes: MessagePack serializer experiment

Companion to `impl-plan.md`. This records deviations from the plan, design decisions the plan did not cover, and
outstanding work. Nothing is committed; all changes are in the `worktree-messagepack-eval` worktree.

## Progress snapshot (updated 2026-10-04)

**Done:** Phases 0–7, plus an optimization pass on the MessagePack backend (deviations 27–35). Benchmarks were rerun
as full BenchmarkDotNet jobs with a master baseline; see **`benchmark-results.md`**. **Stopped at the §11.3 gate.**
Phase 8 has not been started; the go/no-go decision belongs to the user.

**Gate result after the optimization pass** (details in `benchmark-results.md`; ratios are min / median of two full
runs per backend):

| # | Criterion | Result |
|---|---|---|
| 1 | Invocation within 5%, allocations no higher | **Pass.** 0.81–1.03 / 0.76–0.95 of branch-MemoryPack. Allocations are equal or lower for argument calls. The back-to-back run-2 pair has 3 of 14 rows at +6–7%, which is below run-to-run noise. |
| 2 | POCO serialize + deserialize within 25% | **Pass.** 0.85 / 0.89 (was 1.25–1.30). |
| 3 | Primitive arrays within 15% | **Pass.** Doubles16 1.04 / 1.03 (was 1.47–1.57), 1K 1.03, 64K 0.99. |
| 4 | Channels within 15%, fragmented better | **Pass, one marginal case.** All within +3% (min) / +9% (median). Fragmented Persons 0.98 / 0.95 and Ints 0.95 / 0.95; IntArrays256 1.02 / 0.99. |
| 5 | Fuzzing clean for 24 h | **In progress, 5 of 6 clean.** `reader`, `messages`, `formatters`, `builtins` and `channel` completed 24 h clean on 2026-10-05 15:30 (0.9–1.5 billion executions each; `channel` 85,663). `session` (restarted after a harness fix) is due around 20:49. Details in `benchmark-results.md`. |

The gate is not formally passed, because item 5 has not been run. Remaining outliers outside the gate:
PersonList100 S+D 1.31–1.34 and Int32 2.7×, the latter a fixed ~12 ns per-call cost.

**Tree state:** committed and pushed to `origin/worktree-messagepack-eval` (WIP commits). The user asked for backups
on the remote branch, which overrides the earlier no-commit rule for this branch. The `bin` folders hold the
**MessagePack** build. Last verified green, with 0 build errors in both backends:

| Backend | Serialization | Generator | Integration |
|---|---|---|---|
| MessagePack | 249/249 | 171/171 | 2566/2566 |
| MemoryPack | 230/230 | 171/171 | 2563/2563 |

The new tests are `ReaderCursorTests` (segment boundaries, empty segments, every string decode path, and invalid or
truncated UTF-8), extra members from a newer peer, out-of-range `DateTime`, and channel reader tests for the
optimistic path. Two of the channel tests, and the split-point test, are MessagePack-only. The split-point test hangs
the legacy MemoryPack read path (deviation 13).

**Next steps (for the user):** decide on the gate. A 24-hour libFuzzer run of all six harnesses started 2026-10-04 15:30 (ends about 2026-10-05 15:30), on
commit `bee2bc8`, from `<scratchpad>/fuzzrun` (`start-24h.ps1`, `status.ps1`). If it stays clean,
gate item 5 passes. If the 5% invocation margin matters, do a full benchmark run on an idle machine.

## Status by phase

| Phase | Status |
|---|---|
| 0: Framing and protocol header v2 | Done. Little-endian body length, byte-pair pipe IDs, protocol tag compared as bytes, version 2, payload-format byte, dead `ReadingHelpers` removed. Spec written to `docs/internals/wire-protocol.md`. |
| 1: Protocol messages | Done. All messages and the collection union are hand-written MessagePack; `MessagePool` rejects trailing bytes. |
| 2: Serializer core | Done. Shared project `src/NexNet.Serialization`: writer, reader, non-recursive skip, formatter base/registry, built-ins, ext 78, options, exceptions. |
| 3: Attributes and generated formatters | Done. Formatters for `[NexusObject]` (classes, structs, records, constructors, init/required, private members via `UnsafeAccessor`, unions, generics), inline arguments, results, `TypeHasher` support, NEXNET028–034/036/037. |
| 4: Channels and pipes | Done. Channel reader probes with `TrySkip` (no exceptions on partial items). NEXNET038 analyzer. Unmanaged channel API removed. |
| 5: Options and security | Done. `ConfigBase.SerializerOptions` (Untrusted default) reaches message pools, generated invokers, results, channels and collections. |
| 6: Collections | Done. Union messages hand-written; values go through the active payload backend. |
| 7: Benchmarks | Done, plus an optimization pass. Full in-process jobs, 2 runs per backend plus master, interleaved; gate re-evaluated in `benchmark-results.md`. **Items 1–4 pass (4 marginal in one case); 5 not measured.** |
| 8/9: Remove MemoryPack, docs and migration | **Not started**, by instruction (the gate decision belongs to the user). |

## Deviations from the plan

### Backend switch and hashing
1. **The switch lives in `src/Directory.Build.props`, not `NexNet.props`.** Test and sample projects don't import
   `NexNet.props`, so a directory-level props file is the only place that reaches every project. The generator package
   does **not** yet ship a `build/NexNet.Generator.props` with the `CompilerVisibleProperty`, so NuGet consumers would
   always get the MessagePack backend (the generator default). This is outstanding.
2. **`TypeHasher` is attribute-driven, not mode-driven.** A type with `[NexusObject]` is hashed by its keyed members in
   both builds; a `[MemoryPackable]`-only type is hashed as before. Existing `HashLock` values for MemoryPack-only types
   are unchanged. Adding `[NexusObject]` to a type changes its hash identically in both builds, so client and server
   always agree. User-formatter types are hashed by name only; the formatter's FQN is not included because
   `TypeHasher` has no compilation access.

### Wire format
3. **With the MemoryPack payload format, payload values inside protocol messages are wrapped in `bin`.** Arguments,
   results and collection values are MemoryPack bytes in that build, which is not MessagePack, so they cannot be
   embedded raw. With the MessagePack format they are embedded, as planned.
4. **`IInvocationMessage.MaxArgumentSize` differs per build:** 65,526 (MessagePack) / 65,523 (MemoryPack, +3 for the
   bin16 header).
5. **Invocations without arguments** send `0x90` as planned. The receiver normalizes that back to `Memory<byte>.Empty`
   so "no arguments" looks the same before and after the wire.

### Generated code
6. **Generated code calls `NexusFormatterRegistry.Get<T>()` for non-primitive types** instead of calling generated
   formatter instances directly. Cost: one static generic field read plus one virtual call. This avoids resolving
   cross-file and cross-assembly formatter names. Primitives and strings still call the writer and reader directly.
7. **Arguments are read by a generated static helper (`__ReadArguments_{id}`) with `out` parameters,** and results are
   written with `NexusSerializer.Serialize<T>(IBufferWriter, T)`. Reason: `InvokeMethodCore` is `async`, and ref
   struct (`MsgPackReader`/`MsgPackWriter`) locals in async methods need C# 13. The generator tests compile as C# 11.
8. **Formatters for every `[NexusObject]` type reachable from a nexus are emitted as `file`-scoped classes in that
   nexus's generated file,** plus one file per locally declared non-generic `[NexusObject]` type (for types used only
   at runtime). The same type can therefore be generated more than once. Registration is first-wins and the outputs
   are identical, so this is safe, but it duplicates code. A per-assembly dedupe is a follow-up.
9. **Proxy argument buffers:** `IProxyInvoker` gained default-interface overloads that take a `PooledArrayBufferWriter`
   and return it to its pool after sending. The plan's signature change was not made. `TryGetResult<T>` uses the
   registry rather than receiving a formatter instance; deserialization options are captured when the result message
   is read.
10. **Generic `[NexusObject]` types with non-public keyed members are not supported** (NEXNET031). `UnsafeAccessor` on
    generic owners was out of scope.
11. **NEXNET035 is not a separate diagnostic.** The `where TFormatter : NexusFormatter<T>, new()` constraint on
    `NexusFormatterAttribute<TFormatter, T>` already enforces it at compile time.
12. **The NEXNET038 analyzer is heuristic.** It accepts built-ins, the pre-registered closed generics, non-generic
    `[NexusObject]` types, user-formatted types and assembly-declared types. A closed generic used with a channel that
    happens to be registered through some nexus signature, but is not declared, is still reported; the fix is to
    declare it. No false positives occurred in the repository.

### Runtime
13. **The channel reader marks all buffered bytes as examined when an item is incomplete,** using a new
    `NexusPipeReader.AdvanceToExamined(consumed, examined)`, so the next read waits for data. The previous code
    re-read the same buffer in a loop (busy spin) because `AdvanceTo(int, int)` accumulates the examined position.
14. **Collection connection arguments** reuse `DeserializeArguments<ValueTuple<byte>>` in the generated collection
    invoker, backed by a built-in `ValueTupleFormatter<byte>` registration. The collection emitter is unchanged.
15. **The broadcast channels (collection sync) always use the NexNet union formatter,** including in the MemoryPack
    build, through new `NexusChannelReader/Writer` constructors that take an explicit formatter.
16. **`ClientGreetingMessage.Dispose`** previously returned early (without returning the message to the pool) when the
    auth token was empty; it now always returns it. This is a small bug fix.

### Tests
17. **Unmanaged channel removal:**
    - Deleted `NexusChannelReaderUnmanagedTests`, `NexusChannelReaderWriterUnmanagedTests` and
      `NexusChannelWriterUnmanagedTests`.
    - Converted the unmanaged cases in `Nexus{Client,Server}Tests_ChanneReaderIAsyncEnumerable` to `GetChannelReader/Writer<int>`.
    - Converted `GeneratorChannelTests.GeneratesUnmanagedChannel` into `GeneratesChannelOfNexusObjectStruct`.
18. **Two tests encoded MemoryPack-specific byte assumptions and were adjusted to keep their intent in both backends:**
    - `ClientThrowsWhenArgumentTooLarge` used `new byte[65521]`, which fit under the old limit only because of
      MemoryPack's 4-byte header. It now uses `MaxArgumentSize` bytes, which always exceeds the limit once framed.
    - `ReaderCompletesOnPartialRead` wrote `{1,2,3,4}`, which is four complete MessagePack integers. It now writes
      `{0xd3,1,2,3}`, which is partial in both formats.
19. **Four `VersioningTests` that exercise MemoryPack attributes** (`MemoryPackableObjects`,
    `VersionLock_MemoryPack_...`, `MemoryPackable_Interface`, `MemoryPackable_NestedCreation`) now pass
    `SerializerBackendOptions.MemoryPack` explicitly; their hashes and expectations are unchanged. MessagePack-backend
    equivalents were added in `GeneratorSerializationTests`.
20. **`NexNet.Serialization.Tests` excludes `GeneratedFormatterTests` and `FuzzSmokeTests` in the MemoryPack build**
    (`#if !NEXNET_MEMORYPACK`), because the generator emits no formatters for that backend.
21. **StreamStruct was not modified.** `RawTcpClient` builds and parses bodies with the hand-written message code via
    `TestSerialization`; framing still uses StreamStruct. Requirements are in `streamstruct-requirements.md`.

### Fuzzing and benchmarks
22. **Fuzz harness 3 (`session`) drives a real `NexusServer` over an in-memory transport**, one connection per input,
    instead of calling the private `ProcessMessages`. Byte 0 of the input selects whether a valid preamble and a valid
    `ClientGreeting` are sent first. A failure is any of: an exception escaping the harness, a session still open 5 s
    after the stream ends, or the server logging an exception type other than serialization or cancellation errors.
    The fuzz nexus has methods taking primitives, strings, POCOs, unions, channels, a raw pipe and a
    `CancellationToken`.
    - **Tooling:** libFuzzer through `libfuzzer-dotnet` (Windows build). The harness is selected with the
      `NEXNET_FUZZ_HARNESS` environment variable, and `--export-corpus` writes the seeds.
    - **Instrumentation:** a module initializer points SharpFuzz's trace at a scratch buffer, because the generated
      formatter registrations run NexNet code before the fuzzer starts.
    - **Bugs found and fixed** (all also present on master):
      - an unknown message type fell through to an out-of-range `Slice` (`breakLoop` was never set);
      - a stream ending inside the preamble spun the read loop until the handshake timeout;
      - pooled pipe managers were reused while the old session's invocations could still register pipes into them,
        attaching pipes to another session;
      - `RegisterPipe`/`RentPipe` raced with `CancelAll`;
      - sends raced with disconnect: the output was nulled or completed by another thread, so `PipeWriter` was used
        from two threads.
    - **False positive during the 24 h run:** the first `session` run stopped after 19 min on a connection that
      closed only at the 30 s idle timeout. The cause is the per-connection invocation limit
      (`MaxConcurrentConnectionInvocations` = 2). With both slots held by calls blocked on channels, the receive loop
      waits for a slot and stops reading by design, so it can't see the stream end. That is the same as master, and
      not a vulnerability: an idle client can hold a connection that long anyway. The fuzz server now uses
      `Timeout = 2000`, so these connections close inside the 5 s bound.
    - The failing inputs are kept in `Corpus/session-regressions.hex`. The smoke run (in `FuzzSmokeTests`) now takes
      about 50 s to 2.5 min.
23. **The `master` baseline is a separate worktree** (`git worktree add --detach <scratchpad>/master-baseline 57fef36`).
    The branch's invocation and channel benchmarks were ported there, with MemoryPack-only versions of the bench
    types and no `Security` parameter (master has no serializer options). `CollectionBenchmarks` was not implemented.
24. **`BenchmarkConfig`** targeted `CoreRuntime.Core90` although the project is net10.0. It now uses the host runtime.
    `NEXNET_BENCH_SHORT=1` selects an in-process `ShortRun`, so the already built backend is measured.
26. **Benchmark method for the gate:** the machine was under background load, so every benchmark was run twice per
    backend as a full BenchmarkDotNet job, in-process, alternating MessagePack, MemoryPack and master, with a
    rebuild before each backend switch. The gate is judged on the **minimum across runs**, with the median also
    reported. The plan assumed one run per build. The earlier ShortRun pass is kept in `benchmark-results.md` under
    "Before optimization".

### Other
25. **The shared project (`.shproj`) is not in `NexNet.slnx`,** because the dotnet CLI cannot build `.shproj` files. It is
    imported by `NexNet.csproj`. The spec lives at `docs/internals/wire-protocol.md` (next to the existing v1 spec)
    instead of `docs/wire-protocol.md`.

### Optimization pass (2026-10-03)
The wire format is unchanged: golden vectors and wire sizes are identical before and after.
27. **`MsgPackReader` no longer wraps `SequenceReader<byte>`.** The cursor is the current segment as a span plus an
    index, with out-of-line slow paths for segment crossing (`MoveNextSegment`, `TryReadByteSlow`, `AdvanceSlow`,
    `TryReadBigEndianSlow`, `TryCopyToSlow`). Reaching the end of a segment does not advance to the next one until
    a read needs more bytes. Positions are computed from the current segment's `SequencePosition`.
    - **Why the earlier "span fast path" measured slower:** it was not kept, so the cause can't be confirmed. The likely
      reason is that it added a span alongside `SequenceReader` rather than replacing it, which grew the struct and
      added a branch to every read. The rewrite replaces the cursor entirely; the struct is smaller and hot reads
      touch only `_span`/`_index`.
    - The `ReadOnlyMemory<byte>` constructor uses `MemoryMarshal.TryGetArray` to build the sequence and span without
      the out-of-line `ReadOnlySequence(ReadOnlyMemory)` constructor.
28. **String decoding** no longer goes through `Encoding.GetString`.
    - ASCII is checked with `Ascii.IsValid` and widened straight into the new string (`string.Create`).
    - Other text is transcoded once with `Utf8.ToUtf16` into a stack buffer (≤ 256 bytes) or a pooled one, then copied.
    - Strict mode reports invalid or truncated UTF-8 as `NexusSerializationException` (inner `DecoderFallbackException`);
      Trusted replaces it with U+FFFD, as before.
29. **Generated object formatters read members in a straight line when `count` equals the expected array length**
    (max key + 1). Gaps are skipped. The `for`/`switch` loop remains for older peers (fewer members) and newer peers
    (extra members, skipped).
30. **Inline fast paths:**
    - `ReadDouble`/`ReadSingle` for float64/float32.
    - `ReadInt32` for fixints. Its slow path decodes the uint8/16 and int8/16/32 forms straight from the span before
      falling back to `ReadInt64`.
31. **`DateTimeFormatter.Deserialize` decodes Utc and Unspecified values directly.** It validates the ticks and calls
    `new DateTime(ticks, kind)`, which gives the same result as `FromBinary`. Local values still go through
    `FromBinary`, which does the time-zone conversion. The `try`/`catch` moved to that slow path.
32. **ext 78 writes for arrays up to 4096 bytes use one `GetSpan`** for header, kind byte and data
    (`MsgPackWriter.WriteExtHeader(Span<byte>, …)`). Reads take the kind byte with `ReadRawByte` instead of a
    stackalloc copy.
33. **`TrySkip` walks the current span with locals (`SkipInSpan`)** and falls back to the segment-crossing version.
    The old version copied the whole reader three times per call, and the copy back through `this` went through
    GC write-barrier helpers. A 256-entry size table was tried and measured slower than the switch, so it was
    reverted.
34. **Channel reads are optimistic away from the end of the buffer.** `NexusChannelReader` remembers the largest item
    it has read. While the bytes left exceed twice that plus 16, items are deserialized without probing.
    - If such a read throws, the reader restarts at the item and probes it with `TryGetNextValueLength`. An
      incomplete item waits for more data; a complete item is malformed, so the error is rethrown.
    - The fallback needs an item more than twice the largest seen, so it can happen at most about
      log2(`MaxBufferedItemSize`) times per channel.
    - Items near the end of the buffer are probed first and still get the "consumed exactly one value" check;
      optimistic items don't. A formatter that consumes the wrong amount corrupts the following items, which then fail.
35. **Benchmark harness:**
    - `NEXNET_BENCH_INPROC=1` runs the default (full) job in-process with a 30-minute toolchain timeout. The default
      5-minute timeout killed full runs of the channel benchmarks.
    - `NEXNET_BENCH_MEDIUM=1` runs 10 warmups and 15 iterations in-process, for A/B checks.
    - The ShortRun "Persons is slower non-fragmented" anomaly was a warmup artifact: `Fragmented=False` always runs
      first in the process. With proper warmup (or a full job) it disappears.

## Outstanding work

- Generator NuGet `build/*.props` carrying `CompilerVisibleProperty Include="NexNetSerializer"` (packaging).
- Nightly libFuzzer CI job. The channel fuzz harness is slow (about 10 inputs/s, from its 5 ms read timeouts).
- Delete the now-unused `PipeManagerPool` (pipe managers are no longer reused).
- `CollectionBenchmarks`. Optionally, a full run on an idle machine to settle the 5% invocation margin.
- PersonList100 (S+D 1.31–1.34): profile `ListFormatter<T>` and the writer's per-member path.
- Channel Ints allocates ~3.7 KB/op more than MemoryPack (not investigated).
- Dedupe generated formatters per assembly.
- Assume-ASCII string write variant (§11 follow-up). The span-based reader is done (deviation 27).
- Phases 8–9 after the gate decision: remove MemoryPack, update `llm-usage.md`, `llm-dev.md` and DocFX, write the
  migration guide.
