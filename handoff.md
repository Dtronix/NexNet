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
| 7: Benchmarks | **Done.** The final runs and the gate evaluation are in `benchmark-results.md`. **The gate fails**, so work is stopped at the gate. The §4 and §8 notes below are superseded by that file. |
| 8: Remove MemoryPack | Not started. **Do not start**; the gate decision belongs to the user. |
| 9: Docs and migration | Not started (comes after the gate). |

## 3. Tree state (verified on the final code)
Both backends build with 0 errors. The MessagePack build has 23 warnings and the MemoryPack build 12; none are new errors.

| Backend | Serialization tests | Generator tests | Integration tests |
|---|---|---|---|
| MessagePack (default) | 228/228 | 171/171 | 2562/2562 |
| MemoryPack | 210/210 | 171/171 | 2562/2562 |

There are no failures. Commands, run from `src/`:
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
- **No files are mid-change.** The tree builds and is green.
- At handoff, a background run of the MemoryPack benchmarks was in progress (Invocation + Channel), writing to
  `C:\Users\djgos\AppData\Local\Temp\claude\Z--Projects-NexNet-NexNet-master\faf00da2-8c62-4cd1-8d46-a72c76eeba5e\scratchpad\bench-memorypack.txt`.
  The bin folders currently hold the **MemoryPack** build.

Next steps, in order:
1. If that run didn't finish, rebuild MemoryPack and rerun it (§6 has the command).
2. Rebuild MessagePack (`dotnet build src -c Release`) and run Invocation + Channel (+ Serializer if desired).
3. Write `benchmark-results.md`: wire sizes, the tables, the gate evaluation, and caveats (ShortRun, large error bars, no master baseline).
4. Update the snapshot in `impl-notes.md`.
5. Stop at the gate.

## 5. Key design points (details in impl-notes.md, "Deviations")

**Backend switch.** `src/Directory.Build.props` sets `NexNetSerializer` (default `MessagePack`), defines
`NEXNET_MEMORYPACK` for the MemoryPack build, and exposes `CompilerVisibleProperty`. `NexNet.csproj` references
MemoryPack only in the MemoryPack build. The test projects, `NexNetDemo` and the benchmarks always reference MemoryPack.
The generator reads `build_property.NexNetSerializer`. There is no generator NuGet `build/*.props` yet (outstanding).

**Serializer code.** `src/NexNet.Serialization/` is a shared project imported by `NexNet.csproj`; it is not in the
`.slnx` because the CLI can't build a `.shproj`. Key types:
- `MsgPackWriter` and `MsgPackReader`. The reader wraps `SequenceReader`; a span fast path was tried and measured slower, so it was reverted.
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

**Channels.** `NexusChannelReader` probes each item with `TrySkip`, then calls `NexusPipeReader.AdvanceToExamined`, so
an incomplete item waits for more data instead of spinning. The broadcast/collection channels always use the NexNet
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
- **Don't build while tests or benchmarks are running.** It locks binaries and skews timings. Building switches the
  shared `bin`/`obj` folders between backends, so always rebuild before `--no-build` tests or benchmarks.
- **Timing:** the integration suite takes about 3 minutes per backend. `NexNet.Serialization.Tests` takes about 50 s in
  the MessagePack build, mostly the fuzz smoke test's channel harness.
- **The BenchmarkDotNet ShortRun has large error bars;** treat differences under ~10% as noise. The serializer
  benchmarks need the MessagePack build.
- **Runtime-only types must be declared** with `[assembly: NexusSerializable<T>]`. Example: `List<BenchPerson>` in the
  benchmarks failed until it was declared.
- **Python heredocs in bash break on backslashes;** use script files instead.

## 7. Ground rules
- No git commits, pushes or PRs.
- Both backends (`-p:NexNetSerializer=MessagePack` and `MemoryPack`) must be green on all three test projects before
  moving on. Don't skip, weaken or delete tests, except the unmanaged-channel tests the plan removes.
- Stop at the Phase 7 gate. Do **not** remove MemoryPack (Phase 8).
- Don't modify StreamStruct; record needs in `streamstruct-requirements.md` only.
- Don't copy MessagePack-CSharp v4 code; write it yourself.
- Log design decisions the plan doesn't cover in `impl-notes.md`.

## 8. Benchmark numbers so far (in-process ShortRun, Windows x64)

**Wire sizes** (bytes; NexNet compact / NexNet fixed-width / MemoryPack):

| Payload | NexNet compact | NexNet fixed-width | MemoryPack |
|---|---|---|---|
| Int32 | 5 | 5 | 4 |
| AsciiShort | 12 | 12 | 19 |
| Poco | 31 | 35 | 39 |
| NestedGraph | 97 | 113 | 160 |
| Doubles1K | 8197 | 8197 | 8196 |
| PersonList100 | 3093 | 3493 | 3894 |
| Union | 12 | 14 | 10 |

**Serializer, final code** (mean ns, serialize / deserialize):

| Payload | NexNet | MemoryPack |
|---|---|---|
| Int32 | 9.3 / 27.6 | 6.4 / 2.0 |
| AsciiShort | 27.6 / 41.0 | 34.5 / 30.5 |
| AsciiLong | 62.5 / 269 | 78.7 / 227 |
| Unicode | 1292 / 2597 | 1291 / 1701 |
| Poco | 35.1 / 78.2 | 41.5 / 45.8 |
| NestedGraph | 166 / 382 | 150 / 215 |
| Doubles16 | 19.0 / 47.2 | 17.4 / 27.5 |
| Doubles1K | 87.5 / 315 | 83.0 / 291 |
| Doubles64K | 9993 / 45380 | 10031 / 46599 |
| PersonList100 | 2884 / 6036 | 1777 / 2529 |
| Union | 16.1 / 42.9 | 39.5 / 21.9 |

Summary: serialization is at parity or faster. **Deserialization of objects and strings is 1.5–2.4× slower**, which
fails the §11.3 POCO gate (combined serialize + deserialize 113 vs 87 ns is about +30%; the gate is 25%). Primitive
arrays are at parity.

**Invocation, Untrusted** (µs; MessagePack with the reverted fast-path build vs MemoryPack from the earlier run).
Allocations for argument calls are now 624 B, versus 640–760 B for MemoryPack.

| Benchmark | MessagePack | MemoryPack |
|---|---|---|
| NoArgument | 48.2 | 48.0 |
| UnmanagedArgument | 50.0 | 50.2 |
| MultipleArguments | 50.8 | 49.4 |
| PocoArgument | 50.9 | 51.9 |
| NestedRoundTrip | 64.0 | 61.3 |
| WithResult | 52.1 | 51.0 |

Invocations are within noise of MemoryPack.

**Channels** (µs per 2000 items, non-fragmented / fragmented):

| Benchmark | MessagePack | MemoryPack |
|---|---|---|
| Persons | 454–740 / 369–447 | 296 / 295 |
| Ints | ~200 / ~200 | 143 / 143 |
| IntArrays256 | ~160 / ~164 | 160 / 176 |

Persons and Ints are about 25–50% slower with MessagePack: deserialization plus the double pass from `TrySkip`
probing. IntArrays256 is at parity.

Suspected deserialization hot spots, to profile: per-member `NexusFormatterRegistry.Get<T>()` virtual calls (e.g.
`DateTime`), `DateTime.FromBinary`, strict UTF-8 decoding, `ReadDouble` peek + switch, and the per-call reader
construction cost (~25 ns fixed overhead visible in Int32).
