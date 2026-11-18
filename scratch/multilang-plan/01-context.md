# Shared context for every work unit

Read this file together with `00-index.md` and your own card. Section numbers like "Report §3.5" refer to `02-feasibility-report.md`.

## 1. Repo conventions (non-negotiable)

- **.NET 10.** `src/Directory.Build.props` sets `TreatWarningsAsErrors` for every project under `src/`.
  - Warnings in generated code count.
  - NuGet audit advisories NU1901–NU1904 are errors.
  - Fix the cause of a warning. Never add `NoWarn` or `WarningsNotAsErrors`.
- **Docs to read for orientation:** `llm-dev.md` (architecture), `llm-usage.md` (public API), `docs/internals/protocol-specification.md` (wire format), `docs/internals/flow-control.md`.
- **Public API changes need three updates:**
  - `llm-usage.md`, if the API is user-visible;
  - the matching `docs/articles/*.md`;
  - a line in the next release notes (`docs/articles/releases/`).
- **No reflection in runtime paths.** Native AOT and trimming must keep working, for both the C# library and generated C#.
- **The generator** (`src/NexNet.Generator`) is a Roslyn incremental generator.
  - It must not do file IO (RS1035 is an error here).
  - Its models must be equatable and contain no Roslyn symbols.
  - Its output must be deterministic: ordinal ordering, and no timestamps or absolute paths.
- **Tests:** NUnit until T04 merges, TUnit after it. There are three suites: Serialization, Generator, Integration.

## 2. Isolation rules (D27)

- **Nothing under `src/` may invoke** `gradle`, `java`, `javac`, `node`, `npm` or `npx` during `dotnet build` or `dotnet test`, unless one of these environment variables is set:
  - `NEXNET_TEST_LANGS` (e.g. `node,java,browser`);
  - `NEXNET_REQUIRE_SDKS=1`.
- **When neither is set:**
  - interop rows are not generated;
  - SDK-based compile checks report a visible skip with the reason (e.g. "javac not found; set NEXNET_REQUIRE_SDKS=1 in CI").
- **When `NEXNET_REQUIRE_SDKS=1`,** a missing tool is a test **failure**.
- **Paths:** Java and JS sources live in `clients/java` and `clients/js`. Generated test-host code goes to `artifacts/interop/<lang>/generated`, which is gitignored.

## 3. Contracts (frozen when their card merges)

| Contract | Owner card | Consumers | Location |
|---|---|---|---|
| IR schema + `nexnet-ir.json` (versioned `IrVersion`) | G01 | G03–G05, H06, J03/J04/J11, K03/K04/K11, future Rust/C++ | `src/NexNet.Generator/Ir/`, `docs/internals/nexnet-ir.md` |
| Generated-code ↔ runtime API, JS | J01 | J03, J04, J11 and J05–J10 implementations | `clients/js/packages/nexnet/src/contract/*.js` (JSDoc'd interfaces) + `docs/internals/js-runtime-contract.md` |
| Generated-code ↔ runtime API, Java | K01 | K03, K04, K11 and K05–K10 | `clients/java/nexnet-client/src/main/java/com/dtronix/nexnet/spi/*` + `docs/internals/java-runtime-contract.md` |
| Formatter API (`Formatter<T>`, `MsgPackReader/Writer`) | J02 / K02 | emitters | as above |
| Golden vector format | H03 | J02, K02, future languages | `tests/golden/README.md` |
| `IClientUnderTest`, `ClientSpec`, error kinds, capabilities | H04 | P01*, H05 | `src/NexNet.Interop/` |
| Control protocol | H05 | J12, K12, future hosts | `docs/internals/interop-control-protocol.md` |

**Contract-first rule.** J01 and K01 publish the runtime API skeleton before anything else in their track:

- interfaces and signatures;
- `throw new Error('not implemented')` / `UnsupportedOperationException` bodies.

Emitters (J03/J04, K03/K04) and runtime implementations (J05–J10, K05–K10) then proceed in parallel against that skeleton.

## 4. Language conventions

### JavaScript (`@dtronix/nexnet`)

**Language and package:**

- ESM only: `"type": "module"`, `"sideEffects": false`, Node ≥ 22.
- Zero runtime dependencies.
- Plain `.js` with full JSDoc. `.d.ts` files are produced by `tsc --allowJs --declaration --emitDeclarationOnly`.
- CI runs `tsc --noEmit --checkJs`.
- `exports` map entries:
  - `"."` with `browser`, `node` and `default` conditions;
  - `"./node"` for HttpSocket/TCP/TLS/UDS. It uses `node:` built-ins, which must **never** be reachable from the browser entry.

**Async and data types:**

| Concept | Use |
|---|---|
| Async results | `Promise` |
| Cancellation | trailing `{ signal }` (AbortSignal) → rejects with `signal.reason` / `AbortError` |
| Pipes | `{ readable: ReadableStream<Uint8Array>, writable: WritableStream<Uint8Array> }`, highWaterMark 0 |
| Channels | `AsyncIterable<T>` reader / writer with `write`, `writeMany`, `complete` |
| Events | `EventTarget` |
| Disposal | `Symbol.asyncDispose` |
| 64-bit integers | `bigint` (D13) |
| Session id | `bigint` |

**Read and write path:**

- Frames are parsed synchronously in the socket callback.
- User-visible promises always resolve from a microtask, never re-entrantly.
- Writes are batched per microtask.

**Tests:**

- Vitest (`clients/js/vitest.config.js`), run in Node.
- Browser-relevant suites use Vitest browser mode with the Playwright provider and Chromium (D28/D29).

### Java (`com.dtronix:nexnet-client`, package `com.dtronix.nexnet`)

**Build:**

- Built with JDK 21, `--release 11`.
- Gradle Kotlin DSL with the wrapper committed. Versions catalog in `gradle/libs.versions.toml`.
- No runtime dependencies in core.
- JSpecify `@Nullable` annotations, compile-only.
- **API-24 enforcement:** Animal Sniffer with signature `net.sf.androidscents.signature:android-api-level-24`. It runs in `check`.
- **Forbidden APIs** (absent on Android 24):

  | API | Available from |
  |---|---|
  | `java.net.http` | not on Android |
  | `java.util.concurrent.Flow` | API 30 |
  | `java.util.Base64` | API 26 |
  | `java.time` | API 26 (use the runtime value types, D14) |
  | `SSLParameters.setApplicationProtocols` | API 29 |

**Async and data types:**

| Concept | Use |
|---|---|
| Async results | `CompletableFuture` |
| Cancellation | runtime `com.dtronix.nexnet.CancellationToken`/`CancellationTokenSource`; `future.cancel(true)` also cancels remotely |
| Pipes | async `read()`/`write()` + `InputStream`/`OutputStream` adapters |
| Channels | `NexusChannelReader<T>.read(): CompletableFuture<Optional<T>>` + `readBatch` |

**Threading:**

- One blocking reader thread and one writer thread per connection.
- **Never complete a user-visible future on the reader thread.** Complete it via the configured executor.
- Never do IO on the caller thread.

**Tests:** JUnit 5. `gradlew check` runs unit tests, golden vectors and Animal Sniffer.

## 5. Type mapping (normative)

The type mapping is Report §3.5, with these amendments:

- **Java `DateTime`/`DateTimeOffset`/`TimeSpan`/`DateOnly`/`TimeOnly`** map to runtime `NexusDateTime`, `NexusDateTimeOffset`, `NexusTimeSpan`, `NexusDateOnly` and `NexusTimeOnly`. They hold ticks plus kind/offset and provide `java.time` converters that work under desugaring. `java.time` itself is never used in signatures.
- **JS** uses the same value types, holding `bigint` ticks.
- **Java C# `byte`** maps to `int`, range-checked on write.
- **User `[NexusFormatter]` types** map via `[NexusTargetFormatter<T>(target, "fqcn")]` (D12). The emitter generates a native interface stub (`Formatter<T>`), and generated code references the user class by its fully qualified name. If no mapping exists for an opted-in target, that is NEXNET040.

## 6. Protocol facts every runtime implementer must know

The authoritative source is `docs/internals/protocol-specification.md` after F09a–c land. Until then:

**Connection and handshake:**

- Preamble `NnP\x14 000 01`. Post-headers are little-endian; MessagePack is big-endian.
- **Ping roles: only the client pings; the server only replies.** After S04, the client derives its ping interval from `ServerGreeting.idleTimeoutMs`, defaulting to 1/3 of it.
- **Hashes are opaque constants** from the generator IR (`ExportIr.Handshake`). Never recompute them.
- The `version` string must be null when the server is unversioned, and at most 16 chars otherwise.

**Messages and IDs:**

- Disconnect codes 20–38 are recognized before anything else, and unknown codes in that range are still disconnects.
- Pipe, channel and collection methods are invoked with `IgnoreReturn | DuplexPipe`.
- Local pipe IDs are 1..255; 0 means unassigned.
- **Bytes that arrive after the HTTP `101` response** in the same read belong to the NexNet session and must be fed to it.
- **WebSocket is a byte stream.** NexNet frames span WS messages. Send binary messages only and request no subprotocol.

**Flow control:**

- **Grant pipe/channel credit only when the consumer reads**, never when bytes arrive from the network.
- Invocation credit is returned at **dequeue for execution**.

**Cancellation:**

- A cancel before send rejects or throws a cancellation, never returns default (D17, F03).
- A cancel after send completes the call canceled at once and still consumes the late result.

## 7. Gotchas collected from the investigation

1. **`src/server.pfx` has no SAN** until F08 lands. Non-.NET TLS stacks reject it. Use the F08 test CA (`src/NexNet.IntegrationTests/certs/`).
2. **Java `SSLSocket` does not verify hostnames by default.** Set `setEndpointIdentificationAlgorithm("HTTPS")`.
3. **Browser limits:** no `Authorization` header, client cert, pinning, custom CA, server-cert access or HttpSocket. Report these as capability skips, never as silent passes.
4. **TUnit pitfalls:**
   - `Throws<T>` accepts subtypes, so use `ThrowsExactly<T>` where NUnit's `Throws<T>` was exact.
   - Asserts must be awaited, and a synchronous wait is an error.
   - `Repeat(n)` runs n+1 times.
5. **TUnit version and mode:** pin a version at least two weeks old, and keep source-gen mode (reflection mode drops internal tests).
6. **Static counter in tests:** `NexusServer.ResetIdCounter()` is static global state. T05 removes the dependency.
7. **Default values for missing members:** members missing from an older peer get the C# initializer in C# but null/undefined/0 in Java/JS. G01 records literal initializers in the IR where they are simple constants.
8. **JS `Map` keys compare by identity.** Non-primitive dictionary keys are warned on (NEXNET046).
9. **Windows process cleanup:** kill whole process trees for language hosts.
10. **Build determinism:** keep the IR/bundle output byte-stable, or the unpack target rebuilds every time.
