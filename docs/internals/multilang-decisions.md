# Multi-language clients: design decisions

This page records the design decisions behind the Java and JavaScript clients and the server changes that came with
them. Each entry states the decision and why it was made. Other pages refer to an entry by its number, for example
[D27](#d27). For how to use the clients, see [Multi-language Clients](../articles/multi-language-clients.md); for the
runtime APIs, see the [JavaScript](js-runtime-contract.md) and [Java](java-runtime-contract.md) runtime contracts.

The clients shipped in NexNet 0.21.0. The pull requests that implemented them are listed in the
[v0.21.0 change log](../articles/releases/v0.21.0-changes.md).

## Scope and languages

- <a id="d1"></a>**D1. Client-only Java and JavaScript; the server stays .NET.** Only the client side is ported.
  One server implementation keeps the protocol's behavior in one place, and most non-.NET use cases (browsers, Android,
  JVM services) are clients.
- <a id="d2"></a>**D2. JavaScript is ESM, plain `.js` with JSDoc, and ships `.d.ts` declarations; it runs in browsers
  and on Node 22 or later.** Plain JavaScript runs without a build step in both environments, while JSDoc and the
  declarations still give TypeScript users full types.
- <a id="d3"></a>**D3. Java targets Java 11 bytecode (`--release 11`) and Android API 24, uses `CompletableFuture`,
  and stays Kotlin-friendly. It is built with JDK 21.** Java 11 and API 24 cover nearly all JVM and Android
  deployments, and `CompletableFuture` is the async type every JVM language can consume.
- <a id="d4"></a>**D4. Java transports: HttpSocket (plain and TLS), WebSocket (plain and TLS, with its own RFC 6455
  client), TCP and TLS over TCP. No Unix domain sockets.** The JDK and Android have no portable UDS client API that
  works down to API 24, and `java.net.http` is not available on Android.
- <a id="d5"></a>**D5. Node transports: HttpSocket (plain and TLS), WebSocket (plain and TLS), TCP, TLS over TCP and
  UDS.** Node's `net` and `tls` modules support all of them, so the Node client matches the C# client.
- <a id="d6"></a>**D6. Browsers use WebSocket only.** Browsers cannot open raw sockets or take over an HTTP connection,
  so WebSocket is the only transport they can offer.

## Security

- <a id="d7"></a>**D7. Plaintext transports stay allowed, but every client (C# included) logs a warning once per client
  and exposes `IsSecure`.** Plaintext is still needed on loopback and inside trusted networks; the warning and the flag
  make the choice visible instead of silent.
- <a id="d15"></a>**D15. The NexNet greeting token (`Authenticate`) is the canonical credential for every client.
  NexNet.Asp adds an opt-in query-string token hook (redacted in logs) and a WebSocket `AllowedOrigins` check.**
  Browsers cannot set an `Authorization` header on a WebSocket, so the in-protocol token is the one mechanism that
  works everywhere; the query-string hook and origin check cover deployments that must authenticate at the HTTP layer.

## Parity and code generation

- <a id="d8"></a>**D8. Nexuses opt in to target languages. Types that are awkward in a language get runtime shims;
  anything that cannot be represented identically is a generator error.** Full parity with the C# client is the goal,
  so a client that would silently behave differently is never generated.
- <a id="d9"></a>**D9. The opt-in surface is `Targets = NexusTargets.Java | NexusTargets.JavaScript` on the server
  `[Nexus]` attribute, plus `[assembly: NexusClientExport<TServer, TClient>]`.** The server nexus describes both
  sides of a connection, and the assembly attribute covers interfaces whose server class lives elsewhere or older API
  versions.
- <a id="d10"></a>**D10. Output directories are set with MSBuild properties per language (`NexNetJavaOutputDir`,
  `NexNetJavaPackage`, `NexNetJavaScriptOutputDir`) and a generic `<NexNetTarget Include="lang" …/>` item.**
  Properties are the usual way to configure a build, and the item form lets future languages be added without new
  properties.
- <a id="d11"></a>**D11. The generator emits a comment-only bundle `.g.cs` file, and a buildTransitive MSBuild target
  unpacks it to disk after compilation.** Source generators must not do file IO (RS1035), so the files travel through
  the compilation and an MSBuild task writes them.
- <a id="d12"></a>**D12. Types with a user `[NexusFormatter]` map to a native formatter with
  `[assembly: NexusTargetFormatter<T>(NexusTargets.X, "fully.qualified.Formatter")]`.** The generator emits the native
  formatter interface; a missing mapping is NEXNET040. A hand-written C# formatter cannot be translated, so the user
  supplies the matching native one.
- <a id="d13"></a>**D13. JavaScript `long`/`ulong` map to `bigint` by default, with an opt-in, range-checked
  `number` mapping per member or assembly (`[NexusJsNumber]`).** `number` loses precision above 2^53, so the safe
  type is the default, and the opt-in serves APIs whose values are known to stay small.
- <a id="d14"></a>**D14. Java date, time and decimal values use runtime value types (`NexusDateTime`,
  `NexusTimeSpan`, `NexusDecimal` and others) with converters to `java.time` and `BigDecimal`.** They keep the
  exact C# value (ticks, kind, offset) and work on Android API 24, where `java.time` is missing without desugaring.
- <a id="d31"></a>**D31. The generator emits everything a client needs, including the Java and JavaScript runtime
  sources.** Because the runtimes are not published ([D30](#d30)), consumers need no npm or Maven NexNet dependency;
  generated code and runtime always come from the same generator version. See
  [Generator-emitted client runtime](generator-emitted-runtime.md).

## Protocol and server

- <a id="d16"></a>**D16. Keepalive timeouts are set per endpoint on the server. `ServerGreeting` advertises the idle
  timeout, and clients derive their ping interval from it. Browsers and Android also reconnect on lifecycle events.**
  Clients on different transports and networks need different timeouts, and a client that learns the timeout from the
  server cannot be configured inconsistently with it.
- <a id="d17"></a>**D17. A call canceled before it is sent fails with a cancellation: `TaskCanceledException` in C#,
  `CancellationException` in Java, an `AbortError` in JavaScript.** Returning a default value would hide the
  cancellation; each language uses its native cancellation error.
- <a id="d18"></a>**D18. Before 1.0 there is no protocol version-byte policy change; the server and all clients
  upgrade in lockstep.** Wire additions use trailing fields that older readers ignore, so no version bump was needed.
- <a id="d21"></a>**D21. One `NexusServer` can have many listeners (`AddListener`, `AddExternalEndpoint`,
  `Context.Endpoint`).** A server that serves browsers over WebSocket and services over TCP should be one server with
  one set of sessions, groups and collections.

## Packaging and distribution

- <a id="d19"></a>**D19. Package names: `@dtronix/nexnet` for JavaScript and `com.dtronix.nexnet` for Java (Maven
  coordinates `com.dtronix:nexnet-client` plus optional `-android`, `-kotlin`, `-okhttp` and `-micrometer`
  modules). Versions move in lockstep with the NuGet packages.** The names identify the runtime inside generated
  output; publishing was later dropped ([D30](#d30)).
- <a id="d20"></a>**D20. The client pool ships in Java and JavaScript.** It is part of the C# client's public surface,
  and parity ([D8](#d8)) covers it.
- <a id="d30"></a>**D30. The Java and JavaScript clients are not published to npm or Maven. NuGet is the only published
  channel; release CI keeps the built clients as workflow artifacts.** The generator already carries everything a
  consumer needs ([D31](#d31)), and one channel avoids version skew between registries.

## Tooling and tests

- <a id="d22"></a>**D22. All test projects moved to TUnit before the cross-language harness was built.** TUnit's
  source-generated, data-driven tests make one test run against many client languages without reflection.
- <a id="d23"></a>**D23. Client tests are written once in C#. Each language has a thin, long-lived host process driven
  through a JSON control protocol over loopback TCP, a generated scriptable client nexus, a leak audit after every test,
  and automatic host restart.** Writing each test once keeps the languages provably equivalent; see the
  [Interop control protocol](interop-control-protocol.md).
- <a id="d24"></a>**D24. Client-facing tests run against every language; server-side behavior tests run in C# plus a
  cross-language smoke subset; server-internal tests stay C# only.** This tests every client behavior in every
  language without multiplying tests that do not involve the client.
- <a id="d26"></a>**D26. Java development needs only a JDK (Temurin 21 recommended, `--release 11`). Animal Sniffer
  checks the API-24 signature without the Android SDK. A CI job installs the Android SDK, builds the `-android`
  module, runs D8/R8 at `--min-api 24` and smoke-tests on an API-24 emulator; it runs on pull requests that touch the
  Java client (path-filtered) and weekly.** Contributors do not need the large Android toolchain, while CI still proves
  the Android build.
- <a id="d27"></a>**D27. C#-only by default: `dotnet build src` and `dotnet test` never run Gradle or npm.** Java and
  JavaScript builds live under `clients/`. Interop test rows run only when `NEXNET_TEST_LANGS` lists languages, and
  SDK compile checks are skipped visibly when a tool is missing (mandatory in CI with `NEXNET_REQUIRE_SDKS=1`). C#
  contributors keep their usual workflow and need no JDK or Node.
- <a id="d28"></a>**D28. JavaScript unit tests use Vitest in Node, and Vitest browser mode (Playwright) for the
  browser-relevant units.** One test runner covers both environments.
- <a id="d29"></a>**D29. Browser coverage: every pull request runs the browser units and the WebSocket smoke subset in
  Chromium; a weekly run (when something changed) runs the full browser suite in Chromium, Firefox and WebKit.** This
  keeps pull requests fast while still covering all three browser engines.
- <a id="d25"></a>**D25.** (Process only: where the implementation plan was kept. No effect on the code.)
