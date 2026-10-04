# Implementation notes: MessagePack serializer experiment

Companion to `impl-plan.md`. This records deviations from the plan, design decisions the plan did not cover, and
outstanding work. Nothing is committed; all changes are in the `worktree-messagepack-eval` worktree.

## Progress snapshot (updated 2026-10-03)

**Done:** Phases 0–7. The final benchmark pass is written up in **`benchmark-results.md`**. **Stopped at the
§11.3 gate**, which fails as measured. Phase 8 has not been started; the go/no-go decision belongs to the user.

**Gate result** (details and numbers in `benchmark-results.md`):

| # | Criterion | Result |
|---|---|---|
| 1 | Invocation within 5%, allocations no higher | Fail (provisional). Allocations are lower in every case; Untrusted argument calls are +4–16% (min of runs), close to the noise floor. |
| 2 | POCO serialize + deserialize within 25% | Fail, marginally: +25% to +30%. |
| 3 | Primitive arrays within 15% | Partial: 1K and 64K pass (−2% to +7%); 16 elements fail (+47–57%). |
| 4 | Channels within 15%, fragmented better | Fail. Persons +48–88%, Ints +37–40%; IntArrays256 at parity or better. |
| 5 | Fuzzing clean for 24 h | Not measured; smoke run only. |

The root cause is deserialization: a fixed ~25 ns per-call reader overhead plus slower string and object decoding.
Serialization speed and wire size are at or ahead of MemoryPack.

**Tree state:** final code, unchanged since the last green run. The Phase 7 benchmark pass made no code changes.
The `bin` folders hold the **MessagePack** build (rebuilt last, 0 errors). Last verified green, with 0 build errors
in both backends:

| Backend | Serialization | Generator | Integration |
|---|---|---|---|
| MessagePack | 228/228 | 171/171 | 2562/2562 |
| MemoryPack | 210/210 | 171/171 | 2562/2562 |

The `MsgPackReader` span fast path was reverted, because it measured slower. `InvocationMessage.ArgumentsOwner`
(the pooled argument buffer, returned on dispose after sending) is kept.

**Next steps (for the user):** decide on the gate. If more data is wanted, start with a full (non-short) benchmark run
on an idle machine, plus profiling of the deserialize path and the non-fragmented Persons channel anomaly. The
candidate fixes are under "What would move the gate" in `benchmark-results.md`.

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
| 7: Benchmarks | Done. Final short in-process pass for both backends (invocation and channel runs repeated and interleaved); gate evaluated in `benchmark-results.md`. **Gate fails.** |
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
22. **Fuzz harness 3 (the session receive loop over an in-memory pipe) is not implemented.** It needs the private
    `ProcessMessages` path. Implemented: `reader`, `messages`, `channel`, `formatters`, `builtins`. The smoke run takes
    about 45 s, mostly the channel harness's short read timeouts.
23. **No `master` baseline benchmark run.** Comparisons are branch-MemoryPack vs branch-MessagePack, built from the same
    tree. `CollectionBenchmarks` was not implemented.
24. **`BenchmarkConfig`** targeted `CoreRuntime.Core90` although the project is net10.0. It now uses the host runtime.
    `NEXNET_BENCH_SHORT=1` selects an in-process `ShortRun`, so the already built backend is measured.
26. **Benchmark method for the gate:** other workloads were loading the machine during the final pass (50–75% CPU), so
    the invocation and channel benchmarks were run 2× (MessagePack) and 3× (MemoryPack), interleaved by backend with a
    rebuild between runs. The gate is judged on the **minimum across runs**, with the median also reported. The plan
    assumed one run per build. Full (non-short) runs were not done.

### Other
25. **The shared project (`.shproj`) is not in `NexNet.slnx`,** because the dotnet CLI cannot build `.shproj` files. It is
    imported by `NexNet.csproj`. The spec lives at `docs/internals/wire-protocol.md` (next to the existing v1 spec)
    instead of `docs/wire-protocol.md`.

## Outstanding work

- Generator NuGet `build/*.props` carrying `CompilerVisibleProperty Include="NexNetSerializer"` (packaging).
- Fuzz harness for the session receive loop; nightly libFuzzer CI job.
- `CollectionBenchmarks`; a benchmark run against `master` as a third baseline; full (non-short) benchmark runs on an
  idle machine; profiling of the deserialize path and of the MessagePack non-fragmented Persons channel anomaly (it is
  slower than the fragmented case; see `benchmark-results.md`).
- Dedupe generated formatters per assembly.
- Assume-ASCII string write variant and a span fast path in the reader that bypasses `SequenceReader` (§11 follow-ups).
- Phases 8–9 after the gate decision: remove MemoryPack, update `llm-usage.md`, `llm-dev.md` and DocFX, write the
  migration guide.
