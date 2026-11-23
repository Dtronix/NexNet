# Multi-language Clients

NexNet servers stay .NET. Clients can be generated for **Java** and **JavaScript** from the same nexus interfaces, so
they use the same method ids, hashes and handshake as the C# client. The generator writes everything a client needs:
the proxies, the client nexus bases, the serializers of your types **and the client runtime itself**. There is no npm
or Maven package to install. Nothing changes for a project that does not opt in.

| Language | Runs on | Transports | Guide |
|---|---|---|---|
| JavaScript | Node 22+, browsers (ESM) | Node: WebSocket, HttpSocket, TCP, TLS, UDS. Browser: WebSocket only | [JavaScript Client](javascript-client.md) |
| Java | Java 11+ (built with JDK 21), Android API 24+ | WebSocket, HttpSocket, TCP, TLS | [Java Client](java-client.md) |

> [!NOTE]
> The Java and JavaScript clients are distributed only through the generator (the `NexNet.Generator` NuGet package).
> They are not published to npm or Maven Central. `@dtronix/nexnet` and `com.dtronix.nexnet` are the names of the
> runtime inside the generated output, not registry packages.

## Overview

1. Opt a server nexus in to one or more languages with `Targets` (or `[assembly: NexusClientExport]`).
2. Tell the build where each language's files go (`NexNetJavaOutputDir`, `NexNetJavaScriptOutputDir`).
3. Build. The output directories receive the generated client and the runtime.
4. Use them from a Gradle/Maven/Android project or a Node/browser project, as the language guides show.

## Choosing languages

`NexusTargets` (namespace `NexNet`) is a flags enum:

| Value | Language |
|---|---|
| `None` (0) | C# only (the default) |
| `Java` (1) | Java |
| `JavaScript` (2) | JavaScript, browsers and Node |

## Exporting a server nexus

Set `Targets` on the **server** nexus attribute. The export is named after the class:

```csharp
[Nexus<IChatServerNexus, IChatClientNexus>(NexusType = NexusType.Server,
    Targets = NexusTargets.Java | NexusTargets.JavaScript)]
public partial class ChatServerNexus
{
    // ...
}
```

`Targets` on a client nexus is an error (NEXNET043): the server nexus describes both sides of the connection.

The generated names drop a trailing `ServerNexus` (or `Nexus`) from the export name: `ChatServerNexus` gives
`ChatServerProxy`, `ChatClientNexusBase`, `ChatHandshake` and the factories `createChatClient` (JavaScript) and
`ChatClient.create` (Java).

## Exporting interfaces without a class

`[assembly: NexusClientExport<TServer, TClient>]` exports a client from two interfaces. Use it for an older API
version, or when the server class lives in another assembly:

```csharp
[assembly: NexusClientExport<IChatServerNexusV1, IChatClientNexus>(
    Targets = NexusTargets.JavaScript,
    Name = "ChatV1")]
```

- `Targets` is required. An export with `NexusTargets.None` exports nothing.
- `Name` defaults to the server interface name without its leading `I` (`ChatServerNexusV1`).
- The export uses the same method ids, hashes and handshake as a C# client nexus
  `[Nexus<IChatClientNexus, IChatServerNexusV1>(NexusType = NexusType.Client)]`, so it connects to a server that
  implements a later version of the API (see [Versioning](versioning.md)).
- With no server class, the export does not know which methods require authorization.
- Interfaces with duplicate method names are reported as NEXNET042 and not exported (the same error any nexus gets).

## Configuring output

Generated files are written by the build, into directories you choose. Configure each language in the project that
holds the exported nexus (the one referencing `NexNet.Generator`).

### Worked example: one project, Java and JavaScript

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>

    <!-- Java: sources for a Gradle/Maven/Android source root -->
    <NexNetJavaOutputDir>../clients/chat-java/src/generated/java</NexNetJavaOutputDir>
    <NexNetJavaPackage>com.example.chat</NexNetJavaPackage>

    <!-- JavaScript: an ES module folder for Node or a bundler -->
    <NexNetJavaScriptOutputDir>../clients/chat-web/src/generated</NexNetJavaScriptOutputDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NexNet" Version="*" />
    <PackageReference Include="NexNet.Generator" Version="*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

With `ChatServerNexus` above (`Targets = NexusTargets.Java | NexusTargets.JavaScript`), `dotnet build` writes:

```
clients/chat-java/src/generated/java/
  com/example/chat/ChatClient.java, ChatServerProxy.java, ChatClientNexusBase.java, ChatHandshake.java, …
  com/example/chat/formatters/…            serializers of your [NexusObject] types
  com/dtronix/nexnet/**                    the Java client runtime (with LICENSE.txt)
clients/chat-web/src/generated/
  ChatServerNexus.js, ChatServerNexus.d.ts, formatters.js, <one module per type>.js/.d.ts, …
  nexnet-runtime/                          the JavaScript client runtime (package.json, LICENSE, *.js)
```

The language guides show how a Gradle or npm project picks these up.

### Properties

| Property | Meaning |
|---|---|
| `NexNetJavaOutputDir` | Java output directory, relative to the project. Enables the Java target |
| `NexNetJavaPackage` | Java package of the generated code. Required for Java (NEXNET045 otherwise) |
| `NexNetJavaEmitRuntime` | `true` (default) or `false`: also write the Java runtime (`com/dtronix/nexnet/**`) |
| `NexNetJavaNullness` | `none` (default when the runtime is emitted) or `jspecify`: keep the JSpecify `@Nullable` annotations |
| `NexNetJavaEmitRuntimeModules` | Optional Java runtime modules to write beside the runtime: any of `android`, `kotlin`, `micrometer`, `okhttp`, separated by `;`, `,` or spaces. Default none (below) |
| `NexNetJavaScriptOutputDir` | JavaScript output directory, relative to the project. Enables the JavaScript target |
| `NexNetJavaScriptEmitRuntime` | `true` (default) or `false`: also write the JavaScript runtime (`nexnet-runtime/`) |
| `NexNetJavaScriptRuntimeImport` | `package` (default) or `relative`: how generated modules import the runtime (below) |
| `NexNetJavaScriptModuleName` | Module specifier generated code imports the runtime from. Optional; setting it turns `EmitRuntime` off by default |
| `NexNetIrOutputPath` | Where `nexnet-ir.json` is written. Default `obj/nexnet-ir.json` |

The properties are shorthand for `NexNetTarget` items, which you can also write directly. Every metadata other than
`OutputDir` is passed to the generator as an option of that target:

```xml
<ItemGroup>
  <NexNetTarget Include="java" OutputDir="../clients/chat-java/src/generated/java" Package="com.example.chat"
                Nullness="jspecify" />
  <NexNetTarget Include="javascript" OutputDir="../clients/chat-web/src/generated" RuntimeImport="relative" />
</ItemGroup>
```

An invalid option value is NEXNET045. A nexus that opts in to a language with no output directory gets NEXNET044 and
nothing is generated for that language.

### The emitted runtime

Each output directory receives the client runtime of its language next to the generated code. Both come from
the same generator, so they always match; upgrading `NexNet.Generator` rewrites both.

- **JavaScript** goes to `<OutputDir>/nexnet-runtime/`: the runtime's ES modules, a `package.json` named
  `@dtronix/nexnet` (with the `browser`/`node` conditional exports) and the `LICENSE`.
  - `RuntimeImport=package` (default): generated modules import `@dtronix/nexnet`. Add the folder as a local
    dependency, `"@dtronix/nexnet": "file:src/generated/nexnet-runtime"`, and run `npm install` (nothing is
    downloaded), or alias it in your bundler. In Node, the generated factory then gets the Node client with every Node
    transport.
  - `RuntimeImport=relative`: generated modules import `./nexnet-runtime/index.js` directly. No install step, which
    suits plain browser ES modules. The import is the platform-neutral entry, so the generated factory knows only
    WebSocket; Node code that needs the other transports uses `NodeNexusClient` from
    `./nexnet-runtime/node.js` (see the [JavaScript guide](javascript-client.md#node-transports-with-relative-imports)).
  - TypeScript: the generated code and the runtime both come with `.d.ts` files, so no `allowJs` is needed.
- **Java** goes to `<OutputDir>/com/dtronix/nexnet/**` (package `com.dtronix.nexnet`) with a `LICENSE.txt`. Add
  `<OutputDir>` as a source root. The runtime has no dependencies. With `Nullness=none` (the default) neither the
  runtime nor the generated code carries JSpecify annotations; `Nullness=jspecify` keeps them for Kotlin and NullAway
  users, who then add `compileOnly("org.jspecify:jspecify:1.0.0")`.
- **Optional Java modules** are emitted only when listed in `EmitRuntimeModules` (`NexNetJavaEmitRuntimeModules`).
  Each goes beside the core runtime in its own package (`com.dtronix.nexnet.<module>`) and needs third-party
  libraries that your build adds (the versions the modules are tested with):
  - `android`: `androidx.lifecycle:lifecycle-process:2.11.0` and `androidx.annotation:annotation:1.11.0`; compiles
    only in an Android build (`minSdk` 24);
  - `kotlin`: `org.jetbrains.kotlinx:kotlinx-coroutines-core:1.11.0`, compiled by the Kotlin Gradle plugin;
  - `okhttp`: `com.squareup.okhttp3:okhttp:5.5.1`;
  - `micrometer`: `io.micrometer:micrometer-core:1.17.2`.

  What each module adds is in the [Java guide](java-client.md#optional-modules).
- Every runtime file starts with a header line naming the NexNet version and the MIT license.
- `EmitRuntime=false` writes only the generated code. Use it when several output directories share one runtime copy
  (a JavaScript workspace package, a Java library module), and point the others at it (`ModuleName` for JavaScript).

How the runtime travels inside the generator is described in
[the internals page](../internals/generator-emitted-runtime.md).

### How the files get to disk

- The generator does no file IO. It adds `NexNet.Targets.g.cs` to the compilation: a file made only of comments that
  carries every generated file (base64, with a sha256 each) and `nexnet-ir.json`. It compiles to nothing.
- After `CoreCompile`, the `NexNetUnpackTargets` MSBuild target (imported from the package's `build/` and
  `buildTransitive/` folders) verifies the hashes and writes the files. Only files whose bytes changed are rewritten,
  each atomically, so an unchanged build leaves timestamps alone and does not trigger downstream Java/JS rebuilds.
- Files written by the previous build that are no longer generated (a removed export or type) are deleted. The list
  is kept in `obj/nexnet-targets.manifest`. `dotnet clean` deletes every file in it.
- To produce the bundle, the build turns on `EmitCompilerGeneratedFiles`, so the C# output of every source generator
  is also written under `obj/<configuration>/<tfm>/generated/`. This is harmless.
- Output is deterministic: the same sources give byte-identical files on every machine.
- Projects with several `TargetFrameworks` write the files from the first one only.
- Design-time (IDE) builds report the NEXNET diagnostics live but write nothing.

Without any `NexNetTarget`, none of this runs and the build is unchanged.

Treat the output directories as generated: the unpack target owns the files it writes, so do not edit them by hand and
do not put your own files in them. If you delete a generated file yourself, rebuild after a change or run
`dotnet clean` first to get it back. Whether to commit the output is your choice: committing it lets Java and
JavaScript developers build without the .NET SDK.

## Feature parity

Every client feature of the C# client is available in Java and JavaScript, with the same behavior, unless listed
below. The cross-language test harness runs the C# client tests against every language and transport the language
supports (see [Contributing (Java & JS)](contributing-multilang.md)). The table follows the capability flags of that
harness (`src/NexNet.Interop/ClientLanguages.cs`).

| Feature | C# | Java | JavaScript (Node) | JavaScript (browser) |
|---|---|---|---|---|
| Invocations (void, result, cancellation), client nexus methods | Yes | Yes | Yes | Yes |
| Duplex pipes, channels | Yes | Yes | Yes | Yes |
| Synchronized collections (all modes) | Yes | Yes | Yes | Yes |
| Greeting token authentication (`Authenticate`) | Yes | Yes | Yes | Yes |
| Reconnection, client pool | Yes | Yes | Yes | Yes |
| Logging, metrics instruments, snapshots | Yes | Yes | Yes | Yes |
| WebSocket `ws://` / `wss://` | Yes | Yes | Yes | Yes |
| HttpSocket `http://` / `https://` | Yes | Yes | Yes | No |
| TCP, TCP + TLS | Yes | Yes | Yes | No |
| Unix domain sockets | Yes | **No** | Yes | No |
| QUIC | Yes | **No** | **No** | No |
| `Authorization` header on the upgrade | Yes | Yes | Yes | **No** |
| Custom headers on the upgrade | Yes | Yes | Yes | **No** |
| TLS client certificate | Yes | Yes | Yes | **No** |
| Custom CA, certificate pinning | Yes | Yes | Yes | **No** |
| Server certificate (`RemoteCertificate`) | Yes | Yes | Yes | **No** |
| `AsyncLocal` flow into nexus methods | Yes | No | No | No |

The client pool's collection connector (C# `GetCollectionConnector`, Java and JavaScript `collectionConnector`), which
a relay server uses, runs on the test matrix too (`NexusCollectionPoolConnectorTests`). It behaves the same in every
language: each request for the collection returns the client the connector holds to the pool and then rents one, so a
connector holds at most one pooled client, and a request again (after a reconnection) never waits on its own rental.

### Documented exceptions and workarounds

| Exception | Workaround |
|---|---|
| **Browsers: WebSocket only.** No HttpSocket, TCP or UDS (platform limit) | Expose a WebSocket endpoint (NexNet.Asp `MapNexusWebSocket`); one server can serve every transport (see [Multiple endpoints](#serving-several-languages-from-one-server)) |
| **Browsers: no upgrade headers.** A browser WebSocket cannot send `Authorization` or other headers | Use the NexNet greeting token (`authenticate`), the canonical mechanism for every client. For ASP.NET authentication, cookies or the opt-in `AccessTokenQueryParameter` (send the token with `webSocket.queryToken`; [ASP.NET Integration: Browser Clients](asp-net-integration.md#browser-clients)); restrict `AllowedOrigins` when cookies are used |
| **Browsers: redirects.** NexNet clients never follow a redirect of the upgrade, but the browser runs the WebSocket handshake: WebKit may follow a `3xx` (Chromium and Firefox fail it), and page JavaScript cannot stop it. A query-string token (`webSocket.queryToken`) can go with it to the redirect target | Do not redirect NexNet WebSocket paths (reverse proxies, HTTP to HTTPS redirects); serve `wss://` directly. NexNet.Asp never answers an upgrade with a redirect. See [ASP.NET Integration: Do not redirect NexNet paths](asp-net-integration.md#do-not-redirect-nexnet-paths) |
| **Browsers: TLS is the platform's.** No client certificate, custom CA, pinning or access to the server certificate | Use a certificate the browser trusts; authenticate with the greeting token. The browser gets no rows for these tests in the test matrix (never a silent pass), and they are documented parity exceptions |
| **Browsers: background tabs** throttle timers | The runtime runs its keepalive in a Web Worker (needs `worker-src blob:` under a Content-Security-Policy) and reconnects on `visibilitychange`/`online`. See the [JavaScript guide](javascript-client.md#browser-notes) |
| **Java has no Unix domain sockets** (Android and Java 11 portability) | Use TCP on loopback, or TLS |
| **QUIC is .NET-only** (msquic) | Use TCP + TLS or WSS for Java and JavaScript clients, on another endpoint of the same server |
| **`AsyncLocal` flow is platform-specific.** C# nexus methods run in the execution context of the code that connected ([Hub Invocations: Execution Context](hub-invocations.md#execution-context)); Java and JavaScript have no equivalent | Pass context explicitly (fields of your client nexus, method arguments). In Node, wrap your own code in `AsyncLocalStorage` if needed |
| **Reconnection defaults.** C# and Java: off unless a policy is set. JavaScript: off in Node, the default policy in browsers | Set `reconnectionPolicy` explicitly (`null` turns it off) |
| **JS `void` client methods are awaited.** The JS runtime holds the execution slot until a fire-and-forget method settles; C# and Java free it at once | Keep `void` client methods short, or start long work without awaiting it |
| **Java `decimal` negative zero.** `BigDecimal` has no `-0`; `-0m` reads as `0` (equal in .NET) | Use `NexusDecimal` (`Formatters.NEXUS_DECIMAL`) when the exact bits matter |
| **JS `Half` NaN payload.** Any binary16 NaN reads as `NaN` and is written back as `0x7E00` | None needed for normal use: it is still NaN in .NET |
| **Java generic unions** are emitted without type relationships to their cases | Cast to the case class |
| **Java `equals` on a list holding `Stack`/`Queue` values** compares those deques by reference (`ArrayDeque` has no value equality) | Avoid them as `NexusList` items or compare contents yourself |
| **Default values of members an older peer did not send.** C# runs the member initializer. Java and JavaScript apply initializers that are simple constants; any other initializer gives `null`/`0`/`undefined` | Use constant initializers on members added in a later version |
| **JS `Map`/`Set` keys compare by identity** | Use primitive keys (`string`, `number`, `bigint`); object keys are flagged by NEXNET046 |
| **Queued frames at disconnect.** Under backpressure, Java and JavaScript send frames already queued when `disconnect()` is called; C# may drop them | None needed |

### Parity exceptions

The cross-language test matrix runs every client-facing test on every language and transport that applies to it. It
generates only applicable rows: a language that lacks a transport or a capability the test needs (from the table in
`src/NexNet.Interop/ClientLanguages.cs`, which a language host's hello can only narrow) gets no row for it, and a
language that gains one gets its rows from the same table. A language's variants add their own rows: the Java client
also runs every `WebSocket` and `WebSocketTls` row on the OkHttp connector of `nexnet-client-okhttp`
(`java/WebSocket(okhttp)`), in the same CI job as the built-in connector. The list below is every intentional
exception: a test row, or a test assertion, that does not run on some language, with the reason. Anything else that
does not run on a language is a gap to fix, not an exception.

Each CI interop job writes a coverage report to its step summary and uploads it as the `matrix-coverage-<lang>`
artifact (JSON): per language and variant, the rows run, passed, failed and skipped, and the combinations no row was
generated for, by reason. In CI a row may be skipped only for a reason that cites the card that will close the gap
(`K60: ...`); any other skip fails the job. Locally, rows may still skip for the environment (no msquic). Set
`NEXNET_MATRIX_REPORT=<dir>` to write the same report from a local run.

The matrix tests in this list are also listed, with the same reasons, in
`src/NexNet.IntegrationTests/Interop/ClientParityExceptions.cs`. The parity audit (`ClientParityTests`) fails when a
matrix test has no runnable row on a language, checks part of its behavior on the C# client only (a branch on
`ClientLanguages.CSharp` or on the driver type, such as `client as InProcClientDriver<,>`), or branches on any other
language name (`target.Language == ClientLanguages.Java`, an `IsBrowser(target)` helper) and checks something else on
it, without an entry there, and when an entry is no longer needed. One helper's driver-type check is exempt there,
with its reason: `MatrixTestBase.AssertNotOutstanding` asserts on every language that the client holds no invocation
ID, and only adds a by-ID check of the C# client's ID table, so it narrows no language. Reading a harness value that
only the C# client fills (`RemoteListState.IsReadOnly`, null on the other languages) counts as a C#-only check too,
so a `??` fallback on it cannot hide a gap. The audit also fails when an entry there (or an exempt helper) is not
named, as `Method` or `Class.Method`, in this section. That file also lists, with the card that closes it, any gap that is
still being fixed; those are not exceptions and are not listed here. The transport rows below (UDS, QUIC, the browser
transports) are not audit entries: a test that also runs on another transport has a runnable row. The C#-only and
language-idiomatic APIs are not matrix tests.

**Platform limits** (the language or platform cannot do it):

| Exception | Languages | Reason |
|---|---|---|
| No Unix domain socket rows | Java | No portable UDS client API down to Android API 24 and Java 11 (`UnixDomainSocketAddress` is Java 16+). Java runs the same tests on TCP |
| No QUIC rows | Java, JavaScript | QUIC uses msquic, which is .NET-only |
| No TCP, TCP + TLS, UDS or HttpSocket (`http://`, `https://`) rows | Browser | Page JavaScript has no raw sockets, and HttpSocket is an HTTP/1.1 upgrade to `nexnet-httpsockets` that neither `fetch` nor `WebSocket` can make. Browsers run the same tests on WebSocket and WebSocket over TLS. `HttpSocket_SuccessStatusOtherThan101_FailsConnect` tests the HttpSocket upgrade only, so it has no browser row; the WebSocket rows of `UpgradeRedirect_IsNotFollowed_FailsWithProtocolError` cover the browser's upgrade handling |
| Tests that need an `Authorization` (or other) upgrade header (`ClientAuthenticationSucceeds`, `ClientAuthenticationFailsWithBadAuth`, `ServerIgnoresAuthenticationWhenNotEnabled`, `ClientDoesNotConnectWhenServerSelectsBadAuthScheme`, `AuthorizationHeaderWinsOverQueryStringToken`, `CustomUpgradeHeaderReachesTheServer`, `AuthorizationHeaderWinsOverUpgradeHeaderAuthorization`, `RefusedUpgradeHeaderFailsClientCreation`, `OnAuthenticate_SeesAspPrincipalClaims`, `NexusAuthenticationStillDecides_WithAspPrincipal`) | Browser | The browser `WebSocket` API cannot set request headers. The query-string token tests (`webSocket.queryToken`) and the greeting token cover browser authentication |
| Tests that need a client certificate, a custom CA, certificate pinning, the server certificate, or turning certificate verification off (`InsecureSkipVerify`): `TrustedTestCa_AcceptsServerCertificate`, `TrustedTestCa_RejectsWrongHostName`, `NoClientCertificate_ServerSessionHasNone`, `UntrustedServerCertificate_RejectedWithoutCallback`, `ClientCertificate_ReachesServerSession`, `ClientSession_SeesServerCertificate`, `PinnedFingerprint_AcceptsServerCertificate`, `PinnedFingerprint_RejectsWrongCertificate`, `InsecureSkipVerify_AcceptsACertificateTheClientDoesNotTrust` | Browser | The browser does the TLS and exposes none of it to page JavaScript. The WSS rows prove the default-trust path |
| `NonAspTransport_SessionUserIsNull` | Browser | It tests the TCP, TLS and UDS transports, which browsers do not have |
| A refused WebSocket upgrade (redirect) is `InternalError`, not `ProtocolError`, and WebKit may follow the redirect (`UpgradeRedirect_IsNotFollowed_FailsWithProtocolError`) | Browser | The browser `WebSocket` API reports a failed upgrade only as a generic error, with no status. WebKit's `WebSocket` (libsoup, as on Linux) follows the redirect itself before page JavaScript sees anything, so the redirect target may get one connection on a WebKit row. That the connect fails with no session, after one request to the endpoint, is asserted on every language, and that the redirect is not followed on every language but WebKit |
| A text message closes the WebSocket with 1000, not 1003 (`WebSocket_TextMessage_ClientClosesWith1003`) | Browser | Page JavaScript may only send close code 1000 or 3000–4999. That the client refuses the session is asserted on every language |
| The remote endpoint is empty while connected (`RemoteEndpoint_IsTheServerEndpointWhileConnected`) | Browser | The browser does not expose the peer address of a WebSocket |
| Content headers (`Content-Type`, `Expires`, ...) in custom upgrade headers are refused (`ArgumentException`) | C# | .NET cannot put a content header on the upgrade request: `HttpRequestHeaders` refuses them, and the upgrade is a GET with no body. Java and JavaScript validate upgrade headers the same way otherwise, and send these. Not a matrix gap: no test sends a content header |

**By design** (C#-only behavior or API, or a counterpart that is idiomatic and unit-tested in its own language):

| Exception | Languages | Reason |
|---|---|---|
| `INexusList.CopyTo` tests (`CopyTo_CopiesAllElements`, `CopyTo_NullArray_ThrowsArgumentNullException`, `CopyTo_SmallArray_ThrowsArgumentException`) | Java, JavaScript | `CopyTo` is `ICollection<T>` API. JavaScript has `toArray()` and Java a read-only `java.util.List` snapshot |
| `Server_ThrowsWhenPassingPipeFromWrongNexus` | Java, JavaScript | It tests the C# server proxy; the client plays no part |
| The server-side pipe branch of `Client_ThrowsWhenPassingPipeFromWrongNexus` | Java, JavaScript | Only an in-process C# test can hold the server's pipe object and pass it to a client proxy |
| The remote exception type name is asserted on C# only (`TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage`) | Java, JavaScript | Exception type names are language-specific; the error code and message are asserted on every language |
| The count of changes a list received before its snapshot is asserted on C# only (`ClientsJoiningMidStream_ReceiveSnapshotBeforeChanges`) | Java, JavaScript | It is a C# runtime test field that no harness operation exposes. Java and JavaScript buffer such changes the same way, and the test asserts every language's list state and final contents, which a change applied before the snapshot would corrupt |
| `INexusList.IsReadOnly` is asserted on C# only (`IsReadOnly_IsFalseOnBidirectional`, `IsReadOnly_IsTrueOnServerToClient`, `IsReadOnly_IsTrueOnRelay`) | Java, JavaScript | `ICollection<T>.IsReadOnly` is C# API; Java and JavaScript expose the list's `mode`, which these tests assert on every language. The `ClientWrites_AreRefusedOnServerToClient`, `ClientWrites_AreRefusedOnRelay` and `ClientWrites_AreAcceptedOnBidirectional` matrix tests check the behavior behind the flag on every language: a client write is refused (`InvalidOperation`) in ServerToClient and Relay mode and accepted in BiDirectional mode |
| A browser client reconnects with no reconnection settings; the others do not (`DefaultReconnection_OnlyABrowserClientReconnects`) | Browser | The JavaScript client turns its default reconnection policy on in a browser only; C#, Node and Java have none by default |
| Only the browser rows exercise the WebSocket origin allow-list (`Origin_NotInTheAllowList_RefusesOnlyABrowser`, `Origin_InTheAllowList_Connects`) | Browser | Only a browser sends an `Origin` header; the other clients send none and are always allowed |
| The Java rows record metrics through the Micrometer module (`SessionLifecycle_Measured_AlsoThroughMicrometer`) | Java | Covers the `-micrometer` module; the assertions are the same as for the native recorder |
| C# API semantics: `ValueTask` consumption, `AsyncLocal` and execution-context flow, the C# pool's nexus factory, a custom transport's `IsSecureTransport` override, setting both `Authenticate` and `AuthenticateAsync` | Java, JavaScript | C#-only API. These are C# tests, not matrix tests |
| Java executors (`callbackExecutor`, `invocationExecutor`), pipe `InputStream`/`OutputStream` adapters, Kotlin coroutine and flow wrappers | Java only | Java-idiomatic API; unit-tested in the Java suite |
| JavaScript `EventTarget` events, `AbortSignal` cancellation, Web Streams pipes | JavaScript only | JavaScript-idiomatic API; the matrix tests the behavior behind them through the harness: cancellation of invocations, pipe forwards, channel reads, list enables, pool rents and connects (a canceled connect, and a connect signal aborted after the connect, which must not stop reconnection) |
| Reconnection controller: Java `NexusClient.reconnection()` (`ReconnectController`, with `ReconnectListener`) and JavaScript `client.reconnection` (`ReconnectController`, an `EventTarget`) | C# | The C# client reconnects through its `ReconnectionPolicy` and reports it as state changes (`Reconnecting`) and the client nexus's `OnReconnecting`; it has no separate controller object. The reconnection behavior is tested on every language; the controller objects are unit-tested in their own language |
| A reconnected session lost before the reconnection loop that opened it has ended starts a new loop (`NexusClientTests_ReconnectEdgeCases.ReconnectedSession_LostBeforeTheLoopEnds_Reconnects`, a C# test, not a matrix test) | Java, JavaScript | The race needs the session to drop inside a window in the runtime's own loop, which only an in-runtime hold hook can open; the harness has no hold point there. C# holds the loop with the internal `NexusClient.InternalOnReconnected` hook. Java fixed the same race (it kept `ReconnectController` running until `finish()` ran on the serial executor) and holds it with the package-private `NexusClient.afterSessionLost` hook in `NexusClientReconnectTest.aReconnectedSessionLostBeforeTheLoopEndsIsReconnected` and `ReconnectControllerTest.aSessionLostBeforeTheLoopEndsStartsANewLoopWhenItEnds`. JavaScript has no such window: from the attempt's handshake completing to `ReconnectController` clearing its running flag nothing waits on user code or I/O (`onConnected` is queued, not awaited), and a lost session is reported from a transport callback, which runs only after those microtasks, so the loss always starts a new loop |
| Query-string token: `queryToken` option in Java (`ClientConfig.Builder.queryToken`) and JavaScript (`webSocket.queryToken`, `httpSocket.queryToken`) | C# | The C# client takes the token in the URL (`wss://host/nexus?access_token=…`); the server sees the same request |
| Android module (`NexusConnectionKeeper`, `AndroidNexusLogger`, `NexusJavaTime`) | Java (Android) | Runs as a device smoke test on an emulator (connect, invoke, pipe echo), not on the matrix; the lifecycle logic is unit-tested |

Firefox and WebKit run the browser rows weekly; every pull request runs them in Chromium.

## Type mapping

This table is normative: the generator maps every type an export uses as shown, and reports a type it cannot map as
NEXNET040.

General rules:

- **The declared C# type decides the wire encoding**, never the runtime value.
- **Narrow integers are range-checked on write.** JavaScript also rejects non-integral numbers.
- **Struct values are never null on the wire**: a null struct fails on write and on read.
- **`[NexusObject]` types** become generated classes with value equality (`equals`/`hashCode` in Java, an
  `equals` method in JavaScript). Struct copy semantics are not reproduced.

| .NET | Java | JavaScript |
|---|---|---|
| `bool` | `boolean` | `boolean` |
| `sbyte`, `short`, `int` | `byte`, `short`, `int` | `number` (checked) |
| `byte`, `ushort` | `int` (checked) | `number` (checked) |
| `uint` | `long` (checked) | `number` (checked) |
| `long` | `long` | `bigint`; `number` (checked) with `[NexusJsNumber]` |
| `ulong` | `long` (raw bits; `UnsignedLongs` helpers) | `bigint`; `number` (checked) with `[NexusJsNumber]` |
| `float`, `double` | `float`, `double` | `number` |
| `Half` | `float` | `number` |
| `char` | `char` | `string` (one UTF-16 code unit) |
| `string` | `String` | `string` |
| `decimal` | `java.math.BigDecimal` | `NexusDecimal` |
| `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly` | `NexusDateTime`, `NexusDateTimeOffset`, `NexusTimeSpan`, `NexusDateOnly`, `NexusTimeOnly` (ticks; epoch converters, no `java.time` needed) | `NexusDateTime`, `NexusDateTimeOffset`, `NexusTimeSpan`, `NexusDateOnly`, `NexusTimeOnly` (`bigint` ticks; `Date` converters) |
| `Guid` | `java.util.UUID` | `string` (lower case) |
| `BigInteger` | `java.math.BigInteger` | `bigint` |
| `Uri` | `String` | `string` |
| `Version` | `NexusVersion` | `Version` (`NexusVersion`) |
| `byte[]`, `Memory<byte>`, … | `byte[]` | `Uint8Array` |
| Primitive arrays written as a whole (`int[]`, `List<long>`, …) | `short[]`, `int[]`, `long[]`, `float[]`, `double[]`, `char[]` (`ushort`, `char`); unsigned as raw bits; `Half[]` as `float[]` | `Int8Array` … `Float64Array`, `BigInt64Array`/`BigUint64Array`; `char`/`ushort` as `Uint16Array`; `Half[]` as `Float32Array` |
| `T[]`, `List<T>`, `IList<T>`, … | `java.util.List<T>` | `T[]` |
| `HashSet<T>` | `java.util.Set<T>` | `Set<T>` |
| `Dictionary<K, V>` | `java.util.Map<K, V>` | `Map<K, V>` (never a plain object) |
| `Stack<T>`, `Queue<T>` | `java.util.ArrayDeque<T>` | `T[]` (a stack's index 0 is its top) |
| `ValueTuple`, `Tuple` (1 to 7 elements) | `Tuple1` … `Tuple7` | `[a, b, …]` |
| `KeyValuePair<K, V>` | `java.util.Map.Entry<K, V>` | `[key, value]` |
| `Nullable<T>`, nullable references | boxed type (`@Nullable` with `Nullness=jspecify`) | `T \| null` |
| `enum` | generated open value class (keeps unknown and `[Flags]` values) | `number` constants (`bigint` for 64-bit enums) |
| `[NexusObject]` class, struct, record | generated class | generated class + `.d.ts` |
| Union (`[NexusUnion]`) | abstract base class and case classes | base class and case classes |
| Generic `[NexusObject]` | generic class; formatter factory taking the argument formatters | the same |
| `[NexusFormatter]` user type | your class, mapped with `NexusTargetFormatter` (below) | the same |
| `INexusDuplexPipe` | `NexusDuplexPipe` | `NexusDuplexPipe` (Web Streams) |
| `INexusDuplexChannel<T>` | `NexusDuplexChannel<T>` | `NexusDuplexChannel<T>` (async iterable reader) |
| `INexusList<T>` | `NexusList<T>` (a read-only `java.util.List`) | `NexusList<T>` |
| `CancellationToken` | `CancellationToken` (runtime) | `{ signal }` (`AbortSignal`) |
| `ValueTask`, `ValueTask<T>` | `CompletableFuture<Void>`, `CompletableFuture<T>` | `Promise<void>`, `Promise<T>` |

### `[NexusJsNumber]`: `number` instead of `bigint`

C# `long` and `ulong` are `bigint` in JavaScript by default, because a `number` holds integers exactly only up to
±(2^53 − 1). `[NexusJsNumber]` (namespace `NexNet.Serialization`) maps them to a checked `number` instead:

```csharp
[NexusObject]
public partial class Order
{
    [NexusKey(0), NexusJsNumber] public long Id { get; set; }
}

public interface IOrderServer
{
    [return: NexusJsNumber] ValueTask<long> Count([NexusJsNumber] long since);
}

// Or every long/ulong of the assembly's exports and types:
[assembly: NexusJsNumber]
```

- Applies on a `[NexusObject]` member, a nexus method parameter or return value, a channel parameter, a collection
  property, or the assembly. It covers the `long`/`ulong` values inside nullables, sequences, sets, dictionaries,
  tuples and generic type arguments of that place.
- Writing or reading a value beyond ±(2^53 − 1) (or a negative `ulong`) fails.
- Primitive arrays written as a whole (`long[]`, `List<long>`) stay `BigInt64Array`/`BigUint64Array`, and enums
  with a 64-bit underlying type stay `bigint`.
- The wire format, the C# and Java APIs and the nexus hashes are unchanged.
- On a member that is not 64-bit the attribute is silently ignored: there is no diagnostic for it.

## Types with a custom formatter

A type serialized by a user formatter (`[assembly: NexusFormatter<TFormatter, T>]`) has no language-neutral form.
Map it to a hand-written formatter in each target language:

```csharp
using NexNet.Serialization;

[assembly: NexusFormatter<MoneyFormatter, Money>]
[assembly: NexusTargetFormatter<Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
[assembly: NexusTargetFormatter<Money>(NexusTargets.JavaScript, "./money-formatter.js#MoneyFormatter")]
```

- Java: the fully qualified name of a class with a public no-argument constructor that implements
  `com.dtronix.nexnet.serialization.Formatter<T>` (`write(MsgPackWriter, T)` and `read(MsgPackReader)`) for
  your Java representation of the type. Generated signatures type such values as `Object`.
- JavaScript: `<module>#<export>` (or `<module>` for its default export): a module path relative to the output
  directory and an exported `{ write(writer, value), read(reader) }` formatter. Generated `.d.ts` type such
  values as `unknown`.
- The native formatter must write exactly the bytes the C# formatter writes. Declare one mapping per (type, language);
  combined flags apply the same name to each language. A type with no mapping for an opted-in language is NEXNET040.

NEXNET048 reports an invalid mapping: a `None` or undefined target, an empty formatter name, or a second mapping for the
same type and language.

## Serving several languages from one server

One `NexusServer` can listen on several transports at once, so C#, Java, Node and browser clients all connect to the
same server and see each other in broadcasts, groups and collections: for example TCP + TLS for Java and Node services,
WebSocket over HTTPS for browsers and QUIC for .NET clients. See [Transports: Multiple Endpoints](transports.md#multiple-endpoints)
and [ASP.NET Integration: Multiple Endpoints](asp-net-integration.md#multiple-endpoints). A server nexus can tell the
endpoints apart through `Context.Endpoint`.

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| NEXNET040 | Error | A type used by an export cannot be represented in an opted-in language |
| NEXNET041 | Error | A name collides in an opted-in language, or two exports have the same namespace and name (give each `NexusClientExport` a distinct `Name`) |
| NEXNET042 | Error | A nexus interface declares two methods with the same name, including one inherited from another interface. Applies to every nexus, exported or not; a `NexusClientExport` with duplicate names is skipped |
| NEXNET043 | Error | `Targets` is set on a client nexus |
| NEXNET044 | Warning | A nexus opts in to a language that has no output directory (`NexNet<Lang>OutputDir` or a `NexNetTarget` with `OutputDir`); nothing is generated for it |
| NEXNET045 | Error | Invalid target configuration: an unknown target, a missing or invalid option (such as `NexNetJavaPackage`, `EmitRuntime`, `RuntimeImport`, `Nullness` or `EmitRuntimeModules`), an optional Java module listed without the emitted runtime, or an unknown `NexusTargets` value |
| NEXNET046 | Warning | A set element or dictionary key compares by reference in an opted-in language, so a lookup with an equal value does not find the entry |
| NEXNET047 | Error | Two exports or types would write the same output file (paths compared ignoring case), or a generated file would overwrite a runtime file |
| NEXNET048 | Error | Invalid `NexusTargetFormatter` |

The unpack target reports these build messages:

| Code | Severity | Meaning |
|---|---|---|
| NEXNETB001 | Warning | A `NexNetTarget` is configured but the generator produced no bundle (it is not referenced as an analyzer) |
| NEXNETB002 | Error | The bundle is corrupt, from another version of the build files, or has an invalid path |
| NEXNETB003 | Error | The bundle has files for a target with no `OutputDir` |
| NEXNETB004 | Error | A generated file cannot be read or written (a warning when a stale file cannot be deleted) |
| NEXNETB005 | Warning | A `NexNetTarget` is declared twice; the first declaration is used |
