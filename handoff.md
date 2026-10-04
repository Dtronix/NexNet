# Handoff: NexNet MessagePack serializer experiment

Worktree: `Z:\Projects\NexNet\NexNet-master\.claude\worktrees\messagepack-eval` (branch `worktree-messagepack-eval`).
Nothing is committed; all changes are in the working tree.

## 1. Read first
- **`impl-plan.md`**: the source of truth (decisions in §0, order of work in §17).
- **`impl-notes.md`**: the progress snapshot, the full list of deviations from the plan, and outstanding work.
- `messagepack-v4-report.md`: background research.
- `streamstruct-requirements.md`: what StreamStruct would need for the wire tests.
- `docs/internals/wire-protocol.md`: the v2 wire spec.

## 2. Phase status

| Phase | Status |
|---|---|
| 0: Framing / header v2 | Done |
| 1: Protocol messages (hand-written MessagePack) | Done |
| 2: Serializer core (shared project `src/NexNet.Serialization`) | Done |
| 3: Attributes and generated formatters, inline arguments, `TypeHasher`, NEXNET028–034/036/037 | Done |
| 4: Channels (`TrySkip` probing), NEXNET038 analyzer, unmanaged-channel removal | Done |
| 5: Options and security plumbing | Done |
| 6: Collections | Done |
| 7: Benchmarks | **Done, plus an optimization pass** (impl-notes deviations 27–35). Full benchmark runs and the gate re-evaluation are in `benchmark-results.md`: **items 1–4 pass (item 4 marginal in one case), item 5 (24 h fuzzing) not measured.** Work is stopped at the gate. |
| 8: Remove MemoryPack | Not started. **Do not start**; the gate decision belongs to the user. |
| 9: Docs and migration | Not started (comes after the gate). |

## 3. Tree state (verified on the final code)
Both backends build with 0 errors. Everything is committed and pushed to `origin/worktree-messagepack-eval`.

| Backend | Serialization tests | Generator tests | Integration tests |
|---|---|---|---|
| MessagePack (default) | 249/249 | 171/171 | 2566/2566 |
| MemoryPack | 230/230 | 171/171 | 2563/2563 |

Commands, run from `src/`:
```
dotnet build -c Release -p:NexNetSerializer=MessagePack      # or MemoryPack
dotnet test NexNet.Serialization.Tests -c Release --no-build -p:NexNetSerializer=<B>
dotnet test NexNet.Generator.Tests     -c Release --no-build -p:NexNetSerializer=<B>
dotnet test NexNet.IntegrationTests    -c Release --no-build -p:NexNetSerializer=<B>
```
The AOT validation also passes in the MessagePack build without suppressions:
`dotnet build Samples/NexNetSample.Aot.Client -c Release --no-restore -p:TreatWarningsAsErrors=true`.

The baseline before the work was 2637 integration and 149 generator tests. The integration count is lower because
the unmanaged-channel test files were deleted, as the plan requires.

## 4. Work in progress
- **No files are mid-change.** The tree builds and is green on both backends. The `bin` folders hold the
  **MessagePack** build.
- A master baseline worktree exists at `<scratchpad>/master-baseline` (detached at 57fef36, ported benchmark files
  uncommitted). Remove it with `git worktree remove --force <path>` when it is no longer needed.
- Remaining before a go decision: a 24-hour fuzz run (§10.4; harness 3, the session receive loop, is not
  implemented). If the 5% invocation margin matters, a full benchmark run on an idle machine.

## 5. Key design points (details in impl-notes.md, "Deviations")

**Backend switch.** `src/Directory.Build.props` sets `NexNetSerializer` (default `MessagePack`), defines
`NEXNET_MEMORYPACK` for the MemoryPack build, and exposes `CompilerVisibleProperty`. `NexNet.csproj` references
MemoryPack only in the MemoryPack build. The test projects, `NexNetDemo` and the benchmarks always reference MemoryPack.
The generator reads `build_property.NexNetSerializer`. There is no generator NuGet `build/*.props` yet (outstanding).

**Serializer code.** `src/NexNet.Serialization/` is a shared project imported by `NexNet.csproj`; it is not in the
`.slnx` because the CLI can't build a `.shproj`. Key types:
- `MsgPackWriter` and `MsgPackReader`. The reader's cursor is the current segment as a span plus an index, with
  out-of-line segment-crossing slow paths (no `SequenceReader`; impl-notes deviation 27).
- `NexusFormatter<T>`, `NexusFormatterRegistry` and `NexusFormatterCache<T>` (first registration wins).
- `BuiltInFormatters`, `PrimitiveArrayCodec` (ext 78), `NexusSerializerOptions`, `NexusSerializer`, `PooledArrayBufferWriter`.

**Protocol messages.** `IMessageBase.Serialize` / `Deserialize` are hand-written. Payload values inside messages are
embedded raw with the MessagePack format and wrapped in `bin` with MemoryPack (`PayloadSerializer.WriteEmbedded`).
The protocol header byte 4 is the payload format (`Internals/PayloadFormat.cs`); the protocol version is 2.

**Argument buffer ownership.** Generated proxies write the arguments into `PooledArrayBufferWriter.Rent()` and call the
`IProxyInvoker` overloads that take the buffer. `ProxyInvocationBase` passes it on as `argumentsOwner`; it is set on
`InvocationMessage.ArgumentsOwner` and returned in `InvocationMessage.Dispose()` once the message has been sent.
`ISessionInvocationStateManager.InvokeMethodWithResultCore` has a new optional `argumentsOwner` parameter. Early-exit
paths return the buffer explicitly.

**Generator** (`src/NexNet.Generator`):
- `Serialization/SerializationBuilder.cs` walks the types reachable from nexus interfaces and emits `file`-scoped
  formatter classes plus a `[ModuleInitializer]` that registers them. The result is stored as a string in
  `NexusGenerationData.SerializationCode`.
- `NexusGenerator.cs` also has a pipeline for locally declared `[NexusObject]` types and one for the assembly-level
  `[NexusSerializable<T>]` / `[NexusFormatter<,>]` attributes.
- `MethodEmitter` emits the `__ReadArguments_{id}` static helpers. Ref struct readers can't be locals in the async
  `InvokeMethodCore` before C# 13, and the generator tests compile as C# 11.
- `Serialization/ChannelTypeAnalyzer.cs` reports NEXNET038. `TypeHasher` is driven by attributes (`[NexusObject]`
  members hashed by key), not by the backend.

**Channels.** `NexusChannelReader` deserializes items optimistically while the buffer holds more than twice the
largest item seen. It probes the tail items with `TryGetNextValueLength` (impl-notes deviation 34), then calls
`NexusPipeReader.AdvanceToExamined`, so an incomplete item waits for more data instead of spinning. The broadcast/collection channels always use the NexNet
union formatter, through constructors that take an explicit formatter.

**Unmanaged channels** are removed (API, files and generator support).

## 6. Gotchas
- **The worktree command guard rejects complex shell commands** (relative `..` paths, `cd` into subfolders followed
  by writes, awk, quoted globs). Put the logic in a script under the scratchpad and run `bash script.sh` or
  `python ed.py script.py`. Helper scripts in the scratchpad:
  - `ed.py`: replace/rewrite helper (preserves BOM and CRLF).
  - `alltests.sh`: builds and runs all suites for both backends into `alltests.txt`.
  - `bench.sh <label> <filters...>`: runs the benchmarks in-process with `NEXNET_BENCH_SHORT=1` into `bench-<label>.txt`.
    The label `msgpack` also writes `bench-sizes.txt`.
  - `bench_steps.sh <step...>` (2026-10-04 session scratchpad): full in-process runs. Steps are `build:<Backend|master>`,
    `sizes`, `net:<label>`, `master:<label>` and `ser:<label>`. Keep each invocation under the 2-hour background limit.
  - `agg.py`: aggregates `full/*-run*.txt` into min/median tables.
- **Don't build while tests or benchmarks are running.** It locks binaries and skews timings. Building switches the
  shared `bin`/`obj` folders between backends, so always rebuild before `--no-build` tests or benchmarks.
- **Timing:** the integration suite takes about 3 minutes per backend. `NexNet.Serialization.Tests` takes about 50 s in
  the MessagePack build, mostly the fuzz smoke test's channel harness.
- **The BenchmarkDotNet ShortRun has large error bars** and too little warmup: the first benchmark case in the
  process runs slow (this caused the "Persons slower non-fragmented" anomaly). Use full jobs for decisions:
  `NEXNET_BENCH_INPROC=1` (full, in-process, 30-minute timeout) or `NEXNET_BENCH_MEDIUM=1` (10 warmups and
  15 iterations) for A/B checks. The serializer benchmarks need the MessagePack build.
- **Machine load drifts by 20–40% over hours** on this box. Compare backends only in back-to-back or interleaved runs,
  never against a run taken much earlier.
- **Runtime-only types must be declared** with `[assembly: NexusSerializable<T>]`. Example: `List<BenchPerson>` in the
  benchmarks failed until it was declared.
- **Python heredocs in bash break on backslashes;** use script files instead.

## 7. Ground rules
- No PRs and no merges. **WIP commits pushed to `origin/worktree-messagepack-eval` are wanted** as a backup
  (the user asked for this on 2026-10-03). Never push to master.
- Both backends (`-p:NexNetSerializer=MessagePack` and `MemoryPack`) must be green on all three test projects before
  moving on. Don't skip, weaken or delete tests, except the unmanaged-channel tests the plan removes.
- Stop at the Phase 7 gate. Do **not** remove MemoryPack (Phase 8).
- Don't modify StreamStruct; record needs in `streamstruct-requirements.md` only.
- Don't copy MessagePack-CSharp v4 code; write it yourself.
- Log design decisions the plan doesn't cover in `impl-notes.md`.

## 8. Benchmark numbers
See `benchmark-results.md` for the full-job results after the optimization pass (min/median, both backends plus
master) and the gate evaluation. The earlier ShortRun results are kept there under "Before optimization".
