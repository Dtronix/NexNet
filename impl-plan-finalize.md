# Implementation plan: finalize the NexNet MessagePack serializer

This plan finishes the work in PR #78 (branch `worktree-messagepack-eval`): it removes MemoryPack completely,
returns the protocol preamble to its original version-1 layout, hardens Native AOT, applies a few cleanups, and brings
every document up to date. It is written for an implementer starting in a fresh context. Read it end to end before
starting; the phases are ordered so that the tree builds and the tests pass after each one.

## 0. How to use this document

**Where to work.** Worktree `Z:\Projects\NexNet\NexNet-master\.claude\worktrees\messagepack-eval`, branch
`worktree-messagepack-eval`, which backs draft PR #78 (https://github.com/Dtronix/NexNet/pull/78). Do not touch the main
checkout at `Z:\Projects\NexNet\NexNet-master`. Start from commit `d3f6cc5` or later.

**Ground rules.**

- Commit and push to `origin/worktree-messagepack-eval` after each phase (WIP commits are fine; the PR is squash-merged
  at the end). Never push to `master`, never merge, never force-push.
- The build and all three test suites must be green before each commit:
  `NexNet.Serialization.Tests`, `NexNet.Generator.Tests`, `NexNet.IntegrationTests`. After Phase 2 there is only one
  build; the `-p:NexNetSerializer=...` switch no longer exists.
- **Do not run the fuzzer** (libFuzzer, `start-24h.ps1`, `--smoke` from the command line). The 24-hour run is done and
  accepted. The `FuzzSmokeTests` unit test inside `NexNet.Serialization.Tests` is part of the normal test suite and
  must keep passing; that is the only fuzz-related execution allowed.
- Don't modify StreamStruct. Don't copy MessagePack-CSharp code.
- Don't build while tests or benchmarks are running (they share `bin`/`obj`).
- Work through shell-safe edits: the worktree's command guard rejects complex one-liners (relative `..` paths, `cd`
  followed by writes, `awk`). Use the Edit/Write tools, or put logic in a script under the session scratchpad.
- Files use CRLF line endings and some have a UTF-8 BOM; preserve both.

**Commands** (run from `src/`):

```
dotnet build -c Release
dotnet test NexNet.Serialization.Tests -c Release --no-build
dotnet test NexNet.Generator.Tests     -c Release --no-build
dotnet test NexNet.IntegrationTests    -c Release --no-build
```

The integration suite takes about 3 minutes, the serialization suite about 50 seconds to 2.5 minutes (it includes the
fuzz smoke replay).

**Baseline before this plan** (both backends green): MessagePack build 249 / 171 / 2566 tests, MemoryPack build
230 / 171 / 2563. Expected counts change in Phase 5; record the new numbers.

## 1. Decisions

Every row was confirmed with the user; the reasoning is included so the implementer can apply it to cases the plan
does not list.

| # | Topic | Decision | Why |
|---|---|---|---|
| D1 | Backward compatibility | **None.** NexNet is pre-1.0; servers and clients update together. | Removes all dual-format code and the payload-format negotiation. |
| D2 | Preamble | Back to master's layout: magic `4E 6E 50 14`, **three reserved zero bytes**, version byte. The payload-format byte is removed. | Only one payload format exists, so there is nothing to negotiate. |
| D3 | Protocol version byte | **Stays `1`.** | User's choice. A 0.16 peer passes the preamble and is then disconnected with `ProtocolError` when its greeting body fails to decode. That is acceptable pre-1.0. |
| D4 | MemoryPack | **Removed everywhere**: library, generator, tests, samples, benchmarks, CI, docs. | No reason to carry two serializers. |
| D5 | Serializer location | **Folded into `src/NexNet/Serialization/`.** The shared project (`.shproj`/`.projitems`) is deleted. Namespace `NexNet.Serialization` and the public API do not change. | Only `NexNet.csproj` consumed the shared project. The generator only emits source text that names these types; it targets `netstandard2.0` and could not compile them anyway. |
| D6 | MessagePack-CSharp package | **Kept as a test-only dependency** (`NexNet.Serialization.Tests`) for interop cross-checks. | Proves NexNet writes standard MessagePack. Shipping packages have no serializer dependency. |
| D7 | TypeHasher tests | **Convert and re-baseline** around `[NexusObject]`/`[NexusKey]`/`[NexusUnion<T>]`. | Keeps coverage of hashing rules; the expected values change. |
| D8 | Protocol spec | **One spec.** `docs/internals/protocol-specification.md` gets the content of `wire-protocol.md` (updated for D2/D3); `wire-protocol.md` is deleted; one TOC entry. | Single source of truth. |
| D9 | User docs | **Full update, no migration guide.** README, `llm-usage.md`, `llm-dev.md`, DocFX articles, XML docs. | Pre-1.0 users upgrade directly. |
| D10 | Working docs at the repo root | **Delete all**: `impl-plan.md`, `impl-plan-finalize.md` (this file, at the very end), `impl-notes.md`, `handoff.md`, `benchmark-results.md`, `messagepack-v4-report.md`, `streamstruct-requirements.md`. | History stays in git and in PR #78. Copy anything the PR description still needs before deleting. |
| D11 | Benchmarks | **MemoryPack baseline removed.** `SerializerBenchmarks` measures NexNet only. | Follows D4; the comparison numbers live in PR #78 and git history. |
| D12 | Native AOT | **Remove the `[DynamicallyAccessedMembers(All)]` annotations**, enable `IsAotCompatible`, and add a CI AOT publish check. | Verified: these annotations cause all three NexNet AOT warnings (see §3.6). |
| D13 | Cleanups in scope | Delete `PipeManagerPool`; dedupe generated formatters per assembly; remove the benchmark-only `FixedWidth` writer mode; speed up the channel fuzz harness (no fuzz runs). | User selected "small cleanups" and the channel harness speedup. |
| D14 | Out of scope | Performance work on `List<T>`/`PersonList100`, a CI libFuzzer job, a migration guide. | Not selected. |
| D15 | Version | **0.17.0** for NexNet, NexNet.Quic, NexNet.Asp and NexNet.Generator. | Breaking wire and API change. |
| D16 | Branch and PR | Same branch, PR #78, **squash-merge** at the end. | WIP history never reaches `master`. |

## 2. Core concepts

Short explanations of the pieces this plan touches. File paths are as they are before Phase 3 moves the serializer.

**Preamble.** Each side writes 8 bytes once, right after the transport connects, and checks the peer's 8 bytes before
reading any message. On master the bytes are the magic tag, three reserved zero bytes, and the protocol version. The
branch currently puts a payload-format ID (1 = MemoryPack, 2 = MessagePack) in byte 4 and uses version 2; D2/D3 revert
both. The session code is `NexusSession.cs` (the `_protocolHeader` constant) and `NexusSession.Receiving.cs`
(`ConfirmProtocol`).

**Framing and protocol messages.** After the preamble the stream is a sequence of messages: a type byte, a little-endian
`uint16` body length for messages with a body, an optional two-byte pipe ID for `DuplexPipeWrite`, then the body.
Bodies are MessagePack arrays with exact element counts, serialized by hand-written code in `NexNet/Messages/*`. None of
this changes.

**Payloads and "embedded values".** User data (method arguments, results, channel items, collection values) is
serialized by formatters. Inside a protocol message a payload is written *embedded*: as exactly one MessagePack value,
inline. With the MemoryPack backend it was wrapped in a `bin` value instead; that branch disappears.
`Internals/PayloadSerializer.cs` holds the helpers.

**Formatters and the registry.** A `NexusFormatter<T>` writes and reads one MessagePack value for `T`.
`NexusFormatterRegistry.Get<T>()` returns the registered formatter from a static generic slot
(`NexusFormatterCache<T>.Formatter`); the first registration for a type wins. Built-in formatters (primitives, strings,
BCL types, collections, primitive arrays) register themselves through `BuiltInFormatters`. Generated formatters register
through `[ModuleInitializer]` methods in the user's assembly.

**Generated formatters and keys.** For every `[NexusObject]` type reachable from a nexus, the generator
(`Serialization/SerializationBuilder.cs`) emits a `file sealed class __NexusFormatter_*` and a registration. Every
serialized member must carry `[NexusKey(n)]` (NEXNET030 otherwise). An object is written as an array of length
`maxKey + 1`; element *n* holds the member with key *n*, and unused keys are nil. Readers read the keys they know,
skip extra trailing elements, and leave missing ones at their defaults. This is how types evolve: add members with new
keys, never reuse a key. There is no implicit (declaration-order) mode.

**TypeHasher and HashLock.** Versioned nexuses compute a structural hash of every method signature and every type
reachable from it (`NexNet.Generator/TypeHasher.cs`). `[NexusVersion(HashLock = ...)]` pins that hash so an accidental
contract change is a compile error. The hasher walks types and builds a canonical text description (the "walk"), which
the generator tests compare against expected strings. It currently has three type modes: `[NexusObject]` (walk members
in key order), `[MemoryPackable]` (walk members in declaration or `[MemoryPackOrder]` order), and "other user type"
(name only, marked `[NotMemoryPackable]`). The MemoryPack mode is removed; every HashLock value in tests changes.

**Channels.** `NexusChannelReader<T>` decodes items from a duplex pipe. Away from the end of the buffer it deserializes
optimistically; near the end it first measures the next item with `MsgPackReader.TryGetNextValueLength`, so a partial
item waits for more data instead of throwing. `NexusChannelWriter<T>` writes items through the formatter. Both still have
a MemoryPack branch selected by a null formatter.

**Native AOT annotations.** `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]` on a generic parameter tells
the trimmer to keep every member of `T`, including nested types and, through nested enums, `System.Enum`'s static
`GetValues(Type)`, which is marked `[RequiresDynamicCode]`. Wherever such a generic is instantiated with a message type
that has a nested enum, the AOT compiler reports IL3050, and calling an annotated method with an unannotated type
parameter reports IL2091. MemoryPack needed these annotations; the registry-based path does not.

**Fuzz project.** `src/NexNet.Fuzz` holds six SharpFuzz harnesses (`reader`, `messages`, `channel`, `formatters`,
`builtins`, `session`), a smoke runner, regression corpora (`Corpus/*.hex`), and `--repro`/`--export-corpus` modes. The
`FuzzSmokeTests` unit test replays the seeds and corpora through every harness.

## 3. Phases

### Phase 1: Preamble back to the version-1 layout

**Goal.** The preamble is exactly master's: `4E 6E 50 14 00 00 00 01`. No payload-format concept remains.

**Library.**

1. Delete `src/NexNet/Internals/PayloadFormat.cs` (the `PayloadFormat` enum and `PayloadFormatInfo`).
2. In `NexusSession.cs`, set `ProtocolVersion` to 1 and rebuild the header without the format byte:

   ```csharp
   private const byte ProtocolVersion = 1;

   // Magic "NnP" + DC4, three reserved bytes (must be zero), protocol version.
   private static readonly ReadOnlyMemory<byte> _protocolHeader =
       new byte[] { (byte)'N', (byte)'n', (byte)'P', 0x14, 0, 0, 0, ProtocolVersion };
   ```

3. In `NexusSession.Receiving.cs`, `ConfirmProtocol` checks the magic (byte comparison, keep it endian-independent),
   then that bytes 4, 5 and 6 are zero, then the version. Remove the payload-format variable and its mismatch branch:

   ```csharp
   if (!header.Slice(0, 4).SequenceEqual(_protocolHeader.Span.Slice(0, 4)))
   { /* not a NexNet stream: ProtocolError */ }

   if (header[4] != 0 || header[5] != 0 || header[6] != 0)
   { /* reserved bytes must be zero: ProtocolError */ }

   if (header[7] != ProtocolVersion)
   { /* unsupported version: ProtocolError */ }
   ```

   Keep the end-of-stream handling added on this branch (a stream that ends before 8 bytes arrive disconnects
   instead of spinning).

**Tests.**

- `IntegrationTests/Security/RawTcpClient.cs`: drop `PayloadFormat`; `ProtocolVersion = 1`; header values
  `[N, n, P, DC4, 0, 0, 0, 1]`. Remove the `payloadFormat` parameter of `SendProtocolHeaderAsync` (keep `badHeader`
  and `badVersion`; `badHeader` may now corrupt any of bytes 0 to 6).
- `ProtocolSecurityTests.cs`:
  - Delete `PayloadFormatMismatch_ShouldDisconnectWithProtocolError`.
  - Replace `ProtocolVersion1Header_ShouldDisconnectWithProtocolError` with
    `UnsupportedProtocolVersion_ShouldDisconnectWithProtocolError` (send version 2 and expect `ProtocolError`).
  - Add `NonZeroReservedByte_ShouldDisconnectWithProtocolError`, with `[TestCase(4)]`, `[TestCase(5)]` and
    `[TestCase(6)]`.
- `NexNet.Fuzz/SessionHarness.cs`: `Server.Preamble` becomes `[0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1]`; delete the
  `PayloadFormatValue` constant and its `#if`. The seed list's malformed-preamble cases stay as they are; recompute any
  that hard-code byte 4 = 2 or byte 7 = 2 so each still tests what its comment says.
- `Corpus/session-regressions.hex`: the inputs with flag byte `0` get the preamble from the harness, so they are
  unaffected. Check every input with flag bit 0 set (it supplies its own preamble) and update its embedded preamble if
  the test intent was a valid preamble.

**Done when** the build is green and all three suites pass.

### Phase 2: Remove the backend switch and MemoryPack from the library

**Goal.** NexNet has no MemoryPack reference and no `NEXNET_MEMORYPACK` code; payloads always go through formatters.

1. **Build switch.** Delete `src/Directory.Build.props` (it only defines `NexNetSerializer`, `NEXNET_MEMORYPACK` and the
   `CompilerVisibleProperty`). Search the repository afterwards for `NexNetSerializer` and `NEXNET_MEMORYPACK`; the only
   remaining hits should be the generator (Phase 4) and docs (Phase 10).
2. **`NexNet.csproj`.** Remove the conditional `MemoryPack` `PackageReference`.
3. **`PayloadSerializer.cs`.** Keep only the MessagePack bodies, without `#if`:

   ```csharp
   internal static class PayloadSerializer
   {
       public static byte[] Serialize<T>(T value)
       {
           var buffer = PooledArrayBufferWriter.Rent();
           try
           {
               var writer = new MsgPackWriter(buffer);
               NexusFormatterRegistry.Get<T>().Serialize(ref writer, value);
               writer.Flush();
               return buffer.WrittenSpan.ToArray();
           }
           finally
           {
               buffer.Return();
           }
       }

       public static T? Deserialize<T>(ReadOnlyMemory<byte> data, NexusSerializerOptions options) { /* reader + formatter */ }

       // Embeds an already serialized value; an empty payload is written as nil.
       public static void WriteEmbedded(ref MsgPackWriter writer, ReadOnlySpan<byte> serializedValue) { /* WriteNil or WriteRaw */ }

       public static Memory<byte> ReadEmbeddedToPooled(ref MsgPackReader reader) { /* ReadRawValue into a rented array */ }
   }
   ```

4. **Invocation messages.**
   - `IInvocationMessage.MaxArgumentSize` becomes the single constant `ushort.MaxValue - 9`. Update its comment: the
     9 bytes are the fixarray header, the uint16 invocation ID, the uint16 method ID and the uint8 flags.
   - `InvocationMessage.DeserializeArguments<T>` and `InvocationResultMessage.TryGetResult<T>` keep only the formatter
     path. Pass the session's options to the `MsgPackReader` in `DeserializeArguments`: today it uses the default
     (Untrusted) options. Do this by storing the options on the message when it is read, the same way
     `InvocationResultMessage` already stores `_options`.
   - Remove the MemoryPack sentences from the class comments.
5. **Channels.**
   - `NexusChannelReader<T>`: delete `ReadMemoryPack`. `_formatter` becomes non-nullable and is always
     `formatter ?? NexusFormatterRegistry.Get<T>()`. `ReadAsync` calls `ReadMessagePack` unconditionally. Rename it to
     `ReadItems` (there is only one format now) and keep its remarks about optimistic reads.
   - `NexusChannelWriter<T>`: same treatment. Delete the MemoryPack branches in `Write` and `WriteEnumerable`, and make
     the formatter non-nullable.
   - Update both class comments.
6. **AOT annotations (D12).** Remove `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]` from:
   - `PayloadSerializer.Serialize<T>` and `PayloadSerializer.Deserialize<T>`;
   - `IInvocationMessage.DeserializeArguments<T>` and `InvocationMessage.DeserializeArguments<T>`;
   - `InvocationResultMessage.TryGetResult<T>`;
   - `MessagePool<T>`, and `PoolManager.Pool<T>` and `PoolManager.Rent<T>`.

   Drop the `System.Diagnostics.CodeAnalysis` usings that become unused. `MessagePool<T>` only needs
   `where T : class, IMessageBase, new()`; `new()` covers the parameterless constructor statically. This was verified on
   2026-10-06: with these annotations removed, a Native AOT publish of `Samples/NexNetSample.Aot.Client` reported zero IL
   warnings (it reported IL2091 and two IL3050 before).
7. **XML docs and comments.** Search `src/NexNet` for `MemoryPack` and fix every remaining comment. Earlier notes
   mention `NexusDuplexPipeExtensions` XML docs that referred to `[MemoryPackable]`; check them.

**Done when** `grep -ri memorypack src/NexNet` finds nothing and the build and tests are green. Before Phase 5, test
projects still reference MemoryPack; that's fine.

### Phase 3: Fold the serializer into NexNet

**Goal.** The serializer sources live at `src/NexNet/Serialization/`, and the shared project is gone.

1. Use `git mv` for every file under `src/NexNet.Serialization/`, keeping the folder structure: `Attributes/`, `Ext/`,
   `Formatters/`, `Internal/` and the root files. They go to `src/NexNet/Serialization/`. Don't move the `.shproj` or
   `.projitems`; delete them.
2. Remove the `<Import Project="..\NexNet.Serialization\NexNet.Serialization.projitems" Label="Shared" />` line from
   `NexNet.csproj`. SDK-style projects pick up the moved files by default.
3. Namespaces stay `NexNet.Serialization` (and `NexNet.Serialization.Formatters`). Don't rename anything public.
4. Check `NexNet.slnx` and any other `.sln`/`.slnx` files for references to the shared project, and remove them.
5. The `InternalsVisibleTo` list in `NexNet.csproj` already covers `NexNet.Serialization.Tests` and `NexNet.Fuzz`.

**Done when** the build and tests are green and `src/NexNet.Serialization` no longer exists.

### Phase 4: Generator

**Goal.** The generator always emits the MessagePack path and knows nothing about MemoryPack.

1. **Backend switch.** In `NexusGenerator.cs`, delete the `SerializerBackend` enum, the `backend` provider that reads
   `build_property.NexNetSerializer`, and every `.Combine(backend)` with its checks. `Generate(...)` loses its `backend`
   parameter. It always reports serialization diagnostics, always treats them (except NEXNET037) as blocking, and always
   appends `data.SerializationCode`.
2. **Emitters.** Remove the `backend` parameter and every non-MessagePack branch:
   - `NexusEmitter.Emit` / `EmitNexus`: always emit the argument readers.
   - `MethodEmitter`:
     - argument prefix is always `__arg`;
     - always emit `EmitArgumentReader`;
     - the result write is always the `NexusSerializer` path, so delete the `global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result)` branch;
     - proxy invocation always uses the pooled `PooledArrayBufferWriter` path, so delete the
       `__proxyInvocationArguments` / `MemoryPackSerializer.Serialize` branch;
     - delete the `message.DeserializeArguments<global::System.ValueTuple<...>>()` server-side branch.
   - `InvocationInterfaceEmitter`: drop the parameter.
   - Keep `CollectionEmitter`'s `DeserializeArguments<ValueTuple<byte>>` call. It is the MessagePack path, backed by the
     built-in `ValueTupleFormatter<byte>` (impl-notes deviation 14).
3. **`ChannelTypeAnalyzer`.** Remove the `build_property.NexNetSerializer` check; NEXNET038 always runs.
4. **`TypeHasher`.** Remove the MemoryPack mode.
   - Delete `IsMemoryPackable`, the `[MemoryPackUnion]` processing, `GetMemoryPackOrder` and the
     `[MemoryPackable]` member walk.
   - Self-reference tracking (`CanSelfReference` and the visited set) applies to `[NexusObject]` types and
     `[NexusObject]` unions only.
   - A user type that is not `[NexusObject]` is hashed by name only, as today. That only happens for types with a user
     formatter (`[NexusFormatter<,>]`) or a built-in formatter. Rename its walk marker from `[NotMemoryPackable]` to
     `[NotNexusObject]`; every hash changes anyway.
   - The `[NexusObject]` walk is unchanged: members in **key order**, each written as
     `<indent><key>: <name>: <type>`, in whatever format `ProcessNexusObject` emits today. Keep it exactly, because the
     converted tests assert it.
   - Unions are walked by tag order.
   - Update the class comment (it still says "reflecting the complete structure of MemoryPack-serialized types").
5. **Formatter dedupe (D13).**
   - **Today.** A formatter is emitted in every nexus file that reaches the type, in a per-type file for each locally
     declared non-generic `[NexusObject]`, and in `NexNet.AssemblySerialization.g.cs`. Registration is first-wins, so
     this is correct, but it duplicates code (for example, `BenchPerson` exists three times).
   - **Target.** Each `[NexusObject]` type gets exactly one formatter per assembly.
   - **Restructure.** Every producer (nexus extraction, local `[NexusObject]` extraction, assembly declarations) returns
     equatable *formatter specs* (`FormatterSpec(string TypeKey, string ClassName, string Code, string Registration)`)
     instead of a finished string. Combine all three providers with `.Collect()` into one output that dedupes by
     `TypeKey` (the fully qualified metadata name of the closed type), orders by key for determinism, and emits a single
     `NexNet.Formatters.g.cs` holding all formatter classes and one `[ModuleInitializer]`.
   - **What stays.** Nexus files keep only the nexus code and the argument readers. Diagnostics stay attached to their
     producers.
   - **Cost.** One combined output re-runs when any formatter spec changes. That's acceptable: specs are equatable
     records, so unchanged inputs still skip work.
   - **Class names.** Use a stable per-type name such as `__NexusFormatter_<sanitized type name>_<hash of TypeKey>`
     instead of the per-file index.
   - **Determinism.** `VersioningTests.HashLockIsDeterministicAcrossMultipleRuns` must still pass. It covers hashes
     only, so also add:
     - a generator test that runs the same compilation twice and asserts byte-identical generated sources;
     - a test asserting that a type reached from two nexuses and also declared locally produces exactly one formatter
       class.

**Done when** the generator tests compile against the new API (Phase 5 converts them) and the generator contains no
`MemoryPack`, `NexNetSerializer` or `SerializerBackend` strings.

Phases 4 and 5 can share one commit if the generator tests don't compile in between.

### Phase 5: Tests

**Generator tests** (`src/NexNet.Generator.Tests`).

1. Delete `SerializerBackendOptions.cs`. Remove the `options:` arguments that pass it, and the tests that exist only
   for the MemoryPack backend: for example, the `GeneratorSerializationTests` case at line ~211 and the
   channel-analyzer test that expects no diagnostics under MemoryPack (~338).
2. `CSharpGeneratorRunner.cs`: remove `using MemoryPack;` and the `typeof(MemoryPackableAttribute).Assembly.Location`
   metadata reference.
3. `NexNet.Generator.Tests.csproj`: remove the `MemoryPack` package reference.
4. **Convert the hashing tests (D7).** `TypeHasherV2Tests` (51 tests, about 135 `[MemoryPackable]` types),
   `TypeHasherTests` (27) and `VersioningTests` (15) use MemoryPack attributes. Convert each test with this mapping and
   keep the test's intent:

   | MemoryPack | NexNet |
   |---|---|
   | `[MemoryPackable] partial class X` | `[NexusObject] class X` (the generator does not need `partial`) |
   | members in declaration order | each serialized member gets `[NexusKey(n)]` in that order: 0, 1, 2… |
   | `[MemoryPackOrder(n)]` | `[NexusKey(n)]` |
   | `[MemoryPackIgnore]` | `[NexusIgnore]` |
   | `[MemoryPackUnion(tag, typeof(T))]` on an interface | `[NexusObject]` plus `[NexusUnion<T>(tag)]` on the interface; each case type is `[NexusObject]` |
   | `[MemoryPackConstructor]` | `[NexusConstructor]` |
   | walk marker ` [MemoryPackable]` | ` [NexusObject]`, with the member lines in the `[NexusObject]` key format |
   | walk marker ` [NotMemoryPackable]` | ` [NotNexusObject]` |
   | `[MemoryPackUnion` walk lines | the `[NexusObject]` union walk format |

   **Recomputing expected walks.**
   - Each `GenerateStructureHashV2(ExpectedWalk = ...)` and `HashLock` value is produced by the generator.
   - Run a converted test once and read the actual value from the failure message.
   - Check the walk by eye: it must describe the type the test intends.
   - Then paste the value in. Don't paste values without checking them.

   **Implicit-order tests.** MemoryPack tests about implicit versus explicit order have no direct counterpart, because
   NexNet keys are always explicit. Turn them into tests of NexNet's rules:
   - reordering member declarations without changing keys leaves the hash unchanged;
   - changing a key, adding a key, or removing a key changes it;
   - a gap in the keys is part of the hash.

   Keep the self-reference, nesting, generics, nullable, array, dictionary, enum and union cases.

**Integration tests** (`src/NexNet.IntegrationTests`).

1. `NexNet.IntegrationTests.csproj`: remove the `MemoryPack` package reference and its comment.
2. `TestSerialization.cs`: remove the `#if NEXNET_MEMORYPACK` branches. Payload helpers always use the registry.
3. `Pipes/ComplexMessage.cs`: drop `[MemoryPackable]` and `using MemoryPack;`. The `[NexusObject]`/`[NexusKey]`
   attributes stay; add them where only MemoryPack attributes existed.
4. `Security/AuthenticationTokenTests.cs`: remove `using MemoryPack;` and replace any MemoryPack use with
   `TestSerialization`.
5. `Pipes/NexusChannelReaderTests.cs`: remove the `#if !NEXNET_MEMORYPACK` around the MessagePack-only tests and the
   comment about the legacy MemoryPack read path. Those tests now always run.
6. `Pipes/NexusChannelReaderWriterTests.cs`: simplify the comment about partial values ("4 of 8 MemoryPack bytes, or an
   int64 missing 5 bytes"); the MessagePack case is the only one.

**Serialization tests** (`src/NexNet.Serialization.Tests`). Remove the `#if !NEXNET_MEMORYPACK` guards in
`GeneratedFormatterTests.cs`, `FuzzSmokeTests.cs` and `ProtocolMessageTests.cs`. The MessagePack-CSharp package stays (D6).

**Fuzz project** (`src/NexNet.Fuzz`). Remove the remaining `#if NEXNET_MEMORYPACK` (Phase 1 handled `SessionHarness`).

**Done when** all three suites are green. Record the new counts. Expect the generator count to drop slightly, since the
MemoryPack-only tests are removed, and the serialization and integration counts to stay the same or rise, since the
`#if`-guarded tests now always run.

### Phase 6: Benchmarks and samples

1. `NexNetBenchmarks.csproj`: remove the `MemoryPack` package reference.
2. `BenchmarkTypes.cs`: remove `[MemoryPackable]`, `[MemoryPackUnion]` and `using MemoryPack;`. Keep the NexNet
   attributes.
3. `SerializerBenchmarks.cs`: remove `MemoryPack_Serialize`, `MemoryPack_Deserialize` and their delegates. Make
   `NexNet_Serialize` the baseline. `WireSizes`/`--sizes` keeps one column. Remove `NexNet_SerializeFixedWidth` (see
   Phase 8).
4. `BenchmarkConfig.cs`: remove the comment that mentions the MemoryPack build. Keep the in-process `SHORT`, `MEDIUM`
   and `INPROC` jobs, because in-process still avoids rebuilding the benchmark project.
5. `Samples/NexNetDemo/NexNetDemo.csproj`: remove MemoryPack. `Samples/NexNetDemo/Samples/Channel/ComplexMessage.cs`:
   convert to `[NexusObject]`/`[NexusKey]`.
6. `Samples/NexNetSample.Aot.Shared/AOT-FINDINGS.md`: rewrite. There is no MemoryPack, so the MemoryPack warnings and
   crashes no longer apply. Record the zero-warning AOT result from Phase 7.

**Done when** `grep -ri memorypack src` finds no code references (docs are Phase 10) and the build is green.

### Phase 7: Native AOT hardening

**Goal.** NexNet stays warning-free under Native AOT, and CI catches regressions.

1. Phase 2 removed the annotations. Re-verify locally with the procedure below.
2. Set `<IsAotCompatible>true</IsAotCompatible>` in `NexNet.csproj` and `NexNet.Quic.csproj`. That enables the trim,
   AOT and single-file analyzers during the library build, so many issues surface at build time. Fix anything it
   reports.
3. For `NexNet.Asp`, try the same. If ASP.NET Core dependencies make it impractical, leave it off and note why in the
   PR description.
4. **CI publish check.**
   - The existing "Validate AOT compatibility" step runs `dotnet build` with `TreatWarningsAsErrors`. That does **not**
     run ILCompiler, which is how the three warnings went unnoticed.
   - Add a step to the `build` job that runs a real Native AOT publish of `src/Samples/NexNetSample.Aot.Client` for
     `linux-x64`. `ubuntu-latest` has `clang` and `zlib`, which ILCompiler needs.
   - Make it fail on any IL warning:

   ```yaml
   - name: Native AOT publish (zero trim/AOT warnings)
     run: |
       dotnet publish src/Samples/NexNetSample.Aot.Client -c Release -r linux-x64 -o ./aot-out \
         -p:TreatWarningsAsErrors=true -p:WarningsAsErrors=IL2026%3BIL2046%3BIL2055%3BIL2057%3BIL2067%3BIL2070%3BIL2072%3BIL2075%3BIL2080%3BIL2087%3BIL2091%3BIL2104%3BIL3050%3BIL3051%3BIL3053 \
         2>&1 | tee aot.log
       ! grep -E "warning IL[0-9]+" aot.log
   ```

   The final `grep` is the dependable part, because some ILCompiler warnings bypass `TreatWarningsAsErrors`. Keep the
   existing build-only step or replace it with this one.

**Local verification.**
- Run `dotnet publish src/Samples/NexNetSample.Aot.Client -c Release -r win-x64` from a **Developer PowerShell for VS**,
  so `vswhere.exe` and the MSVC linker are on PATH.
- In a plain shell, the final native link fails because `vswhere.exe` isn't found. That's harmless: the IL warnings come
  from the ILCompiler analysis, which runs before the link (look for "Generating native code").
- Expect zero `warning IL` lines.

### Phase 8: Small cleanups (D13)

1. **`PipeManagerPool`.**
   - This branch stopped returning pipe managers to the pool, because a reused manager could receive a pipe registration
     from the previous session's in-flight invocation. So the pool now only ever creates.
   - Delete `src/NexNet/Pools/PipeManagerPool.cs`, the `PipeManagerPool` field in `PoolManager` and its `Clear()` call.
   - Create the manager directly in `NexusSession`:

     ```csharp
     PipeManager = new NexusPipeManager();
     PipeManager.Setup(this);
     ```

   - Keep the comment in the disconnect path explaining why managers aren't reused.
2. **Formatter dedupe.** Done in Phase 4, step 5.
3. **`FixedWidth` writer mode.** `MsgPackWriter.FixedWidth` exists only so `SerializerBenchmarks` could measure compact
   versus full-width integers. Remove:
   - the field;
   - every `if (FixedWidth)` branch and the `WriteXxxForced` helpers that only those branches use (keep any helper that
     non-fixed paths also call);
   - the `fixedWidth` parameter of `TestHelpers.Write`;
   - `WriterGoldenTests.FixedWidthModeWritesFullWidth`;
   - the benchmark method.

   The golden tests for the compact encoding stay and must pass unchanged.

### Phase 9: Channel fuzz harness speedup (no fuzz runs)

**Problem.**
- The `channel` harness feeds the input to a `NexusChannelReader<FuzzOrder>` in chunks. After each chunk it calls
  `ReadAsync` with a 5 ms cancellation timeout, because `ReadAsync` waits forever when the buffer ends inside an item.
- On Windows a 5 ms timer fires on the 15.6 ms tick. An input fed one byte at a time therefore costs about
  316 × 15.6 ms ≈ 5 s, and the 24-hour run managed only about 1 input per second.

**Approach.** Give the channel reader an internal, non-blocking way to decode whatever is buffered, and use it in the
harness.

1. Add to `NexusChannelReader<T>`:

   ```csharp
   /// <summary>
   /// Decodes every complete item currently buffered, without waiting for more data. Returns the number of bytes
   /// consumed. For tests and fuzzing.
   /// </summary>
   internal long ReadAvailable<TTo>(List<TTo> list, Converter<T, TTo>? converter)
   {
       if (!Reader.TryRead(out var result) || result.Buffer.Length == 0)
           return 0;

       return ReadItems(result.Buffer, list, converter); // the method ReadAsync uses; it calls AdvanceToExamined itself
   }
   ```

   `NexusPipeReader.TryRead` already exists and returns false when nothing new is buffered.

   **Watch out:** `ReadItems` (formerly `ReadMessagePack`) calls `Reader.AdvanceToExamined(consumed, buffer.Length)`.
   That marks everything examined, so a later `TryRead` returns false until new bytes arrive. That's the behavior the
   harness needs. Keep the `MaxBufferedItemSize` check in that path.

2. In `NexNet.Fuzz/Harnesses.cs`, replace the per-chunk `ReadAsync` + `CancellationTokenSource(5 ms)` with
   `reader.ReadAvailable(list, null)`. After the last chunk, call `pipeReader.CompleteNoNotify()` (it exists) and
   `ReadAvailable` once more, so the completion path is covered. Remove the `OperationCanceledException` allowance from
   `Expect` if nothing else needs it.
3. Add a unit test in `NexNet.IntegrationTests/Pipes/NexusChannelReaderTests.cs` covering `ReadAvailable`:
   - partial data returns 0 items and consumes nothing;
   - the rest of the data returns the item;
   - a second call without new data returns 0.
4. Validate **only** through the existing `FuzzSmokeTests` unit test and the new test. Don't run the fuzzer. The smoke
   test should get noticeably faster, because its channel harness no longer waits on timers. Record the new duration.

### Phase 10: Documentation (D8, D9)

**Protocol spec (D8).**
1. Replace the content of `docs/internals/protocol-specification.md` with the content of
   `docs/internals/wire-protocol.md`, then update it:
   - Title: "NexNet Wire Protocol (version 1)".
   - Preamble: three reserved bytes, version 1. Remove the payload-format byte, the legacy-format notes, and the
     "builds with different serializers are rejected" sentence. Add: a peer with a different version, or non-zero
     reserved bytes, is disconnected with `ProtocolError`.
   - §4: remove "Legacy MemoryPack payload format". Embedded values are always inline MessagePack.
   - §4.3: the 9-byte overhead and the 65,526-byte argument limit stay.
   - Keep §4.8 (typed channel items), §5 (payload encoding), §5.5 (ext 78) and §6 (limits and hardening).
2. Delete `docs/internals/wire-protocol.md`. In `docs/internals/toc.yml`, keep one entry, "Wire Protocol", pointing to
   `protocol-specification.md`.

**User documentation (D9).** Replace every MemoryPack reference with the NexNet serializer. Search the whole repository
(`grep -ril memorypack`).

1. **`README.md`.**
   - Feature list: "Serialization: NexNet MessagePack serializer (no external serializer dependency), generated
     formatters, Native AOT compatible".
   - DTO examples use `[NexusObject]`/`[NexusKey]`.
   - Remove `[MemoryPackable]` and the MemoryPack package mentions.
2. **`llm-usage.md`** (consumer guide). Rewrite the serialization section:
   - the attribute set: `[NexusObject]`, `[NexusKey(n)]`, `[NexusIgnore]`, `[NexusConstructor]`,
     `[NexusUnion<T>(tag)]`;
   - assembly attributes: `[assembly: NexusSerializable<T>]` for types used only at runtime, and
     `[assembly: NexusFormatter<TFormatter, T>]` for third-party types, with a minimal `NexusFormatter<T>` example;
   - the supported BCL types (the table in spec §5.4);
   - `ConfigBase.SerializerOptions` (`Untrusted` by default, `Trusted`);
   - the new diagnostics NEXNET028–034 and 036–038, with one line each;
   - key rules for evolving types (new key for new members, never reuse a key);
   - remove unmanaged channels; `INexusDuplexChannel<T>` plus ext 78 covers primitive arrays.
3. **`llm-dev.md`** (contributor guide).
   - Describe:
     - `src/NexNet/Serialization/` (reader, writer, formatters, registry, ext 78);
     - the generator's serialization builder;
     - hand-written protocol messages;
     - the wire protocol, with a link to the spec;
     - the test projects (`NexNet.Serialization.Tests` and `NexNet.Fuzz`, with `--repro`/`--export-corpus`).
   - Remove the backend-switch instructions.
4. **DocFX articles.**
   - `docs/articles/channels.md`: drop MemoryPack and unmanaged channels; describe items as MessagePack values and
     NEXNET038.
   - `docs/articles/versioning.md`: hashing is based on `[NexusObject]` keys. Changing keys changes `HashLock`; adding a
     member with a new key is the compatible way to extend a type.
   - Add `docs/articles/serialization.md` (attributes, built-in types, custom formatters, options and limits, AOT notes)
     and its entry in the articles TOC.
5. **XML docs.** Any remaining `<see cref>` or prose that mentions MemoryPack.

**Working documents (D10).**
- Before deleting, check whether the PR #78 description links to or quotes any of them; it quotes benchmark numbers
  inline, which is fine.
- Then delete: `impl-plan.md`, `impl-notes.md`, `handoff.md`, `benchmark-results.md`, `messagepack-v4-report.md` and
  `streamstruct-requirements.md`.
- Delete this file, `impl-plan-finalize.md`, as the last commit of Phase 11.

### Phase 11: Version, CI, final verification, PR

1. **Version (D15).** Set `<Version>0.17.0</Version>` in `NexNet.csproj`, `NexNet.Quic.csproj`, `NexNet.Asp.csproj` and
   `NexNet.Generator.csproj`.
2. **CI** (`.github/workflows/dotnet.yml`):
   - delete the `memorypack-backend` job and the comment above it;
   - keep the `build` job's test steps, including "serialization tests (includes fuzz harness smoke run)";
   - the Phase 7 AOT publish step is part of this job.
3. **Final checks.**
   - `grep -ril "memorypack\|NexNetSerializer\|NEXNET_MEMORYPACK\|PayloadFormat\|SerializerBackend" .` (excluding
     `.git`, `bin`, `obj`) returns nothing. The spec's historical mention, if any, is the only acceptable hit; prefer
     none.
   - Build, then all three test suites green; record the counts.
   - Local AOT publish from a VS developer shell (Phase 7): zero `warning IL` lines.
   - Optionally, one quick in-process benchmark sanity run
     (`NEXNET_BENCH_SHORT=1 dotnet bin/Release/net10.0/NexNetBenchmarks.dll --filter *SerializerBenchmarks*`) to confirm
     the benchmarks still run. No performance comparison is needed.
4. **Commit and push.** Then update the PR #78 description with `gh pr edit 78 --body-file <file>`:
   - "What's in it": the backend switch is gone and MemoryPack is removed; the protocol header is back to the version-1
     layout (three reserved bytes, no payload-format byte); AOT is warning-free and enforced in CI; the cleanups.
   - Keep the performance tables: they explain why MemoryPack could be removed. Add one line saying the MemoryPack
     column is the pre-removal measurement.
   - Gate section: unchanged (it passed).
   - Breaking changes: protocol (version byte stays 1, but bodies are incompatible with 0.16, so mixed versions
     disconnect with `ProtocolError`); `[MemoryPackable]` replaced by `[NexusObject]`; unmanaged channels removed;
     HashLock values change; 0.17.0.
   - "Not done yet": remove everything that's now done. List only the out-of-scope items (D14).
   - Leave the PR as a draft. The user decides when to mark it ready and squash-merge it.
5. **Delete this file** in a final commit and push.

## 4. Risks and gotchas

- **The preamble version stays 1 (D3).** A 0.16 client talking to a 0.17 server now passes the preamble. The server then
  fails to decode the client's greeting (MemoryPack bytes) and disconnects with `ProtocolError`. Make sure that path
  really disconnects cleanly rather than throwing. No existing test covers it: the current tests cover a missing,
  duplicate or wrong-direction greeting, and malformed message headers. Add
  `MalformedClientGreetingBody_ShouldDisconnectWithProtocolError` to `ProtocolSecurityTests`:
  - send a valid preamble;
  - send a `ClientGreeting` frame (type 100) whose body isn't a valid greeting array, such as a few MemoryPack-style
    bytes;
  - assert the `ProtocolError` disconnect.

  The existing `InvalidVersionHeader_ShouldDisconnectWithProtocolError` (version 255) stays valid as is.
- **Hash re-baselining** is where mistakes hide. Check every new walk string by eye.
- **Formatter dedupe touches the incremental pipeline.** Keep outputs equatable. Run the determinism test, and confirm
  that editing an unrelated file does not regenerate formatters. The generator tests include incremental-step tracking
  if `CSharpGeneratorRunner` exposes it; otherwise compare outputs from two runs.
- **`IsAotCompatible` may surface new analyzer warnings** in code that was never analyzed. Fix them rather than
  suppressing them. If a suppression is unavoidable, justify it in a comment.
- **Line endings and BOMs.** Several files are CRLF with a BOM; keep them that way.
- **Long test runs.** The integration suite takes about 3 minutes; run it in the background with a generous timeout,
  and don't build in parallel.
- **No fuzz runs.** Repeated from §0 because it's easy to reach for `--smoke`: use the `FuzzSmokeTests` unit test
  instead.

## 5. Reference: what this branch already contains

Phases 0–7 of the original plan are done and merged into this branch: framing and protocol messages, the serializer
core, generated formatters, `TypeHasher` support for `[NexusObject]`, channel probing and optimistic reads, the
unmanaged-channel removal, options and security, collections, benchmarks, and an optimization pass.

Fuzzing found five session bugs, also present on master, and they're fixed here:
- an unknown message type is now disconnected cleanly;
- a stream ending inside the preamble no longer spins;
- pipe managers are no longer reused across sessions;
- the `RegisterPipe`/`CancelAll` race is fixed;
- sends no longer race with disconnect (the output is completed under the write lock).

The §11.3 gate passed: all performance criteria are met, and all six fuzz harnesses ran 24 hours clean
(4.67 billion executions).
