# MessagePack-CSharp v4: PR #2298 Review and Community Reaction

Research date: 2026-10-03. Compiled from two independent research passes: a review of PR #2298 (and the v4 branch at `a9057be2`, 2026-10-01, compared against `master`/v3.1.x), and a survey of GitHub threads and web sources about the v4 controversy. Items marked **[inference]** are analysis rather than something stated in a source.

## 1. Executive summary

v4 is a near-total rewrite of MessagePack-CSharp. It is led by the original author, Yoshifumi Kawai (neuecc), and lives in the same repository under the same `MessagePack` package ID. **It has not been released.** As of 2026-10-03 there is no 4.x package on NuGet; the latest is 3.1.10.

**What carries over from v3:**
- The wire format, which the author claims is "100% payload compatible".
- The `[MessagePackObject]` / `[Key(n)]` annotations.
- The general feel of the high-level `Serialize`/`Deserialize` API.

**What breaks completely:**
- The low-level API: `MessagePackWriter`/`MessagePackReader` are removed.
- The custom formatter interface.
- Resolver composition.
- Security configuration: `MessagePackSecurity` is removed and protections are on by default.
- Union attributes.

**The controversy** is real but limited to a few threads. It peaked in PR #2281 (July 2026):
- Microsoft .NET runtime engineers (EgorBo, Tanner Gooding) found memory-safety bugs in a draft that leaned heavily on `Unsafe`/`MemoryMarshal` and included source-generator code its author labelled unvetted and AI-generated.
- Replies on both sides got heated.
- Co-maintainer Andrew Arnott (AArnott, Microsoft) declined to endorse v4 and suggested shipping it as a separate library. neuecc refused.
- AArnott continues to service v2 and v3, and the apps he owns, including Visual Studio, will not move to v4.

**For NexNet:** v4's *design* actually fits NexNet better than v3 does. It deserializes into existing (pooled) instances, uses generic ref-struct buffers similar to MemoryPack's, is secure by default, and treats AOT as a first-class target. But it is unreleased, its API is still moving, it adds a new dependency (`SerializerFoundation`), and its safety record is under active scrutiny. Adopting v3 now means building against the layer that v4 rewrites.

## 2. Timeline

| Date | Event |
|---|---|
| Oct–Dec 2024 | v3.0 / v3.1 released, with a built-in source generator. |
| May–Jun 2026 | Waves of security fixes. On 2026-06-09, 14 advisories were published at once (3 high: `Skip` recursion, DateTime stack overflow, LZ4 access violation). Patched in v3.1.5–3.1.7 and v2.5.205–2.5.302. |
| 2026-07-14 | AArnott (Discussion #2280): updates "primarily come from me these days… primarily security related." |
| **2026-07-21** | **PR #2281, "v4: 2x–10x performance through assembly-level optimization"** (draft, neuecc). The controversy starts here. |
| 2026-07-22 | AArnott's review: "I have concerns about endorsing this evolution." |
| 2026-07-28 | #2281 closed. A series of "Round" review PRs follows (#2285, #2288, #2290, #2291, #2293, #2294, #2296). Each is closed without merging; work continues on the `v4` branch. |
| **2026-09-14** | **PR #2298, "v4 prepare for preview release"** (draft). "We won't merge into master until the v4 stable release." |
| 2026-09-17 / 09-23 | Two more advisories; v3.1.8–3.1.10 and v2.5.303/305 released. |
| 2026-09-28 | `SerializerFoundation` 1.0.0 (Cysharp) published. It is a new v4 dependency. |
| 2026-10-01 | Last v4 commit ("net9 support, remove msgpack SerializerFoundation"). No NuGet release. |

## 3. PR #2298 at a glance

| | |
|---|---|
| Author | neuecc |
| State | Open, draft; `v4` → `master` |
| Size | 1,273 files, +99,257 / −93,546, 15 commits |
| Docs | The root README is only a title; there is no v3→v4 migration guide yet |
| Outside review | Very little on #2298 itself. After Round 2, neuecc declared anything outside each round's scope off-topic. |

**The author's stated motivation:**
- A 2–10× speedup from devirtualization and inlining, branchless format selection, and a resolver/formatter/factory structure designed to work well with profile-guided optimization (PGO).
- Fixing long-standing design flaws: `Deserialize` not accepting `ReadOnlySpan<byte>`, the mutable static `DefaultOptions`, and the lack of a NativeAOT policy.
- Redoing security properly. In his words, v3 "has many ad-hoc fixes… I cannot proudly say it is safe."

## 4. API differences, v3 → v4

### 4.1 Serializer entry points
- The static `MessagePackSerializer` remains. `options` can no longer be passed as null and sync calls have no `CancellationToken`.
- The `Deserialize(ReadOnlyMemory<byte>)` overload and the `out bytesRead` overloads are **removed**. Use `Deserialize(ReadOnlySpan<byte>)` instead; `byte[]` still binds.
- `Serialize(ref MessagePackWriter, …)` becomes `Serialize<TWriteBuffer, T>(ref TWriteBuffer, T, options)`, where `TWriteBuffer : struct, IWriteBuffer, allows ref struct`. **This form is net9+ only.**
- `Deserialize(ref MessagePackReader, …)` becomes `Deserialize<TReadBuffer, T>(ref TReadBuffer, options)`, with the position available as `buffer.BytesConsumed`.
- **New:** `Deserialize<T>(source, ref T value, options)` fills in an existing instance. The source says: "A non-null value is reused, so pooled objects avoid the result allocation."
- Removed: `DefaultOptions`, the static `Typeless` class, and `MessagePackStreamReader`. The streaming replacements are `DeserializeAsync(PipeReader)`, `DeserializeMessagesAsync` and `DeserializeElementsAsync`.
- `SerializeToJson` becomes `ConvertToJson`; `ConvertFromJson` is new.

### 4.2 Options and resolvers
- `MessagePackSerializerOptions` is now a sealed record with four settings: `Resolver`, `MessageProcessor`, `MaxDepth` (default 500) and `MaxBufferedMessageSize` (default 64 MiB).
- Removed from options: `WithResolver`, `WithCompression`, `Security`/`WithSecurity`, `SequencePool`, `OldSpec`, and the assembly-version settings.
- Presets: `Default` and `DotNetOptimized` (both marked as requiring dynamic code), and **`DefaultAot` / `DotNetOptimizedAot`** (AOT-safe; source-generated and built-in formatters only).
- **`IFormatterResolver`, `CompositeResolver` and all `*Resolver` classes are gone.** Their replacements:
  - `MessagePackFormatterFactory`, chained with `MessagePackFormatterFactory.Combine(...)` (first match wins).
  - `new MessagePackFormatterResolver(factory, hashFloodingResistant: true, validateRequiredMembers: true, …)`.
  - Formatters are cached per resolver instance rather than in static generic caches.

### 4.3 The formatter interface (a complete break)
```csharp
// v3
public interface IMessagePackFormatter<T> {
    void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options);
    T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options);
}

// v4
public interface IMessagePackFormatter<TWriteBuffer, TReadBuffer, T>
    where TWriteBuffer : struct, IWriteBuffer   // + allows ref struct on net9+
    where TReadBuffer  : struct, IReadBuffer    // + allows ref struct on net9+
{
    void Initialize(MessagePackFormatterResolver resolver);
    void Serialize(ref TWriteBuffer buffer, ref SerializeState state, T value);
    void Deserialize(ref TReadBuffer buffer, ref DeserializeState state, ref T value);
}
```
- Reading and writing tokens is now done with C# 14 extension members on the buffer: `WriteInt32`, `WriteString`, `WriteArrayHeader`, `ReadInt32`, `ReadArrayHeader(ref state)`, `TryReadNil`, `Skip`, and so on.
- Formatters must call `state.Enter()`/`Exit()` around nested calls. An analyzer enforces this.
- The buffer types (`BufferWriterWriteBuffer`, `ReadOnlySequenceReadBuffer`, `SpanWriteBuffer`, …) live in the new **`SerializerFoundation`** package.
- `[MessagePackFormatter(typeof(X))]` now points at a factory or an open generic formatter, not at a v3 formatter type.
- A formatter compiled against netstandard2.0 still works on net9+ through a "Compatible" buffer fallback that costs about 4–7% (author's figure).

### 4.4 Attributes
- All attributes now live in the `MessagePack` assembly. `MessagePack.Annotations` 4.x becomes a type-forwarding shim, so referencing only the annotations package is no longer supported.
- `[Union(key, typeof(T))]` becomes **`[UnionTag<T>(key)]`**. The argument order is swapped, and the root type must also be `[MessagePackObject]`.
- New attributes and features:
  - `[MessagePackSerializable<T>]`, which declares AOT root types.
  - `KeyNamingPolicy`.
  - `AllowCircularReferences`.
  - `MessagePackUnknownMembers`.
  - Struct-based surrogates.
  - Support for C# 15 `union` declarations.

### 4.5 Source generator
- The generator is rewritten and now ships *inside* the `MessagePack` package.
- For each assembly it emits an `internal sealed GeneratedMessagePackFormatterFactory`, which registers with the process-global `SourceGeneratedFormatterFactory.Instance` through a `[ModuleInitializer]`.
- Closed generic types reachable from annotated types are harvested automatically for AOT. Other root types need `[MessagePackSerializable<T>]`.
- The generated deserializer fills in an existing instance (`value ?? new T()`).
- Analyzer MsgPack001 ("Public member requires Key or IgnoreMember") is an **error**.

### 4.6 Wire format
- Claimed to be 100% compatible with v3 for the default formatters. 579 v3 tests were ported; all are reported green or skipped with a stated reason.
- Intentional differences:
  - OldSpec writing is removed (reading is still tolerated).
  - Boxed primitives in `object` slots are written forced-width.
  - `ExpandoObject` and `System.Type` support must be opted into.
- Behavior changes:
  - Serializing an untagged union subtype now **throws**; v3 wrote nil.
  - `ValidateRequiredMembers` defaults to true, so a payload missing required or constructor members throws.
- LZ4 moves to a separate package (`MessagePack.LZ4`) that depends on a **native** binding (`NativeCompressions.LZ4`). Zstandard support is new.

### 4.7 AOT and target frameworks
- `IsAotCompatible` is set, and the repo has AOT publish test projects.
- Runtime IL emit is gone entirely. Contractless and dynamic types use a reflection tier that only runs under JIT.
- Targets: `netstandard2.0; netstandard2.1; net9.0; net10.0; net11.0`. The explicit net472 and net8.0 targets are dropped.
- Building the repo needs the preview language version and the .NET 11 RC SDK.

### 4.8 Security
- **`MessagePackSecurity` (including `UntrustedData`) is removed.** These protections are on by default instead:
  - hash-flooding-resistant comparers (SipHash);
  - `MaxDepth` of 500;
  - a per-message element budget that rejects array/map headers claiming more elements than the payload can hold, before anything is allocated;
  - `MaxBufferedMessageSize`;
  - an LZ4 decompression-bomb guard.
- Typeless `LoadAnyType` and the `System.Type` formatter are `[Obsolete]`.
- Use of unsafe code has moved rather than shrunk. Rough grep counts:

  | Pattern | v3 | v4 |
  |---|---|---|
  | `unsafe` keyword | 121 | 7 |
  | `Unsafe.*` calls | ~275 | ~553 |
  | `MemoryMarshal.*` calls | ~76 | ~146 |
- Fuzzing (SharpFuzz) and property-based tests (CsCheck) were added.

### 4.9 Packaging
- **Added dependency:** `SerializerFoundation`.
- **Removed packages:** `MessagePack.Analyzers` and `MessagePack.SourceGenerator` (folded into core), `ImmutableCollection` (folded into core), `ReactiveProperty`, `Experimental` (UnsafeBlit, HardwareIntrinsics), and the in-repo Unity client.
- **New packages:** `MessagePack.LZ4`, `MessagePack.Zstandard`, `MessagePack.SignalR` (net10), `MessagePack.Unity` (UPM).
- Assemblies keep the same strong-name key.

### 4.10 Migration burden
- **Annotation-only users calling `Serialize`/`Deserialize`:** mostly a recompile, plus fixes for `ReadOnlyMemory` overloads, null options, `DefaultOptions`, `WithResolver`/`WithSecurity`/`WithCompression`, and unions.
- **Custom formatters, resolvers, or `MessagePackWriter`/`MessagePackReader`:** a **full rewrite** of that code.
- **Binary compatibility:** v3-built DLLs that only carry annotations still load. v3-compiled formatters do not.

## 5. The controversy

### 5.1 Who is involved

**neuecc (Yoshifumi Kawai, Cysharp)**: original author and org owner; drives v4.
- On v3: "since v3 does not include much of my own work, I felt it is hard for me to have a strong interest in it."
- He considered freezing v3 and splitting the project, but felt "this library itself would have no future" that way.
- On his relationship with AArnott: "this library ran on two wheels, my forceful, frontier attitude and his stability. That balance broke when he released his new library."

**AArnott (Andrew Arnott, Microsoft)**: co-maintainer and author of Nerdbank.MessagePack.
- "The complexity… is *much* higher now… I have concerns about endorsing this evolution."
- He suggested shipping v4 as a separate library ("UltraMessagePack"). neuecc refused: "I want to finish this as v4."
- He accepted ("MessagePack-Sharp is your baby") on the condition that v2 and v3 can keep being serviced. Since then he has given constructive advice on AOT and target frameworks.

**EgorBo and Tanner Gooding (.NET runtime team, Microsoft)**:
- EgorBo posted a memory-corruption bug in the draft's generated code, found with an AI-assisted review.
- Tanner: "you have a PR that would introduce real security bugs/holes."
- EgorBo later added: "opinions are solely my own, not on behalf of MSFT… there has been a massive overreaction to my comments."

**Community**: split.
- Wraith2: neuecc's replies were "rude, dismissive and aggressive."
- nuskey8 defended the push for performance; that comment got the most reactions in the thread.
- rampaa argued that 1–3% speed differences matter.

### 5.2 Main complaints
1. **Unsafe code and memory safety.** Specific findings:
   - an unchecked write that could run past the buffer in the generated code;
   - a string that is created and then mutated in place;
   - a struct that holds an `ArrayPool` array, risking a double return.

   Counter-evidence: a safe rewrite of a hot path was only 0.9–2.7% slower on .NET 10, and removing `AggressiveInlining` changed little.
2. **AI-generated code** in the draft. neuecc says the core was written or checked by hand, and the AI parts will be replaced after line-by-line review. Whether that review has been completed is unverified.
3. **Breaking changes splitting the ecosystem.** Large apps such as Visual Studio cannot load two major versions side by side.
4. **Governance and tone.** The co-maintainer has stepped back from v4, and the author argued openly with Microsoft staff. neuecc later apologised to AArnott.
5. **Unity and older runtimes** were given as the justification for unsafe code, since JIT optimizations can't be relied on under IL2CPP.

### 5.3 Where it stands
- There has been no fork and no package rename. v4 continues on the `v4` branch with low-visibility review rounds.
- v2 and v3 are actively patched for security, most recently on 2026-09-23. SECURITY.md lists 3.x EOL as "not yet determined" and 2.x support as running to "2026-12-31 (at least)".
- Alternative: **Nerdbank.MessagePack** (AArnott, built on PolyType). Version 1.3.88 is stable. It is not a drop-in replacement: types are marked with `[GenerateShape]` instead of `[MessagePackObject]`, and it has no formatter/resolver model. The author's benchmarks show it 2–3× slower than v4.
- **Twitter/X could not be verified.** Only search snippets of neuecc's own announcement tweets were found. No Reddit or Hacker News threads were found. The visible argument is on GitHub.

## 6. Implications for NexNet

| NexNet usage | v3 | v4 | Assessment |
|---|---|---|---|
| Channels reading from and writing to pipes | `MessagePackWriter(IBufferWriter)`, `MessagePackReader(ReadOnlySequence)` | Removed. Replaced by `BufferWriterWriteBuffer` / `ReadOnlySequenceReadBuffer` with the generic `Serialize`/`Deserialize` overloads and `BytesConsumed`. | Full rewrite between v3 and v4. v4's buffer model is closer to MemoryPack's. |
| Pooled protocol messages deserialized into existing instances | Not supported; needs hand-written code. | **Native** via `Deserialize(src, ref value, opts)`. [inference] Members missing from the payload probably keep their old values, so pooled objects must be reset. | Strong v4 advantage. Hand-writing these messages, as already planned, removes the dependency either way. |
| Method arguments as `ValueTuple` | Built-in generic formatter. | Same wire format, but **not served by `DefaultAot`** unless registered. | Main AOT design risk; see below. |
| Interaction between NexNet's generator and MessagePack's | Neither generator can see the other's output. | Same. v4 harvests closed generics only from declarations its own generator sees. | **Prototype first.** NexNet's generator would need to emit its own `MessagePackFormatterFactory` (or inline array writes) and put it first in the chain. |
| Union collection messages | `[Union(k, typeof(T))]` | `[UnionTag<T>(k)]` with the root also `[MessagePackObject]`; same wire format. | Mechanical change. |
| Security for untrusted data | `UntrustedData` must be opted into explicitly (default is `TrustedData`). | Type removed; protections on by default. | On v3, every inbound path must use `UntrustedData`. |
| `[MessagePackObject]` + `[Key(n)]` user contract | Supported | Supported (Annotations becomes a shim) | **Carries over.** The safe user-facing contract. |
| AOT on .NET 10 | Partial | First-class (`DefaultAot`, AOT tests) | v4 is much better, once the generator interaction is solved. |
| Dependencies | Self-contained | Adds `SerializerFoundation`; LZ4 becomes native | Fine if NexNet doesn't use compression. |

**Related finding about the current serializer:** [MemoryPack #381](https://github.com/Cysharp/MemoryPack/issues/381) (raised by EgorBo). MemoryPack's raw-memory copy of unmanaged structs can carry padding bytes and booleans that are neither 0 nor 1. This applies to NexNet's current unmanaged channels and to unmanaged argument types when receiving data from untrusted peers.

## 7. Options

1. **Wait for the v4 preview** and design directly against it. Meanwhile, do the serializer-independent work:
   - hand-write the protocol messages;
   - make the byte order explicit with `BinaryPrimitives`;
   - add an internal serializer seam;
   - move unmanaged channels to an explicit raw path.
2. **Prototype on v3 behind a thin NexNet-internal adapter.** No MessagePack writer, reader, formatter or resolver type would leak outside the seam, and `[MessagePackObject]`/`[Key]` would be the user contract. The later v4 swap then stays inside NexNet. The cost is writing pooling and argument code that v4 makes unnecessary.
3. **Evaluate Nerdbank.MessagePack.** It has the more conservative, safety-focused maintainer, but a different programming model and lower speed.
4. **Stay on MemoryPack** with the earlier fixes (explicit byte order, a startup check that rejects big-endian machines, documented unmanaged-type limits) plus mitigations for #381.

## 8. Open questions and unverified items
- v4 release date. The API is explicitly not frozen; package validation is disabled until release.
- Final API names. There is no README or migration guide yet.
- All performance figures, the 579-test compatibility claim and the 4–7% compatibility-tier cost come from the author. Wraith2 partially reproduced the speed ordering.
- How an instance that is reused for deserialization behaves when the payload omits some members (inferred from generator code, not tested).
- Whether per-assembly generated factories will stay `internal`, and whether explicit composition will be supported instead of the global registry.
- How MessagePack's generator and NexNet's generator will interact for AOT root registration.
- Whether the AI-assisted code has been reviewed line by line as promised.
- v3 support window ("not yet determined").
- The Twitter/X discussion itself.

## 9. Sources
- PRs: [#2281](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2281), [#2285](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2285), [#2288](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2288), [#2290](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2290), [#2291](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2291), [#2293](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2293), [#2294](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2294), [#2296](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2296), [#2298](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2298)
- Key comments in #2281: [EgorBo bug report](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2281#discussion_r3626319083), [EgorBo unsafe critique](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2281#issuecomment-5056971750), [AArnott review](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2281#pullrequestreview-4758127981), [fork vs. v4 thread](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2281#discussion_r3638517177)
- [Discussion #2280](https://github.com/MessagePack-CSharp/MessagePack-CSharp/discussions/2280), [Releases](https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases), [Security advisories](https://github.com/MessagePack-CSharp/MessagePack-CSharp/security/advisories), [v4 branch](https://github.com/MessagePack-CSharp/MessagePack-CSharp/tree/v4)
- [SerializerFoundation](https://github.com/Cysharp/SerializerFoundation), [Nerdbank.MessagePack](https://github.com/AArnott/Nerdbank.MessagePack), [MemoryPack #381](https://github.com/Cysharp/MemoryPack/issues/381)
- [neuecc: BenchmarkDotNet in the AI era](https://neuecc.medium.com/how-to-write-the-fastest-code-with-benchmarkdotnet-in-c-in-the-ai-era-6a585e634488), [HeroDevs on the June 2026 CVE batch](https://www.herodevs.com/blog-posts/messagepack-csharp-patches-12-cves-affecting-net-6-and-signalr), [Microsoft unsafe-code best practices](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices)
- X/Twitter (search snippets only): [neuecc Round 7 tweet](https://x.com/neuecc/status/2093307451840114813)
