# Java & JavaScript clients: feasibility investigation

Status: **investigation / proposal**. No code has been changed. Written 2026-11-17 against `master` @ `ec02d2b` (0.20.0).

This document consolidates five independent investigations:

1. Generator & code emission
2. Client runtime, protocol & feature parity
3. Cross-language test infrastructure
4. Multi-transport server
5. NUnit → TUnit migration

Each item ends with a **Recommendation**. Claims marked ✅ were re-verified against the source after the investigations.

---

## 0. Executive summary

**Verdict: feasible.** No hard blockers were found. The three key enablers already exist:

- **The generator already knows everything a non-C# client needs.** A server nexus's `NexusGenerationData` contains both interfaces, final method IDs, version tables and every hash.
- **There is a written wire protocol** (`docs/internals/protocol-specification.md`).
- **The serializer is NexNet's own MessagePack dialect**, with no reflection, so it can be emitted for other languages.

The main risks are scope and parity at the platform edges:

- **The client runtime is not a thin stub.** A client nexus also *executes* server→client invocations. It needs two execution pools, both pipe roles (the server can open pipes to the client), and synchronized collections. Size it at ~70% of a full session.
- **The protocol spec is not clean-room ready.** It has 16 gaps (G1–G16). The biggest are the collection sync semantics and the HTTP/WebSocket transport bindings.
- **Browsers cannot match the TLS layer.** They have no HttpSocket, no certificate pinning, no custom CA, no programmatic client certificates, no server-certificate access, and no `Authorization` header on a WebSocket. Parity has to be defined at the **NexNet layer** (greeting token), with the TLS-layer exceptions documented.
- **Test parity is the largest single cost.** About 380 client-facing test methods have to run against every language.

**Rough effort** (engineer-time, sequential estimates; parallelizable across people):

| Workstream | Estimate |
|---|---|
| Spec gaps + C# semantic fixes + conformance kit (shared, once) | ~1 month |
| TUnit migration | 2–3 weeks |
| Multi-transport server | ~2 weeks |
| Generator: IR + bundle + Java/JS emitters + validation | 8–12 weeks |
| JS runtime | 4–6 months |
| Java runtime | 5–7 months |
| Cross-language harness + porting ~380 test methods | 5–7 months |

**Recommended order** (detail in §9):

1. Spec and C# fixes, the test certificate, and the TUnit migration.
2. Multi-transport server, and the generator IR plus the bundle/unpack mechanism.
3. Golden vectors and the harness skeleton, using a Node host as the reference implementation.
4. JS runtime and JS emitters.
5. Java runtime and Java emitters.
6. Port the long tail of tests.
7. Later: Rust and C++ via the same IR and harness.

---

## 1. Decisions already made

| Topic | Decision |
|---|---|
| Scope | **Client-only** Java and JS libraries talking to a .NET NexNet server |
| JS targets | Browser + Node, modern ESM, no TypeScript source, but ships `.d.ts`/JSDoc for full TS typing |
| Java baseline | Java 11 / Android API 24+, `CompletableFuture`-based, Kotlin-friendly |
| Transports | **HttpSocket over TLS (preferred) + WSS** on Java/Node; **WSS only** in browsers (they cannot do the HttpSocket upgrade) |
| Plaintext | Allowed, **with a warning** |
| Parity | **Paramount.** Nexuses **opt in** to targets; awkward types get **shims**; anything that still can't be represented identically in every opted-in target is a **generator error** |
| Codegen | The existing C# generator also emits Java/JS proxies, client nexus bases and MessagePack formatters; hand-written static runtime per language |
| Output dirs | **MSBuild properties per language**, plus a generic item for future languages |
| Future languages | Design for Rust / C++ |
| Multi-transport server | **One `NexusServer`, many listeners**, sharing registry, groups, collections, auth and rate limiting |
| Test parity | Server-only tests stay C#. **All client tests run against every client language** |
| Test authoring | **C#-driven tests + thin language hosts**, on **TUnit** (migrate first) |
| Language hosts | **One long-lived host per language per run**, many isolated clients inside |
| Host control channel | **Independent minimal protocol** (length-prefixed JSON), no NexNet code in the path |
| Client callbacks in tests | **Generated scriptable client nexus** per language + a few named handlers |
| Host failures | **Leak check after every test + auto-restart** of a crashed/hung host |
| Report location | This file |

---

## 2. Cross-report conflicts and how they are resolved

| Conflict | Resolution |
|---|---|
| How Java/JS files reach disk. The generator report proposes a *comment-only bundle in a generated .cs file + MSBuild unpack target*. The test report proposes a *`NexNet.Codegen` CLI*. | **Bundle + unpack target** (§3.2). It keeps one front end (the generator), keeps live IDE diagnostics, and avoids compiling twice. The test hosts consume the same output. A CLI can come later as a convenience wrapper. |
| Java `DateTime`/`DateTimeOffset`/`TimeSpan`. Generator report: `java.time` + require Android desugaring. Runtime report: runtime value types (`NexusDateTime`, `NexusTimeSpan`) with `java.time` converters. | **Runtime value types with converters.** Lossless (ticks + kind), API 24 with no desugaring requirement, and identical in shape to the JS types. |
| `[NexusFormatter]` user formatters. Generator report: error. Runtime report: emit an interface stub the user implements. | **Error now** (`NEXNET040`), which matches "error on gaps". **Later escape hatch:** `[NexusTargetFormatter<T>(Target, "fqcn")]`, so the user supplies a native formatter per target explicitly. |
| Control channel carrier. Decision: "stdio or loopback". | **Loopback TCP** dialed by the host, with a random token. stdio is unsafe because libraries write to stdout. The browser connects through a WebSocket↔TCP bridge. |
| Java `byte` (unsigned in C#). Generator report: `short`. Runtime report: `int` or `short`. | **`int`**, range-checked on write. Java widens byte/short arithmetic to `int` anyway. |

---

## 3. Generator & code emission

### 3.1 Is the model language-neutral enough?

**What is already clean:**

- The pipeline `NexusDataExtractor.Extract` → `NexusGenerationData` → `NexusValidator` → `NexusEmitter` is cached, Roslyn-symbol-free, and mostly language-neutral.
- IDs, hashes, versions, collection mode, authorization flags, pipe/cancellation indices and `SeparatePool` are plain data.
- ID assignment and hashing depend only on the interface symbol. So the server's `ProxyInterface` equals what any client would compute.

**Where C# leaks in:**

- `MethodData.ReturnType` and `MethodParameterData.Type`/`SerializedType`/`ChannelType` are C# type strings.
- `MethodParameterData.SerializedValue` is a **C# expression**.
- There are no structured type references anywhere in the models.

**What `Serialization/` looks like:**

- `TypeShape.cs` (`ObjectShape`, `UnionShape`, `EnumShape`, …) is the single definition of the wire shape, and `ShapeHasher` reads the same graph. That makes it the right basis for an IR.
- `SerializationBuilder.cs` emits C# formatter *text* during the transform phase.
- NEXNET029–034/037 are raised inside that C# emission.

**Recommendation:** Add a symbol-free, equatable **IR layer** (`src/NexNet.Generator/Ir/`), projected from `TypeShape` and `InvocationInterfaceData`. Because the wire shape, the hash and every target's code then derive from one graph, they cannot drift.

- Leave C# emission byte-for-byte unchanged in phase 1.
- Add enum member names to `ShapeBuilder.CreateEnum`; they stay out of the hash.
- Precompute the handshake values (version string, both nexus hashes, accepted incoming ID set) in the IR, so no emitter re-derives them.

Sketch:

```
NexusIr     { IrVersion, Exports[], Types[] }
ExportIr    { Name, Targets, ServerApi, ClientApi, Handshake{Version?, ServerNexusHash, ClientNexusHash}, ClientAcceptedIds[] }
InterfaceIr { ClrName, Hash, Methods[], Collections[], Versions[] }
MethodIr    { Name, Id, Hash, Return(Void|Task|TaskOf T), Params[], HasCancellation, Pipe(None|DuplexPipe|Channel T), SeparatePool, IsAuthorized }
TypeRef     = Prim | Bin | PrimArray(ext78) | Seq | Set | Map | Tuple | Nullable | Decl | TypeParam | External
TypeDeclIr  = ObjectDecl{Members{Name,Key,Type,Nullable}, IsStruct, IsRecord, ArrayLength, IsRecursive} | UnionDecl{Cases{Tag,..}} | EnumDecl{Underlying, IsFlags, Members}
```

### 3.2 Getting non-C# files onto disk (the big gotcha)

A Roslyn source generator can only `AddSource` C# into the compilation. File IO is banned by RS1035, which is a **build error** in this repo because of `EnforceExtendedAnalyzerRules` + `TreatWarningsAsErrors`.

| Option | Verdict |
|---|---|
| A. Generator writes files directly | ✗ RS1035, races, runs on every IDE keystroke |
| B. Java/JS hidden behind `#if false` | ✗ breaks on JS lines starting with `#` (private fields) |
| C. MSBuild task hosting Roslyn / `CSharpGeneratorDriver` | Viable but heavy: compiles twice, Roslyn/ALC conflicts in VS's .NET Framework MSBuild |
| **D. Comment-only bundle + unpack target** | **Recommended** |
| E. IR embedded as `AssemblyMetadata`, emitters in an MSBuild task | Fallback; ships contract metadata in production binaries |
| F. `dotnet nexnet export` (MSBuildWorkspace) | Optional later convenience |

**How option D works:**

1. When targets are enabled, the generator adds `NexNet.Targets.g.cs`, made only of `//` comments: per file a `// @file <relpath> sha256=…` header followed by base64 lines. It also carries `nexnet-ir.json`.
2. A `buildTransitive` target sets `EmitCompilerGeneratedFiles=true`, only when `@(NexNetTarget)` is non-empty.
3. After `CoreCompile`, the target unpacks the bundle into each language's output dir:
   - it writes only changed files and deletes stale ones via a manifest;
   - it hooks into `Clean`;
   - it is incremental (Inputs = bundle, Outputs = stamp);
   - it can be an inline `RoslynCodeTaskFactory` task, like the repo's existing `StripHtmlImages`.
4. IDE diagnostics stay live. Only real builds write files, and the produced assembly gains nothing.

**Configuration (MSBuild, per the decision):**

```xml
<PropertyGroup>
  <NexNetJavaOutputDir>../android/app/src/main/java</NexNetJavaOutputDir>
  <NexNetJavaPackage>com.acme.chat</NexNetJavaPackage>
  <NexNetJavaScriptOutputDir>../web/src/generated</NexNetJavaScriptOutputDir>
</PropertyGroup>
<!-- generic form; the shorthand properties above are converted into these items -->
<ItemGroup>
  <NexNetTarget Include="rust" OutputDir="../rust/src/gen" Crate="acme_chat" />
</ItemGroup>
```

A target running before `GenerateMSBuildEditorConfigFileCore` flattens the items into `CompilerVisibleProperty` values (`NexNetTargets=java;javascript`, `NexNetTarget_java_Package`, …). Only options that change the generated text need to be compiler-visible; `OutputDir` is used only by the unpack target.

**Gotchas:**

- Multi-TFM projects must unpack from exactly one TFM.
- Output must be deterministic: ordinal merge order, no timestamps or absolute paths.
- `EmitCompilerGeneratedFiles` also dumps other generators' output to `obj/`. This is harmless.
- Possibly skip building the bundle during `DesignTimeBuild`, while still validating. Verify in VS and Rider.
- The generator nupkg needs a new `buildTransitive/` folder.

**Recommendation:** Use option D, with E as the fallback. The generator stays the single front end.

### 3.3 Opt-in and diagnostics

There are two ways to opt in:

```csharp
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server,
       Targets = NexusTargets.Java | NexusTargets.JavaScript)]   // [Flags], room for Rust/Cpp
public partial class ServerNexus { }

// contracts-only projects, older versions (e.g. a V1 client of a V2 server), multiple exports
[assembly: NexusClientExport<IServerNexusV1, IClientNexus>(Targets = NexusTargets.Java, Name = "ChatV1")]
```

A new IR-based `Validation/TargetValidator.cs` runs in the output phase, with real source locations.

| ID | Sev | Condition |
|---|---|---|
| NEXNET040 | Error | Type not representable identically in target X (e.g. a user `[NexusFormatter]` type) |
| NEXNET041 | Error | Name collision after mapping (camelCase, `_` stripping, keywords, getters) |
| NEXNET042 | Error | Duplicate/overloaded method names across exported interfaces |
| NEXNET043 | Error | `Targets` set on a Client nexus |
| NEXNET044 | Warning | Target opted in but no output dir configured |
| NEXNET045 | Error | Unknown target / missing required option (e.g. Java package) |
| NEXNET046 | Warning | Set/dictionary key type compares by reference in the target (JS `Map`) |
| NEXNET047 | Error | Two exports produce the same output file |

**Existing bugs found here:**

- ✅ Duplicate method names already **crash** the generator: `allMethods.ToDictionary(m => m.Name, …)`, `NexusDataExtractor.cs:926`. NEXNET042 fixes this for C# too.
- Serialization diagnostics are reported with `Location.None`. Fix that at the same time.

**Recommendation:** Support both opt-in forms, use the IR-based validator, and add NEXNET040–047.

### 3.4 Hashes and versioning

All method, collection, interface and version hashes are computed in C#: FNV-1a over shape hashes, with XxHash32 of method names. The results are stored as fixed ints. The handshake sends the version string plus both nexus hashes, and incoming calls are gated by `(hash << 16 | id)`.

**Recommendation:** Java and JS embed **constants only**, so FNV/XxHash never need porting. Add a test that compiles a C# client for the same interface pair and asserts its `MethodHash` and `VersionHashTable` equal the emitted constants.

### 3.5 Type mapping (MessagePack formatter emission)

General rules:

- **The declared type decides the wire kind**, never the runtime value. The writer still picks the smallest canonical encoding.
- **Objects** are arrays of length `ArrayLength`, with nil in the gaps. Readers skip extra trailing elements.
- **Narrow integers are range-checked on write.** JS also rejects non-integral numbers.
- **Struct-typed values are never null on the wire**: throw on read and on write.

| .NET | Java 11 / API 24 | JS | Decision |
|---|---|---|---|
| bool, short, int, float, double, string | native | `boolean`/`number`/`string` | Direct |
| sbyte / byte / ushort / uint | `byte` / `int` / `int` / `long` (checked) | `number` (checked) | Direct |
| long | `long` | **`bigint` always** (opt-in checked `number` mapping) | Direct / Shim |
| ulong | `long` raw bits + unsigned helpers | `bigint` (checked) | Shim (Java) |
| char | `char` | 1-char string | Direct / Shim |
| Half | `float` (manual f16 conversion) | `number` (`Math.f16round` or fallback) | Shim |
| decimal | `BigDecimal` (throws beyond 96 bits / scale 28) | runtime `NexusDecimal` | Shim |
| DateTime / DateTimeOffset / TimeSpan / DateOnly / TimeOnly | runtime `NexusDateTime` etc. (ticks + kind) with `java.time` converters | runtime `NexusDateTime` etc. (`bigint` ticks) | Shim |
| Guid | `UUID` (byte order matches) | lowercase string | Direct / Shim |
| BigInteger | `BigInteger` (reverse byte order) | `bigint` | Direct |
| Uri / Version | `String` / runtime `NexusVersion` | string / runtime `Version` | Shim |
| byte[] / Memory\<byte> / … | `byte[]` | `Uint8Array` | Direct |
| Ext 78 primitive arrays | Java primitive arrays (`char[]` for ushort, raw bits for unsigned) | typed arrays (`Int32Array`, `BigInt64Array`, …); copy for alignment | Direct |
| T[] / List / IList… | `List<T>` | `T[]` | Direct |
| HashSet / Dictionary | `LinkedHashSet` / `LinkedHashMap` | `Set` / **`Map`** (never a plain object) | Direct (+ NEXNET046) |
| Stack / Queue | `ArrayDeque` | `T[]` (stack: index 0 = top) | Shim (documented order) |
| ValueTuple / Tuple / KeyValuePair | runtime `Tuple1..7` / `Map.Entry` | tuple arrays | Shim |
| Nullable\<T> | boxed + JSpecify `@Nullable` | `T \| null` | Direct |
| enum | **generated open value class** (round-trips unknown and `[Flags]` values) | frozen constants object + literal union in `.d.ts` | Shim |
| `[NexusObject]` class / struct / record | POJO / final class with value `equals`/`hashCode` | class + `.d.ts` | Direct / Shim (copy semantics lost) |
| Union | abstract base; write dispatches by `instanceof` in inheritance-depth order; read switches on tag | base class + TS union type | Direct |
| Generic `[NexusObject]` | `Box<T>` + `BoxFormatter.of(Formatter<T>)` (erasure) | same factory pattern | Direct |
| `[NexusFormatter]` user types | — | — | **Error NEXNET040** |
| Pipe / Channel\<T> / CancellationToken / ValueTask | runtime pipe & channel / runtime `CancellationToken` / `CompletableFuture` | Web Streams / `AsyncIterable` / `AbortSignal` / `Promise` | Direct |
| INexusList\<T> | runtime `NexusList<T>` | runtime `NexusList<T>` | Direct |

**Gotchas:**

- **Defaults for members missing from an older peer differ.** C# gets the field initializer; Java and JS get null or undefined. Either extract literal initializers into the IR or document the difference.
- **Java and JS cannot look up a formatter from `T` at runtime.** Generated code must expose formatter constants or factories, and channel APIs take them explicitly. `[assembly: NexusSerializable<T>]` stays the way to register types used only at runtime.
- **Generated equality is required.** `NexusList.RemoveAsync(item)` uses `EqualityComparer<T>.Default`, so Java needs generated `equals`/`hashCode` and JS needs a generated structural equality.

**Recommendation:** Make this table the normative mapping, encoded as per-target capability tables. These tables drive NEXNET040.

### 3.6 Extensibility (Rust, C++)

```csharp
internal interface ITargetLanguage {
  string Id { get; }  NexusTargets Flag { get; }
  TargetOptions ParseOptions(AnalyzerConfigOptions o);
  ITypeMapper Types { get; }   // TypeRef -> mapping | Unsupported  (drives NEXNET040)
  INameMapper Names { get; }   // drives NEXNET041
  IEnumerable<TargetDiagnostic> Validate(NexusIr ir, ExportIr e, TargetOptions o);
  IEnumerable<GeneratedFile> Emit(NexusIr ir, TargetOptions o);
}
```

- Each language lives in `Targets/<Lang>/` (proxy, client nexus, formatter and test-scaffolding emitters) with a shared `CodeWriter`.
- **Rust and C++** need fewer shims because they have exact unsigned and 64-bit integer types. They do need an `IsRecursive` flag in the IR (for `Box`/`unique_ptr`), and unions map to `enum`/`std::variant`.
- A versioned **`nexnet-ir.json`** in the bundle is the out-of-tree contract.

**Recommendation:** Internal `ITargetLanguage` with a registry; `nexnet-ir.json` as the external contract; test scaffolding as an emitter option (`EmitTestScaffolding`).

### 3.7 Effort

| Piece | Size |
|---|---|
| IR + merge + JSON | L |
| Opt-in surface + `TargetValidator` + location fix | M + M |
| Bundle + buildTransitive + unpack | M |
| Java proxies/base + formatters | L + L |
| JS proxies/base (ESM + `.d.ts`) + formatters | L + L |
| Test scaffolding | M per language |
| Golden + `javac --release 11` + `node` + `tsc --noEmit` tests | L |
| **Total** | **XL, about 8–12 engineer-weeks** |

**Recommendation:** Work in this order:

1. IR plus bundle/unpack with no emitters, to prove the disk path and incrementality.
2. Opt-in and the validator.
3. Java, validated against a real server via the harness.
4. JS.
5. Test scaffolding.
6. Optionally, move the C# formatters onto the IR.

---

## 4. Client runtime, protocol & feature parity

### 4.1 Parity matrix (condensed)

Verdict legend:

- **Full**: a direct mapping exists.
- **Work**: achievable with deliberate design.
- **Risk**: a platform limit prevents an exact match on at least one client.

| Feature (C# source) | Java | JS | Verdict |
|---|---|---|---|
| Connect/TryConnect/Disconnect, `StateChanged`, `DisconnectedTask` | futures + listeners, `AutoCloseable` | Promises, `EventTarget`, `Symbol.asyncDispose` | Full |
| Reconnection policy (`IReconnectionPolicy`, default 0/2/10/30 s) | interface + scheduler | object + `setTimeout` | Full |
| Lifecycle hooks `OnConnected(isReconnected)`, `OnDisconnected`, `OnReconnecting` | overridable methods | overridable methods | Full |
| Preamble, framing, all message bodies | runtime codec | runtime codec | Full |
| Greeting (version string, hashes, `MaxMessageSize`, credits) | generated constants | generated constants | Full |
| Timeouts & ping (client pings; server only replies) | `ScheduledExecutorService`, `nanoTime` | timers, `performance.now()` | **Work** (browser/Android throttling, §4.5) |
| Greeting auth token (`Authenticate`), on every reconnect | sync + **async** supplier | `() => Uint8Array \| Promise` | Full (add async to C# too) |
| HTTP `Authorization` header on the upgrade | yes | Node yes / **browser impossible** | **Risk** |
| TLS: custom CA, pinning, client certificates | `TrustManager`/`KeyManager` | Node `tls` options / **browser none** | **Risk** |
| `Context.RemoteCertificate` | from `SSLSession` | Node yes / **browser `null`** | **Risk** |
| Invocations: void / `ValueTask` / `ValueTask<T>` | `void` / `CompletableFuture<Void>` / `CompletableFuture<T>` | `void` / `Promise` | Full |
| Cancellation (`CancellationToken` → `InvocationCancellation`) | runtime `CancellationToken` + `future.cancel()` | trailing `{ signal }` → `AbortError` | Work (fix C# quirk, §4.3) |
| Invocation credits (FIFO gate; fire-and-forget also waits for credit) | lock + deque | resolver queue | Full |
| Large messages (`MessageStart`/`Chunk`/`Abort`, budget, `NexusBusyException`, `NexusMessageTooLargeException`) | chunked writer | same | Full |
| **Receiving invocations** (server→client: queue, concurrency limit, `SeparatePool`, cancellation, streamed results, `RemoteExceptionDetail`) | bounded executor + separate pool | promise limiter + separate limiter | Work (largest hidden piece) |
| Authorization results (`ProxyUnauthorizedException`) | `NexusUnauthorizedException` | `NexusUnauthorizedError` | Work (fix C# pipe hang, §4.3) |
| Remote error detail `[code,type,message]` | exception fields | error fields | Full |
| **Duplex pipes, both roles** (server can open pipes to client), credit, half-close, refusal | async read/write + `InputStream`/`OutputStream` adapters | `ReadableStream`/`WritableStream` (bytes, HWM 0) | Full (L effort) |
| Pipe relaying (`ForwardToAsync`, `BridgeAsync`) | helpers | `pipeTo` + `bridge()` | Full |
| Typed channels (varint framing, batch, extra credit) | `NexusChannelReader<T>` (+ Kotlin `Flow` module) | `AsyncIterable<T>` | Full |
| Synchronized collections (`INexusList<T>`: events, Ack, reset) | `NexusList<T>` + listeners | `NexusList<T>` + `EventTarget` | Work (blocked on spec G10) |
| Client pool | `Semaphore`-based | async semaphore | Full |
| Metrics (~25 instruments, identical names/tags) | listener SPI + optional Micrometer | listener + optional OpenTelemetry | Work |
| Snapshots (~60 fields) | immutable POJOs | frozen objects | Work |
| Logging | `NexusLogger` + SLF4J/`android.util.Log` adapters | `NexusLogger` + console | Full |
| Session context (`Proxy`, `Id`, `Store`, `CreatePipe`, …) | same members | same; `id: bigint` (random 64-bit) | Full |
| `AsyncLocal` execution-context flow | none | Node `AsyncLocalStorage`; browser none | **Risk** (platform; document) |
| Custom `[NexusFormatter]` | — | — | **Error** (§2) |
| Transports | HttpSocket TLS + WSS (+ cheap TCP/TLS) | Node: HttpSocket TLS + WSS; browser: WSS | **Risk** (QUIC everywhere; UDS on Java 11) |
| Config validation (`ValidateFlowControl`) | same check | same check | Full |
| Injectable clock | `Clock` SPI | injectable timers | Full (required for tests) |

**Recommendation:**

- The critical path is receive-side execution, two-role pipes, collections and serialization.
- Accept documented platform exceptions **only** for: browser TLS and headers, execution-context flow, QUIC and UDS. Custom formatters are an error for now, with an escape hatch later.
- Publish this matrix in the public docs, together with the workaround for each exception.

### 4.2 Concurrency mapping

**C# baseline:**

- one read loop per session;
- coalesced control replies;
- an `AsyncMutex` for writes;
- bounded invocation pools;
- every completion source uses `RunContinuationsAsynchronously`.

**Java:**

- One blocking reader thread and one writer thread per connection. NIO+`SSLEngine` is not worth it at client scale.
- Invocations run on a configurable executor.
- **Never complete user-visible futures on the reader thread.** `CompletableFuture.complete()` runs dependent stages inline, so user code could deadlock the read loop. Complete them via `executor.execute(...)`.
- On Android:
  - no IO on the caller thread;
  - an optional `callbackExecutor` (main thread) for callbacks;
  - avoid APIs above level 24: `Flow` is API 30, `Base64` and `java.time` are API 26, and `java.net.http` does not exist. Enforce this with Animal Sniffer or Lint in CI.

**JS:**

- Parse frames synchronously in the socket callback, then enqueue invocations.
- Batch writes per microtask.
- Backpressure: Node uses `drain`; the browser uses `bufferedAmount`.
- `MaxConcurrentConnectionInvocations` becomes an in-flight promise counter.

**Flow-control rule (both runtimes):** grant pipe and channel credit **only when the consumer reads** (`pull()`/`read()`), never when bytes arrive from the network. This one rule keeps backpressure faithful. In the browser, NexNet credits bound receive memory even though the WebSocket API has no receive backpressure.

**Recommendation:** Use the model above. The Kotlin coroutine/`Flow` adapters go in an optional `nexnet-client-kotlin` artifact.

### 4.3 C# behaviours to fix *before* other languages copy them

1. ✅ **Pre-send cancellation returns `default` instead of throwing.** In `SessionInvocationStateManager.cs:122-126` a token that is already cancelled calls `TrySetNoResult()`, and `RegisteredInvocationState.cs:346` returns `default!`. JS would resolve `undefined`; Java would return `null`, which becomes an NPE when unboxed. **Throw `TaskCanceledException`.**
2. **An unauthorized pipe/channel method sends nothing**, so the opener's `ReadyTask` hangs until disconnect (`MethodEmitter.EmitNexusInvocation` returns before registering the pipe). **Send `DuplexPipeUpdateState[Refused]`.**
3. **`ClientConfig.Authenticate` is synchronous only**, so OAuth token refresh blocks. **Add an async overload.**
4. **Plaintext-with-warning parity:** add the same once-per-client warning to the C# client configs so the policy matches everywhere. Expose `IsSecure` on the client.
5. **NexNet.Asp WebSocket hardening:**
   - `UseWebSocketNexusServerAsync` calls `app.UseWebSockets()` with defaults (`NexNetMiddlewareExtensions.WebSocket.cs:134`), so there is no `AllowedOrigins`. Cookie-authenticated browsers are exposed to cross-site WebSocket hijacking. **Expose `AllowedOrigins`.**
   - Optionally add a SignalR-style query-string access-token hook, with log redaction.

### 4.4 Transports & TLS

**HttpSocket contract** (from code; not yet in the spec):

```
GET <path> HTTP/1.1
Host: <host>[:port]
Connection: Upgrade
Upgrade: nexnet-httpsockets
[Authorization: ...]   [Origin: ...]
```

- Success is `101` with matching `Connection`/`Upgrade` headers; then the raw NexNet stream follows.
- `401`/`403` map to `AuthenticationError`, and `500` to `InternalError`.
- The server also accepts HTTP/2 extended CONNECT; clients don't need it.
- **Gotcha:** the server writes its preamble right after accepting, so bytes that arrive after `\r\n\r\n` in the same read belong to the session and must be fed to it.

**Java HttpSocket:**

- Use a raw `SSLSocket` with a hand-written HTTP/1.1 upgrade.
- **Hostname verification must be enabled explicitly** (`setEndpointIdentificationAlgorithm("HTTPS")`); `SSLSocket` does not verify the hostname by default.
- Set SNI via `SSLParameters`.
- Don't offer ALPN `h2`.
- OkHttp can't do arbitrary upgrade tokens (verify against OkHttp 5) and pulls in about 2 MB of dependencies.

**Node HttpSocket:** `https.request` plus the `upgrade` event. Feed the `head` buffer to the session first.

**WSS:** write **one small RFC 6455 client (~500 LOC)** per language, over the same TLS stack as HttpSocket, for Java and Node.

- `java.net.http` doesn't exist on Android.
- Node's global `WebSocket` can't take TLS options or headers.
- The browser uses the native `WebSocket` with `binaryType='arraybuffer'` and **no subprotocol** (the server selects none, and the browser fails the handshake if it asked for one).
- NexNet treats WebSocket as a byte stream: frames span messages, and outbound writes should be coalesced.

**TLS parity:**

| Capability | C# | Java | Node | Browser |
|---|---|---|---|---|
| System trust | ✓ | ✓ | ✓ | ✓ |
| Custom CA / pinning | ✓ | ✓ | ✓ | ✗ |
| Programmatic client cert | ✓ | ✓ | ✓ | ✗ |
| Server cert visible to nexus | ✓ | ✓ | ✓ | ✗ |
| `Authorization` on WS | ✓ | ✓ | ✓ | ✗ |

**Recommendation:**

- Make the **NexNet greeting token** the canonical, transport-independent auth path in all docs and samples.
- Document the browser TLS exceptions; the browser answer is "use a WebPKI certificate".
- Return `remoteCertificate === null` in the browser.
- Plaintext (`http://`, `ws://`) is allowed with a once-per-client warning and `client.isSecure`.

### 4.5 Protocol spec gaps (must close before clean-room implementation)

| ID | Gap |
|---|---|
| G1 | Ping roles: only the client pings and the server only replies. The idle timeout isn't advertised, so throttled clients can't adapt |
| G2 | Greeting validation: ServerGreeting `version` is the hash of the client nexus; `ClientMismatch`/`ServerMismatch` rules; version string ≤16 chars or null; 3 attempts |
| G3 | Greeting 100 vs 101: the server treats 101 like 100; resumption is a TODO |
| G4 | Auth token is `bin` **or nil** |
| G5 | Incoming method-ID validation against the version method set gives `ProtocolError` |
| G6 | Disconnect frames: no body, unassigned codes 20–38, `DisconnectDelay`, receiver must not reply |
| G7 | Pipe/channel/collection methods are sent as `IgnoreReturn \| DuplexPipe`; behaviour when authorization refuses |
| G8 | Pipe ID allocation: local IDs 1..255, 0 means unassigned |
| G9 | Large-message threshold: arguments > 65,526; chunks ≤ 65,535 |
| **G10** | **Collection sync semantics**: Ack flag, Noop+Ack, reset buffering, stale-version rule, state machine. *Largest blocker* |
| **G11** | **HTTP/WebSocket bindings are absent** (§4.4 contract, Origin, 401/403, binary-only, no subprotocol, close codes) |
| G12 | Bit layouts for `DateTime` (kind bits, Local), `TimeSpan`, `DateOnly`, `TimeOnly`, `decimal` |
| G13 | `InvocationCredit{0}` gives a `FlowControl` disconnect |
| G14 | Client-side pre-auth rule is symmetric |
| G15 | Caller-side cancellation contract (ties to fix §4.3-1) |
| G16 | **Version skew**: pre-1.0 wire changes don't bump the version byte. That is unsafe once npm/Maven packages release separately |

**Recommendation:**

1. Close G1–G16 before the runtimes start; G10 and G11 matter most.
2. For G16, bump the preamble version on every wire change, or add a `protocolRevision` to the greetings.
3. For G1, consider advertising `idleTimeout` in `ServerGreeting`, or adding a server-initiated keepalive.
4. Then build a **language-neutral conformance kit**: golden vectors plus a C# conformance server plus scenarios. This is the same thing as the test harness in §5.

### 4.6 Platform gotchas

**JS 64-bit integers:** `bigint` by default.

- `getBigInt64` is about 5–20× slower than reading a number.
- Offer an opt-in checked `number` mapping that throws on unsafe integers.

**JS decoding:**

- Use a manual UTF-8 loop for strings under ~32 bytes and `TextDecoder` above that.
- Use a growable reassembly buffer.
- Copy ext 78 payloads before creating typed-array views, because they are unaligned.

**Java:**

- Pool 64 KiB buffers.
- Read ext 78 arrays via `ByteBuffer.order(LITTLE_ENDIAN).asIntBuffer()`.

**Android battery:** a 10 s ping keeps the cellular radio awake, and Doze kills sockets.

- Ship a lifecycle-aware AAR (disconnect on background, reconnect on foreground or network callbacks).
- Use longer pings on mobile.
- Requires **per-endpoint server timeouts** (§6) or G1.

**Browser throttling:** hidden tabs throttle timers to 1/s, and to 1/min after 5 minutes, so a 10 s ping can exceed the 30 s server timeout.

- Run pings in a Web Worker.
- Reconnect on `visibilitychange`, `freeze`/`resume` and `online`.
- Enable a reconnection policy by default in the browser build.
- Verify Chrome's exemptions for pages with open WebSockets in a spike.

### 4.7 Packaging

**npm** (`@nexnet/client`, scope TBD):

- ESM only, `"sideEffects": false`, zero dependencies.
- The `exports` map gives separate `browser` and `node` entries. **Node built-ins must never be reachable from the browser entry.**
- Source is JS with JSDoc. `tsc --emitDeclarationOnly` produces the `.d.ts`; CI runs `tsc --noEmit --checkJs`.
- `engines: node >= 22`.

**Maven** (`io.nexnet:nexnet-client`, group TBD):

- A plain JAR built with `--release 11`, zero dependencies, no reflection, so no R8 rules are needed.
- Optional artifacts: `-android` (AAR lifecycle/connectivity), `-kotlin`, `-okhttp`, `-micrometer`.

**Versioning:**

- **Lockstep** across NuGet, npm and Maven, released by the same `llm-release.md` procedure.
- Generated code embeds the runtime version it targets, and the runtime refuses a major.minor mismatch.

### 4.8 Effort

- **JS runtime:** XL, about 4–6 engineer-months.
- **Java runtime:** XL, about 5–7 engineer-months.
- **Shared pre-work** (spec, C# fixes, Asp options) is M.
- **Conformance kit** (shared with §5) is L.

**Recommendation:** pre-work first, then **JS** (fastest feedback loop), then **Java**, reusing the JS fixtures. Don't start collections in either language until G10 is written.

---

## 5. Cross-language test infrastructure

### 5.1 What gets ported

| Project | Methods | Cases |
|---|---|---|
| IntegrationTests | 1342 | 2811 |
| Serialization.Tests | 114 | 189 |
| Generator.Tests | 190 | 190 |

IntegrationTests buckets:

| Bucket | Methods / cases | Port? |
|---|---|---|
| **A. Client-facing** (lifecycle, invocations, cancellation, auth, versioning, pipes, channels, collections, TLS, credits, large values, remote errors) | ~381 / ~1585 | **Every language** |
| B. Server-focused (routing, groups, relays, rate limiting, raw-TCP security tests) | ~228 / ~465 | C# only + a ~20-method `[CrossLanguageSmoke]` subset |
| C. Tied to C# runtime behaviour (ExecutionContext, ValueTask, Metrics) | 37 / 60 | No |
| D. Unit tests of C# internals | 696 / 701 | No (they serve as the native-unit checklist for each runtime) |

- Serialization tests become **golden vectors**.
- Generator tests stay in C#.

**Porting blockers** (C#-only hooks), and what replaces them:

| Hook | Replacement |
|---|---|
| `InternalOnSend` (13 uses) | Server-side observation |
| `InternalOnClientConnect` | `OnConnected` |
| `FakeTimeProvider` | Injectable clock in every runtime + a `time.advance` op |
| Synchronous `client.State` and list reads | Async snapshots |
| `GetChannel<T>()` on the internal `ReaderCore`/`WriterCore` | Explicit remote channel ops |
| `Task.Delay` used to order steps (157 uses) | Signals and barriers |

**Recommendation:** Phase 0 is a C#-only refactor of bucket A to remove these hooks. It improves the C# suite on its own merits.

### 5.2 Architecture

```
TUnit test (C#)  ──IClientUnderTest──►  InProcClientDriver (real NexusClient, same script interpreter)
      │                              └► RemoteClientDriver ──control protocol (loopback TCP, JSON)──► Language host (one per language per run)
      ▼                                                                                                 ├─ client #1 (generated client + runtime)
  real C# NexusServer (multi-endpoint: HttpSocket-TLS, WSS, TCP...)  ◄────── NexNet wire ──────────────┼─ client #2
                                                                                                        └─ …
```

**`IClientUnderTest`:**

- Async-only. Covers connect, disconnect, state, `Proxy<T>()`, `Callbacks<T>()`, `CreatePipeAsync`, `List<T>`, `SignalAsync`, `BarrierAsync`, `AdvanceTimeAsync` and `Capabilities`.
- Configured by one shared `ClientSpec`: transport, URL, TLS CA/cert/pin, timeouts, auth, limits and fake time. Each driver maps it to its native config, so a language that lacks an option fails visibly.

**Shared error kinds:** Transport, RemoteInvocation, Unauthorized, Canceled, Disconnected, Serialization, Busy and InvalidOperation. The remote driver rethrows them as the matching C# exceptions, so the existing `Assert.ThrowsAsync<…>` code keeps working.

**Generated test-only code:**

- **Generated remote proxy** (`Remote_IServerNexus : IServerNexus`): tests call the typed nexus API unchanged.
  - Remote cancellation tokens use `RemoteCancel.After(ms)`, so the timer runs inside the host.
  - An `IRemotePipe` replaces `INexusDuplexPipe`, which can't be remoted: Write, `WritePattern(seed, len)`, Drain → (len, sha256), Complete and channel ops.
- **Generated scriptable client nexus** per language:
  - `c.Callbacks<ClientNexusScript>().ClientTaskValueWithParam.Returns(54321).Times(1)`, plus `NextCallAsync()` to observe the arguments.
  - Steps that run inside the host: return, throw, delay, awaitSignal, awaitCancel, invoke and named handler.
  - A **Forward** step makes a round trip to a C# lambda, so most tests port almost unchanged.
  - Named handlers: `pipe.echo`, `pipe.drain`, `channel.pump`, `channel.echo`.

**TUnit matrix:**

- A custom `ClientMatrix` data source produces `(Language × Transport)` rows. New `WebSocketTls` and `HttpSocketTls` transports are needed.
- The language list comes from `NEXNET_TEST_LANGS`.
- Capability gaps are **visible skips**, never silent.
- Hosts are a `[ClassDataSource<LanguageHostPool>(Shared = PerTestSession)]` with `IAsyncInitializer`.
- `[ParallelLimiter]` applies per host.
- The leak audit runs in `[After(Test)]`.

### 5.3 Control protocol

**Carrier:** the host dials a C#-owned loopback socket, given the port and a random token. The browser host reaches it through a Playwright-launched page and a WS↔TCP bridge.

**Framing:** `u32 BE length + u8 kind` (0x01 = JSON).

**Messages:**

- `hello{proto, lang, runtime, lib, schemaHash, capabilities}`
- `req{id, test, op, args, timeoutMs}`
- `res{id, ok, value|error{kind, message}, hostNs}`
- `evt{test, h, ev, …}`

**Rules:**

- **Handles are scoped to a test**, so leak checks are per test even though the host is shared.
- A **`schemaHash` mismatch** means a stale host build, and the run fails fast.

**Ops (~45):** session (`test.begin`, `test.end` returning the leak audit, `host.dumpThreads`), client lifecycle, `invoke` (detachable futures, cancel tokens), scripts and signals, pipes, channels, lists, and `time.advance`.

**Values:** typed JSON.

- Integers above 2^53 are sent as strings.
- Floats are sent as bit-pattern strings.
- Bytes are base64.
- Objects are arrays by key; unions are `[tag, value]`.

Bulk data goes as seeded patterns plus sha256 digests, never large base64.

**Size:** each host is about 1.5–2.5k hand-written lines. Everything nexus-specific is generated, which keeps Rust and C++ hosts cheap.

**Crash handling:**

- A ping miss over 5 s or a process exit triggers a thread dump (`jcmd`, or `process.report` for Node).
- In-flight requests fail with `HostCrashed`.
- The host is restarted and the run continues.

### 5.4 Build pipeline

1. Move `TestInterfaces/*.cs` into a new **`src/NexNet.TestNexus`** project.
2. Add an **`IInteropNexus` type zoo** with echo methods for every §10.4 type, objects, unions, dictionaries, nullables, ext 78 and large values. Today's test nexuses only use `int`, `byte[]`, pipes and `INexusList<int>`.
3. The generator (with `EmitTestScaffolding`) writes into `artifacts/interop/<lang>/generated`.
4. Incremental targets run Gradle `installDist` (`--release 11`) and `npm ci`.
5. `LanguageHostPool` launches the hosts, or fails with the exact build command to run.

### 5.5 Timing risks

**Risks:**

- `.Timeout(1)` appears about 1,833 times, and `Timeout(0.1)` appears too.
- 157 sleeps are used to order steps.
- `CancellationTokenSource(100)` races: if the cancel travels over the control channel, the race under test is never exercised.
- Negative assertions can pass falsely while events are still in flight.
- Flow-control pacing changes when every read costs a round trip.

**Mitigations:**

- `T(x)` scaled timeouts: C# 1×, Node and JVM 3×, browser 5×, plus slack.
- Assert durations measured inside the host (`hostNs`).
- Warm-up self-test before a host is admitted.
- Run cancel timers, delays and slow readers inside the host.
- `BarrierAsync()` for negative assertions.
- A **server frame-injection hook** so `RemoteExceptionDetailTests` and client-hardening tests become portable.

### 5.6 Golden vectors

- **Files:** `tests/golden/*.jsonl`, one vector per line: `{id, type, value, hex, mode: both|decode|reject}`.
- **Generation:** a C# tool generates them from `WriterGoldenTests`, `ProtocolMessageTests`, `PrimitiveArrayTests`, `GeneratedFormatterTests` and the fuzz corpus, and cross-checks against MessagePack-CSharp.
- **Freshness:** the files are checked in, and CI regenerates them and fails on any diff.
- **Verification:** each language checks them with ~150 lines of JUnit `@TestFactory` / Vitest `test.each`.
- **Reverse direction:** live echo round trips through the interop zoo, plus seeded property dumps that C# verifies.

### 5.7 CI

**Jobs:**

- **Existing:** build + C# tests.
- **`golden`:** freshness check + JUnit/Vitest.
- **`interop` matrix (java, node):** JDK 11/21 and Node 22/24 on every PR, TLS HTTP transports only.
- **Nightly:** browser (Playwright, WSS), plaintext, full matrix, an Android API-24 check, and 10× stress runs.

**Cost:** about 760 cases per language, roughly 2–4 minutes at 8–16 in parallel.

**Policy:**

- Quarantine with `[Flaky("issue")]`, which maps to `Retry(2)` and is reported. No blanket retries.
- Rich artifacts: control transcripts, host logs, thread dumps and Playwright traces.

### 5.8 Gotchas

1. ✅ **`src/server.pfx` has no SubjectAltName.** Its subject is `CN=127.0.0.1, CN=localhost`, it has no extensions, and it expires 2033. Java, Node and Chromium will reject it. **Regenerate a test CA plus a leaf certificate with `DNS:localhost, IP:127.0.0.1`.** Don't use trust-all, or the untrusted-certificate tests become meaningless.
2. **Browsers** can't send auth headers, client certificates or pins, and can't do HttpSocket. Those become capability skips, and browser auth goes through the greeting token.
3. **Each runtime must expose diagnostic counters** (sessions, pipes, timers, pending invocations, subscriptions) for the leak audit. That is a runtime requirement, decided now.
4. **The in-proc C# driver must use the same script interpreter** as the hosts, or the semantics drift.
5. **On Windows, kill the whole process tree**, so orphaned JVMs don't hold ports.
6. **The control socket is loopback-only and requires the token.**

### 5.9 Effort

**About 5–7 engineer-months** before Rust/C++. The largest piece is porting ~380 methods: XL, about 6–8 weeks of focused work.

**Recommendation:**

1. Phase 0: the C# refactor and the certificate.
2. Build the harness with **Node as the reference host**.
3. Get ~50 core tests green on C# and Node.
4. Add Java.
5. Port the remaining tests file by file.

---

## 6. Multi-transport server (one server, many listeners)

### 6.1 Current state

`NexusServer` (`src/NexNet/NexusServer.cs`) has exactly one of each:

- `ServerConfig`
- listener
- cancellation token source
- `MetricsTagSet`
- rate limiter
- pending-handshake counter

`Configure` can run only once.

**It already half-works.** `IAcceptsExternalTransport.AcceptTransport` gates only on `State == Running`, not on the connection mode. `MapHttpSocketNexus(cfg, server)` and `MapWebSocketNexus(cfg, server)` never call `Configure`. So a TCP/TLS-configured server can also accept ASP.NET WSS and HttpSocket sessions today. The tests already do this (`BaseTests.cs`). In that setup the following are already shared:

- session registry
- groups
- collections
- rate limiter
- `Clients.All` broadcasts

**What is wrong in that mode:**

- Every session gets the primary config's `TransportName` metric tag and session settings. The `Map*` config's `MaxMessageSize`, `Authenticate` and timeouts are **silently ignored**.
- Only one listener-mode transport is possible.
- Both `Use*NexusServerAsync` helpers can't be used together: the second throws "already configured".
- It relies on `Unsafe.As` to an explicit interface implementation.

Almost every session setting already flows through `NexusSessionConfigurations.Configs`. So per-endpoint overrides come nearly for free; only the server fields need to become plural.

### 6.2 Proposed API

```csharp
var server = ServerNexus.CreateServer(tcpTlsConfig, () => new ServerNexus()); // unchanged: primary = server-wide options + endpoint #0
server.AddListener(udsConfig,  name: "uds");                                  // listener-mode configs (TCP/TLS/UDS/QUIC)
var wss = server.AddExternalEndpoint(wsConfig, name: "wss");                  // receiver handle bound to its own config + tags
app.MapNexusWebSocket(server, wsConfig);
app.MapNexusHttpSocket(server, httpSocketConfig);
await server.StartAsync();
```

| Scope | Settings |
|---|---|
| **Server-wide (primary only)** | Logger, Time, MeterFactory, RateLimiting, MaxPendingHandshakes, session manager, watchdog period, collections |
| **Per endpoint** | All session settings (MaxMessageSize, Timeout, HandshakeTimeout, flow control, SerializerOptions), Authenticate, AuthorizationCacheDuration, TLS/socket options, ASP path/auth/proxy headers, TransportName |

Per-endpoint settings make useful overrides possible. For example: Untrusted limits and a small `MaxMessageSize` for public browsers, **longer idle timeouts for mobile clients** (needed by §4.6), and auth on public WSS but not on internal UDS.

**Internals:**

- One accept loop per listener. A fault in one marks that endpoint Faulted instead of stopping the server.
- A single extracted accept routine.
- The watchdog uses each session's own timeout.
- `MetricsTagSet` per endpoint, plus an optional low-cardinality `nexnet.endpoint` tag.
- A child logger per endpoint.
- `NexusServerSnapshot.Endpoints[]`.

**Start:**

- All-or-nothing bind with rollback, and a `TransportException` naming the failing endpoint.
- Opt-in `BestEffort` mode.
- New `Starting`/`Stopping` states.

**Stop:**

1. Gate new accepts.
2. Close listeners in parallel.
3. Stop collections.
4. Disconnect sessions in parallel, bounded.
5. Dispose.

**Rate limiting:** keep **one global limiter**, so per-IP limits span transports. Otherwise an attacker multiplies their budget per transport. An optional per-endpoint connection cap can be added.

**Security guard:** throw at start if the primary requires `Authenticate` but some endpoint does not, so an endpoint can't accidentally become a back door.

**ASP lifetime:** a hosted lifecycle service starts the server after Kestrel binds and stops it on `ApplicationStopping`. `Use*` becomes "configure, or attach if already configured".

**`Context.Endpoint`:** a new `NexusEndpointInfo(Name, Transport, IsSecure, Path)`, available from `OnAuthenticate` onward. It allows per-transport policy and lets the cross-language harness assert which transport a foreign client used.

### 6.3 Existing bugs to fix in the same work

1. ✅ **The state flips to Running before the bind.** `NexusServer.cs:226` sets it; the listener is created at `:236`. A bind failure (e.g. AddressInUse) leaves the server Running with no listener, no watchdog and no collections.
2. ✅ **The rate limiter is disposed and nulled in Stop (`:309-310`) but created only in Configure (`:184`).** **A restarted server has no rate limiting.**
3. A single accept-loop fault stops the whole server.
4. Stop disconnects sessions sequentially, each with a 200 ms `DisconnectDelay`, so about 1,000 sessions take about 200 s.
5. The TLS handshake runs inside the accept loop, so one slow client blocks every TLS accept for up to 5 s.
6. QUIC `AcceptInboundStreamAsync(CancellationToken.None)` inside the accept loop can block the QUIC listener forever.
7. `QuicTransportListener.Create` mutates the shared `SslServerAuthenticationOptions.ApplicationProtocols`.

### 6.4 Back-compat and effort

**Back-compat:**

- `CreateServer(config, factory)` is unchanged, and all new APIs are additive.
- `NexusServerState` gains values.
- **One behaviour change:** `Map*(cfg, server)` with a config different from the server's would now *apply* that config's session settings. If `cfg` is the primary, today's behaviour is kept exactly.

**Effort:**

- Core: M (3–5 days).
- ASP attach + hosted service: S–M.
- Tests and docs: M.
- Handshake-off-loop hardening: S–M.
- **Overall: L, about 2 weeks.**

**Recommendation:** three PRs:

1. Lifecycle bug fixes + an internal endpoint refactor with no public API change.
2. `AddListener`/`AddExternalEndpoint`, `Context.Endpoint`, per-endpoint metrics, ASP attach and the hosted service.
3. Move TLS and QUIC handshakes off the accept loop.

The cross-language harness **can start now** on the interim `Map*(cfg, server)` pattern.

---

## 7. TUnit migration

### 7.1 Inventory

| | Integration | Serialization | Generator |
|---|---|---|---|
| `[Test]` | 944 | 90 | 190 |
| `[TestCase]` | 1,882 | 92 | 0 |
| `Assert.That` | 2,235 | 233 | 166 |
| `Assert.Multiple(lambda)` | 108 | 0 | 0 |

- Assertions use the constraint model only.
- None of these are used: `Category`, `Timeout`, `Retry`, `Parallelizable`, `SetUpFixture`.
- The transport matrix is stacked `[TestCase(Type.X)]` on 394 methods; 250 of them use all 6 transports.
- **There is no parallelism configuration, so NUnit runs serially today.**

### 7.2 Mapping and traps

| NUnit | TUnit | Trap |
|---|---|---|
| `[TestCase(Type.X)]` ×6 | Custom `[Transports(Except=…)]` data source, or `[MatrixDataSource]` | Don't emit 1,847 `[Arguments]` lines |
| `[SetUp]`/`[TearDown]` | `[Before(Test)]`/`[After(Test)]` | `After` hooks run derived-first; a throw fails the test |
| Instance `[OneTimeSetUp]` | **Static** `[Before(Class)]` or `ClassDataSource` | TUnit creates one instance per test, so the UDS `_counter` resets and socket names collide |
| `TestContext.CurrentContext.Result` | `ctx.Execution.Result` | — |
| `Assert.That(a, Is.EqualTo(b))` | `await Assert.That(a).IsEqualTo(b)` | Must be awaited; check array equality semantics |
| `Assert.Throws<T>` (exact) | `ThrowsExactly<T>` | TUnit `Throws<T>` accepts subtypes |
| `Assert.Multiple` | `using (Assert.Multiple())` | The code fixer doesn't convert these (108 sites) |
| Sync asserts in callbacks (~60) | Capture, then assert; or a throwing helper | `.GetAwaiter().GetResult()` raises TUnitAssertions0002, which is an **error** under TreatWarningsAsErrors |
| Asserts near `ref struct` `MsgPackReader` | Read into locals, then assert | You can't `await` while a ref struct is live |
| `[Repeat(n)]` | `[Repeat(n-1)]` | TUnit runs n+1 times |

### 7.3 Parallelism hazards

TUnit runs tests in parallel by default. IntegrationTests has unsafe shared state:

- ✅ `ResetIdCounter()` resets a static counter in every SetUp (`BaseTests.cs:99`).
- The `FreeTcpPort` race.
- UDS name collisions.
- Fire-and-forget teardown.
- `Trace.Listeners` growth.
- About 1,833 `Timeout(1)` calls under load.

**Recommendation:**

- Start IntegrationTests with `[assembly: NotInParallel]`.
- Serialization and Generator tests are parallel-safe.
- Enable parallelism class by class after these fixes:
  - port 0 / OS-held listeners;
  - a UDS path per test;
  - awaited teardown;
  - no static ID counter.

### 7.4 Tooling

- **Runner:** TUnit is Microsoft.Testing.Platform only. .NET 10 needs a root `global.json` with `{"test":{"runner":"Microsoft.Testing.Platform"}}`, and then *every* test project must be on MTP.
- **Side-by-side:** for a gradual migration, first move NUnit to `EnableNUnitRunner`.
- **Packages:**
  - Remove `Microsoft.NET.Test.Sdk`.
  - Remove the unused `Mvc.Testing`.
  - Check that TUnit's coverage and TRX dependencies pass the NuGet audit (NU1901–1904 are errors here).
- **Filters:** `--filter "FullyQualifiedName~X"` becomes `--treenode-filter "/*/*/*/X"`. Update `llm-dev.md` (lines 28–34, §4.4) and `llm-release.md` (lines 116–124).
- **CI:**
  - TRX output + upload on failure.
  - `--minimum-expected-tests` per suite, to catch discovery collapse.
  - Built-in coverage (coverlet is incompatible).
- **Engine mode:** keep **source-gen**. `--reflection` silently drops internal tests (issue #7677), and 84 of 96 integration classes are internal.
- **Version:** TUnit ships almost daily (1.86.0 on 2026-11-16). **Pin a version at least two weeks old.**

### 7.5 Features the harness will use

- `ClassDataSource(Shared = PerTestSession/Keyed)` + `IAsyncInitializer` for host processes.
- Matrix and custom data-source generators, plus `TestDataRow` skips.
- `[Timeout]` with a token.
- `[After(Test)]` / `ITestExecutor` for leak checks.
- A `RetryAttribute.ShouldRetry` subclass that retries only on host-crash exceptions.
- `ParallelLimiter` per host.
- Artifacts for logs.

**Spike first:**

- whether a throw after `action()` in `ITestExecutor` fails the test;
- when PerTestSession fixtures are disposed.

### 7.6 Plan and effort

1. **Step 0:** NUnit on MTP + `global.json`.
2. **Generator.Tests:** S, about half a day (pilot).
3. **Serialization.Tests:** M, 1–2 days.
4. **IntegrationTests:** L–XL, 5–8 days to green serially, plus 3–5 days to make it parallel-safe.
5. **Fuzz and Benchmarks:** no change.

**Total about 2–3 weeks.**

**Method:**

- Use the TUNU0001 code fixer for the bulk.
- Script the transport-matrix rewrite.
- Do the lifecycle, callback, ref-struct and semantic asserts by hand.
- Compare `--list-tests` counts per suite before and after.

---

## 8. Pre-existing issues found (independent of the new clients)

| # | Issue | Where | Verified |
|---|---|---|---|
| 1 | Overloaded/duplicate method names crash the generator | `NexusDataExtractor.cs:926` | ✅ |
| 2 | Pre-send cancellation returns `default` instead of throwing | `SessionInvocationStateManager.cs:122-126`, `RegisteredInvocationState.cs:346` | ✅ |
| 3 | Server state set to Running before the listener binds | `NexusServer.cs:226` vs `:236` | ✅ |
| 4 | Rate limiter lost after Stop → Start | `NexusServer.cs:184`, `:309-310` | ✅ |
| 5 | `server.pfx` has no SAN (rejected by non-.NET TLS stacks) | `src/server.pfx` | ✅ |
| 6 | Unauthorized pipe method hangs the opener's `ReadyTask` | `MethodEmitter.EmitNexusInvocation` | reported |
| 7 | WebSocket middleware has no `AllowedOrigins` (cross-site WS hijacking with cookie auth) | `NexNetMiddlewareExtensions.WebSocket.cs:134` | ✅ (defaults used) |
| 8 | Serialization diagnostics reported with `Location.None` | `NexusGenerator.ReportSerializationDiagnostics` | reported |
| 9 | Accept-loop fault stops the whole server; sequential 200 ms disconnects on stop | `NexusServer.cs` | reported |
| 10 | TLS/QUIC handshakes inside accept loops (head-of-line blocking) | `TcpTlsTransport.cs`, `QuicTransport.cs` | reported |
| 11 | `Map*` config's session settings silently ignored | `NexusServer.cs:377` | reported |

**Recommendation:** fix 1–5 and 7 early. They are small, valuable on their own, and unblock later phases.

---

## 9. Recommended roadmap

| Phase | Content | Depends on |
|---|---|---|
| **0. Foundations** | Fix §8 items 1–7; regenerate the test certificate (CA + SAN); close spec gaps G1–G16; protocol-revision policy (G16) | — |
| **1. TUnit** | Step 0 (MTP), then Generator, Serialization, Integration (serial); docs/CI updates | — |
| **2. Server** | Multi-endpoint server (3 PRs), `Context.Endpoint`, per-endpoint timeouts | 0 |
| **3. Generator core** | IR, opt-in, `TargetValidator`, bundle + unpack target, `nexnet-ir.json` | 0 |
| **4. Harness skeleton** | C# refactor of bucket A (hooks, barriers, `T(x)`, port 0); `IClientUnderTest` + in-proc driver; TestNexus + interop zoo; golden vectors; control protocol | 1 |
| **5. JavaScript** | JS emitters + test scaffolding; JS runtime (codec → session → invocations → pipes → channels → collections); Node host; then browser host | 3, 4 |
| **6. Java** | Java emitters + scaffolding; Java runtime; JVM host; Android API-24 checks; optional AAR/Kotlin modules | 5 (reuse fixtures) |
| **7. Parity hardening** | Port remaining bucket-A tests; nightly full matrix; public parity table; lockstep release in `llm-release.md` | 5, 6 |
| **8. Future** | Rust/C++ via `ITargetLanguage` + a new host (~M each) | 7 |

---

## 10. Open decisions

| # | Decision | Recommendation |
|---|---|---|
| 1 | **Browser authentication**, since browsers can't set `Authorization` on WS | Greeting token as canonical (all clients), plus an optional query-string token hook in NexNet.Asp |
| 2 | **Idle timeout / keepalive** for throttled browsers and Android Doze (G1) | Per-endpoint server timeouts now (§6); advertise `idleTimeout` in `ServerGreeting` at the next protocol revision |
| 3 | **Change C# pre-send cancellation to throw** (behaviour change) | Yes, in the next minor release, noted in the release notes |
| 4 | **Protocol version policy** (G16) | Bump the preamble version on every wire change from now on |
| 5 | **Package names**: npm scope, Maven group | e.g. `@nexnet/client`, `io.nexnet:nexnet-client` (check availability) |
| 6 | **Optional extra transports** (raw TCP/TLS in Java and Node) | Defer. HttpSocket over TLS + WSS covers the decided scope, and TCP/TLS is cheap to add later |
| 7 | **JS `long` mapping** | `bigint` by default; opt-in checked `number` per member or assembly |
| 8 | **Client pool in Java/JS** | Ship it (cheap, and keeps parity) |
| 9 | **Custom formatter escape hatch** | Error now; `[NexusTargetFormatter<T>(Target, "fqcn")]` later |
