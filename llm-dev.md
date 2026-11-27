# NexNet LLM Development Reference

Token-condensed architecture reference for LLM navigation and modification.

---

## Project Structure

```
src/
├── NexNet/                    # Core library
├── NexNet.Generator/          # Roslyn source generator (C#; Java/JS targets: Ir/, Targets/, Runtime/, Bundle/, build/)
├── NexNet.Quic/              # QUIC transport
├── NexNet.Asp/               # ASP.NET Core integration
├── NexNet.Inspector.Core/    # Inspector: framing, reassembly, .nntl format, schema decoding, capture sessions, compare, text renderer, findings
├── NexNet.Inspector.Tap/     # Inspector tap: opt-in capture, recorder, inspector endpoint (§3.3)
├── NexNet.Inspector.Cli/     # nexnet-tap CLI, Native AOT (§3.4)
├── NexNet.Inspector.Sample/  # Chat sample + fixture generator (§3.5)
├── NexNet.Inspector.App/     # nexnet-inspector desktop app, Avalonia, Native AOT (§3.6)
├── NexNet.Inspector.Tests/   # Core/Tap/CLI/compare tests; App.Tests: the app's tests + capture harness
├── NexNet.IntegrationTests/  # Integration tests (TUnit, parallel; [ClientMatrix] rows run per client language)
├── NexNet.TestNexus/         # Shared test nexuses + interop type zoo (not packaged); writes the Java/JS host clients
├── NexNet.Generator.Tests/   # Generator tests (incl. Java/JS emitter snapshots and SDK-gated compile checks)
├── NexNet.Serialization.Tests/ # Serializer, protocol message and fuzz smoke tests
├── NexNet.Fuzz/              # SharpFuzz harnesses (libFuzzer), smoke runner, regression corpora
├── NexNet.Interop/           # Cross-language harness: IClientUnderTest, drivers, language host pool (test-only)
├── NexNet.Interop.Abstractions/ # Harness types generated scaffolding uses (typed JSON, remote descriptors)
├── NexNet.Interop.Tests/     # Harness unit tests (control protocol, host supervision)
├── NexNet.InteropServer/     # Test server exposing every endpoint kind at once
├── NexNet.GoldenVectors/     # Writes tests/golden/*.jsonl (byte vectors every language checks)
├── NexNet.Generator.IntegrationSample/ # Builds a sample nexus through the real build targets
├── NexNetBenchmarks/         # BenchmarkDotNet
└── Samples/                  # NexNetDemo, Asp and Native AOT samples
clients/
├── js/                       # npm workspace: packages/nexnet (@dtronix/nexnet runtime), interop-host-node, interop-host-browser
└── java/                     # Gradle build: nexnet-client (com.dtronix.nexnet runtime), optional -android/-kotlin/-okhttp/-micrometer, interop-host, build-logic
tests/golden/                 # Golden vectors (checked in; format in tests/golden/README.md)
```

Release: follow [llm-release.md](llm-release.md).

Build: `dotnet build src -c Release`. `src/Directory.Build.props` sets `TreatWarningsAsErrors` for every project under
`src/` (libraries, generator, tests, fuzz, benchmarks, samples), NuGet audit advisories (NU1901-NU1904) and warnings in
generated code included. Fix the cause of a warning; do not add `NoWarn` or `WarningsNotAsErrors`.
Test (all three suites must pass):
```
dotnet test src/NexNet.Serialization.Tests -c Release
dotnet test src/NexNet.Generator.Tests -c Release
dotnet test src/NexNet.IntegrationTests -c Release
```
Tests run on Microsoft.Testing.Platform (the root `global.json` selects it), so options after the project path go to the
test app. Every suite is TUnit; filter with `--treenode-filter`:
- `dotnet test <proj> -c Release --treenode-filter "/*/*/*/TestName"` (class: `--treenode-filter "/*/*/ClassName/*"`).
  The path is `/assembly/namespace/class/test`. `--filter` (VSTest syntax) is not supported.

List tests: `dotnet test <proj> -c Release --list-tests`.

IntegrationTests run in parallel (see 4.4). `NEXNET_TEST_PARALLEL=<n>` overrides how many tests run at once (`1` runs
the suite serially, which helps when chasing a timing failure).

Cross-language rows (`[ClientMatrix]` tests, `src/NexNet.Interop/`): each row is one client language times one transport
and carries the TUnit properties `Lang` (`csharp`, `node`, `java`, `browser-chromium`, `browser-firefox`,
`browser-webkit`) and `Transport` (a `ClientTransport` name such as `WebSocketTls`). Select rows by property:
- `--treenode-filter "/*/*/*/*[Lang=node]"` runs one language's rows; `[Transport=Tcp]` one transport's.
- `NEXNET_TEST_LANGS` (comma list) picks the languages. `csharp` (in process) always runs first; `browser` means
  `browser-chromium`, `browsers` all three. Unset: only `csharp`, and no Gradle/npm is ever invoked.
- `NEXNET_TEST_TRANSPORTS` picks the transports of tests that name none of their own. Unset: HttpSocketTls and
  WebSocketTls. `all`: every transport the language supports. A comma list of `ClientTransport` names
  (case-insensitive) is used for every language; a transport a language lacks is a visible skip on its row, and an
  unknown name is an error that lists the valid names.
- A filter that selects no test exits with code 8; add `--ignore-exit-code 8` when an empty selection is expected.

---

## 1. Core Library (src/NexNet/)

### 1.1 Entry Points

- `NexusClient.cs` - Generic client managing connection, reconnection, proxy access, ping/timeout
- `NexusServer.cs` - Generic server with Listener/Receiver modes, rate limiting, session management. Multi-endpoint: private `ServerEndpoint` list (primary first, named after its `TransportName`); public `AddListener`/`AddExternalEndpoint` (before start; validated: names, TCP ports, UDS/ASP paths, `Time`, `RateLimiting`), `Endpoints`. `TryAdmitTransport` builds each session from its endpoint: `Configs` = endpoint config, logger `SV{id}/{name}` (primary: `SV{id}`), `MetricsTags` with `nexnet.endpoint`, `Endpoint` (`NexusEndpointInfo`, `IsSecure` overridden per ASP request). Watchdog ticks at min(Timeout)/4 and checks each session against its own `Config.Timeout`. Start throws when the primary sets `Authenticate` and an endpoint does not
- `NexusEndpointInfo.cs` - Public record (Name, Transport, IsSecure, Path); `Context.Endpoint` on server sessions (set in the `NexusSession` constructor from `NexusSessionConfigurations.Endpoint`)
- `NexusClientPool.cs` - Connection pool with health checking, idle timeout, semaphore-controlled rental

### 1.2 Attributes (Source Generator Markers)

- `NexusAttribute.cs` - `[Nexus<TNexus,TProxy>]` marks classes for generation; NexusType=Server/Client
- `NexusMethodAttribute.cs` - Optional `[NexusMethod(id)]` for manual method ID assignment
- `NexusVersionAttribute.cs` - `[NexusVersion]` enables interface versioning with hash validation
- `NexusAuthorizeAttribute.cs` - `[NexusAuthorize<TPermission>(...)]` marks methods/collections for authorization; `CacheDurationSeconds` property (-1=server config, 0=never, >0=override)
- `AuthorizeResult.cs` - Enum: Allowed, Unauthorized, Disconnect

### 1.3 Invocation Layer (src/NexNet/Invocation/)

**Base Classes:**
- `NexusBase.cs` - Common base; implements IMethodInvoker; cancellation sources (calls with a result: pooled source keyed by invocation ID, canceled by `InvocationCancellation`; fire-and-forget: one shared source per session, canceled on disconnect), pipe registration (frees the invocation's execution slot), pooling
- `ServerNexusBase.cs` - Server base; OnAuthenticate, OnNexusInitialize, OnAuthorize virtuals; CheckAuthorization with ConcurrentDictionary cache; InvalidateAuthorizationCache() overloads
- `ClientNexusBase.cs` - Client base; OnReconnecting virtual

**Contexts:**
- `SessionContext.cs` - Base context with session info
- `ServerSessionContext.cs` - Server context with session ID, client identity
- `ClientSessionContext.cs` - Client context with server proxy
- `LocalSessionContext.cs` - Local session registry and key-value store

**Proxy Infrastructure:**
- `ProxyInvocationBase.cs` - Base for generated proxies; Configure() sets session/mode; supports All/Client/Group modes. A call that waits for its result is not `async`: it rents a `RegisteredInvocationState`, starts the send and returns a `ValueTask` backed by the state; only an asynchronous send or client lookup goes through an `async` slow path
- `ProxyUnauthorizedException.cs` - Thrown client-side when server denies method invocation
- `IProxyBase.cs` - Interface for proxy client selection (All, Client(id), Group(name), etc.)

**Routing & Registry:**
- `IInvocationRouter.cs` - Routes invocations to target sessions
- `LocalInvocationRouter.cs` - Single-server routing. Two passes: `TryWriteInvocation` to every target without waiting (a single-frame invocation is encoded once with `MessageFrame` and copied), then the busy sessions are sent to concurrently (at most 256 waiting) within one `ProgressTimeout`; sessions still without credit are skipped
- `LocalSessionRegistry.cs` - ConcurrentDictionary<long, INexusSession> session tracking
- `LocalGroupRegistry.cs` - Group membership with LocalSessionGroup management
- `NexusCollectionManager.cs` - Manages synchronized collections per nexus
- `SessionStore.cs` - Per-session ConcurrentDictionary<string, object> for custom data

### 1.4 Messages (src/NexNet/Messages/)

**Protocol:**
- `MessageType.cs` - Enum: Ping, Disconnect variants (20-37 incl. FlowControl 35, Stalled 37; 36 unassigned), DuplexPipeWrite (50), PipeWindowUpdate (51), Greetings (100-105), Invocation (110-112), MessageStart/Chunk/Abort (113-115), InvocationCredit (116)
- `IInvocationMessage.cs` - InvocationId, MethodId, Flags, Arguments; MaxArgumentSize=65,526 bytes is the largest argument sent in one frame (larger ones are streamed)
- `ClientGreetingMessage.cs` - Client handshake: protocol version, nexus hash, auth token, MaxMessageSize, InvocationCredits
- `ServerGreetingMessage.cs` - Server response: session ID, server nexus hash, MaxMessageSize, InvocationCredits
- `LargeMessageMessages.cs` - MessageStart/MessageAbort for arguments and results larger than one frame (see `Internals/LargeMessageReceiver.cs`)
- `InvocationMessage.cs` - Remote method invocation payload; `Serialize` goes through `InvocationBody`
- `InvocationBody.cs` - Readonly snapshot of an invocation's fields: exact body `Length` and `Write`, the only invocation body encoder
- `InvocationCreditMessage.cs` - Returns invocation credit to the peer (type 116)
- `InvocationResultMessage.cs` - Return value, exception, Unauthorized or Canceled from invocation; StateType: Unset, CompletedResult, Exception, Unauthorized, Canceled
- `InvocationCancellationMessage.cs` - Cancel ongoing invocation
- `DuplexPipeUpdateStateMessage.cs` - Pipe state changes

All message bodies are hand-written MessagePack (`Serialize(ref MsgPackWriter)` / `Deserialize(ref MsgPackReader)`): fixed-length
arrays with exact element counts, no trailing bytes. User values inside them (arguments, results) are "embedded values":
exactly one inline MessagePack value. Collection sync messages live in `Collections/NexusCollectionMessage.cs` and
`Collections/Lists/NexusCollectionListMessages.cs`. Exact byte layouts: [wire protocol spec](docs/internals/protocol-specification.md).

### 1.5 Session Management (src/NexNet/Internals/)

**Core Session:**
- `NexusSession.cs` - Central lifecycle: protocol, framing, routing; NnP protocol header [N][n][P][0x14][3 reserved][Version=1] (`_protocolHeader`)
  - Received invocations: `DispatchInvocations` runs each on a pooled `InvocationWorkItem` (`IThreadPoolWorkItem`, local queue) inside the ExecutionContext captured when `StartReadAsync` begins (pinned by `InvocationExecutionContextTests`)
- `NexusSession.Sending.cs` - Write logic serialized by `AsyncMutex`; invocations are written header-first straight to the output (`InvocationBody`), others through a staging buffer; streams bodies over one frame; `TryWriteInvocation` writes without waiting
- `NexusSession.Receiving.cs` - Read loop and message dispatch; `ConfirmProtocol` rejects wrong magic, non-zero reserved bytes or another version with `ProtocolError`
- `PayloadSerializer.cs` - Serializes user payload values through `NexusFormatterRegistry` for runtime-typed paths (results, collections, broadcasts)
- `NexusSessionConfigurations.cs` - Immutable session config passed at creation
- `MessageHeader.cs` - Mutable struct: Type + BodyLength for framing
- `RegisteredInvocationState.cs` - Pending call awaiting its result: `IValueTaskSource` (void) and `RegisteredInvocationState<T>` (`IValueTaskSource<T>`), pooled statically per result type. `GetResult` maps the outcome (result, remote exception, Unauthorized, Canceled) and returns the state to its pool, so the proxy's `ValueTask` may be consumed once; blocking on an unfinished call waits on a lazily created event. Completion is claimed atomically and unregisters the caller's token
- `SessionControlSender.cs` - Sends control replies off the read loop, coalesced: pongs, pipe window grants and close acknowledgements, large-message aborts, invocation credit
- `CreditGate.cs` - Outbound invocation credits: `TryTake` (lock-free), FIFO `WaitAsync(timeout, token)` -> `CreditWaitResult` (Acquired, TimedOut, Closed) over pooled `IValueTaskSource` waiters, `Release(n)` serves waiters first, `Close()` on disconnect. A waiter whose timer or cancellation callback may still run is not reused
- `SessionBudget.cs` - Per-session receive memory reservations (pipe windows, invocation queue, large values); FIFO `ReserveAsync`
- `LargeMessageReceiver.cs` - Reassembles streamed arguments/results (MessageStart/Chunk), reserving their size from the budget first
- `PreAuthFailureLog.cs` - Aggregated warning for sessions that fail before authentication
- `MessageFrame.cs` - Encodes a complete frame outside a session (pre-encoded broadcast frames)
- `InvocationWriteResult.cs` - Result of `INexusSession.TryWriteInvocation`: Written (committed to the output), Busy (would wait), Failed
- `IInvocationSlot.cs` - Execution slot an invocation holds; released when its pipe registers
- `ISessionTransportView.cs` - Test seam: the reader/writer a session uses and its transport
- `Pipelines/TapPipeReader.cs` / `Pipelines/TapPipeWriter.cs` - Transport tap wrappers (see 1.14), installed in the session constructor only when `ConfigBase.Tap` returns a per-session tap; otherwise `_pipeInput`/`_pipeOutput` are the transport's own instances

**Internal State Flags:** ProtocolConfirmed, InitialClientGreetingReceived, InitialServerGreetingReceived, NexusCompletedConnection, ReconnectingInProgress

### 1.6 Transports (src/NexNet/Transports/)

**Abstraction:**
- `ITransport.cs` - Extends IDuplexPipe; Input/Output pipes, RemoteAddress, RemotePort, RemoteCertificate (TLS peer certificate; read once into the session, exposed as `ISessionContext.RemoteCertificate`), CloseAsync (the only teardown the session calls; a transport releases everything it owns there)
- `ITransportListener.cs` - Accepts incoming connections

**Implementation:**
- `SocketTransport.cs` - TCP and Unix domain sockets: receive loop (`Socket.ReceiveAsync(Memory)`) into a `Pipe` (`ReceiveSessionPipeOptions`, read-ahead with back pressure; end of stream completes it, a socket error completes it with an `IOException`); output through `StreamSendPipe` over a `NetworkStream` that does not own the socket. Graceful close drains the send pipe, then `Shutdown(Both)`; abortive close resets (linger 0). Connect: `Socket.ConnectAsync` with a `ConnectionTimeout` token. `SetNoDelay` for TCP sockets (also used by TLS)
- `SocketTransportListener.cs` - Socket-based listener

**Configuration:**
- `ConfigBase.cs` - Base: Timeout, PingInterval, Logger, PipeOptions, `SerializerOptions` (default `NexusSerializerOptions.Untrusted`), `MeterFactory`, `TransportName` (metric tag, overridden per transport config)
- `ClientConfig.cs` - ConnectionTimeout, ReconnectionPolicy, Authenticate func
- `HttpClientConfig.cs` - Base of WebSocket/HttpSocket client configs: Url, AuthenticationHeader, UpgradeHeaders (custom upgrade-request headers; AuthenticationHeader, when set, replaces an `Authorization` entry), SslClientAuthenticationOptions

**HTTP client transports:**
- `HttpConnector.cs` - One connect attempt: `SocketsHttpHandler` + `HttpMessageInvoker` built from `HttpClientConfig`. Copies the TLS options (never mutates the config's), wraps certificate validation to keep a copy of the accepted server certificate (`TakeServerCertificate`), `IsTlsAuthenticationFailure`. When `SslClientAuthenticationOptions` gains properties, add them to `CreateSslOptions`
- `HttpSocket/HttpSocketTransport.cs` - Client: upgrade GET (`Upgrade: nexnet-httpsockets`) through the connector; owns response + connector until `CloseAsync`. Server: built by NexNet.Asp with address, port and `Connection.ClientCertificate`
- `WebSocket/WebSocketTransport.cs` - Client: `ClientWebSocket.ConnectAsync(uri, invoker)` with the same connector. Both map TLS failures and 401/403 to `TransportError.AuthenticationError`
- `WebSocket/WebSocketPipe.cs` - Receive loop into a `Pipe`; output through `StreamSendPipe` (one binary message per coalesced write); `TryAdvanceInput` and `CompleteAsync` share `_inputWriterLock` so a receive finishing after completion drops its bytes instead of advancing a completed writer
- `ServerConfig.cs` - AcceptorBacklog, Authenticate bool, RateLimiting config, AuthorizationCacheDuration (nullable TimeSpan)

**Send pipe (src/NexNet/Internals/Pipelines/):**
- `StreamSendPipe.cs` - Output of every transport (TCP/UDS over `NetworkStream`, TLS, QUIC, HttpSocket, WebSocket): `Pipe` (`SendSessionPipeOptions`) + send loop writing to the stream, copying a multi-segment read into one write of up to 1 MiB (buffer rented per batch; the limit must exceed a frame, or the peer waits for the frame's tail). A flush returns once the frame is in the pipe, so the write mutex is not held across stream writes. `DrainAsync` on graceful close (waits up to 2 s if the writer was completed, else aborts), `Abort` otherwise. A stream error completes the reader, so the session's next flush ends the session. NexNet.Quic uses it through `InternalsVisibleTo`

### 1.7 Collections (src/NexNet/Collections/)

**Modes:** `NexusCollectionMode` enum - Unset, ServerToClient (one-way), BiDirectional (two-way), Relay (read-only hierarchical)

**Core:**
- `INexusCollection.cs` - Base interface for synchronized collections
- `INexusList.cs` - List interface: Add, Remove, Insert, Clear, Count
- `NexusCollectionAttribute.cs` - Decorates properties with sync mode
- `NexusCollectionKind.cs` - Kind of a collection in snapshots (List)
- `INexusCollectionSnapshotSource.cs` - Internal: a collection's `NexusCollectionSnapshot` for one session (see 1.14)

**Lists (src/NexNet/Collections/Lists/):**
- `NexusListServer.cs` - Server-side with VersionedList<T>; broadcasts via NexusBroadcastServer
- `NexusListClient.cs` - Client-side list implementation
- `NexusListRelay.cs` - Relay mode for hierarchical distribution
- `NexusListTransformers.cs` - Converts operations to/from sync messages

**Versioned Operations (src/NexNet/Internals/Collections/Versioned/):**
- `VersionedList.cs` - Internal list with version counter and operation history
- `Operation.cs` - Base: Insert, Remove, Modify, Move, Clear, Noop

### 1.8 Pooling (src/NexNet/Pools/)

- `PoolManager.cs` - Central manager: message pools (offset=100), CTS pool, PipeManager pool, BufferWriter pool
- `PoolBase.cs` - Base pool class
- `ResettablePool.cs` - Pool for resettable objects
- `MessagePool.cs` - Pool for message objects
- `CancellationTokenSourcePool.cs` - Pools CTS instances
- `BufferWriter.cs` (Internals/Pipelines/Buffers/) - Byte `IBufferWriter` over reference-counted segments rented from `ArrayPool<byte>.Shared`; `GetBuffer()` returns a `ReadOnlySequence<byte>`; consumed data is returned from the head (`ReleaseTo`, `Deallocate`) while writing continues. Session staging buffer, result buffers, `NexusPipeReader`/`NexusPipeWriter` buffers. (The vendored `Sequence<T>`/Arenas types were removed; nothing outside the writer used them.)

**Session Pooling:**
- `SessionPoolManager.cs` - Manages proxy instance pooling per session
- `ProxyPool.cs` - Pool of proxy instances

### 1.9 Synchronization (src/NexNet/Internals/Threading/)

- `AsyncMutex.cs` - Session write lock: `TryEnter()` (lock-free, fails if held), `EnterAsync(token)` (short spin, then a pooled FIFO waiter; canceled -> `TaskCanceledException`), `LockToken` (disposing releases; a stale or second release throws). Release hands the lock straight to the next waiter

### 1.10 Pipes (src/NexNet/Pipes/)

- `INexusDuplexPipe.cs` - Bidirectional pipe: Id, ReadyTask, CompleteTask, CompleteAsync()
- `NexusPipeManager.cs` - Manages duplex pipes for large data transfers
- `NexusPipeWriter.cs` - Sends what the peer's credit allows and waits in `FlushAsync` for more. A canceled flush (token or `CancelPendingFlush`) releases only what it sent and keeps the rest buffered for the next flush (the `PipeWriter` contract; dropping it tore the stream). `CancelPendingFlush` cancels one flush. A chunk the session gives up before writing (token canceled; `NexusSession` throws instead of returning for pipe chunks) keeps its credit. Completion or a session failure drops the unsent rest; buffers return to the pool on `Dispose`
- `NexusChannelWriter.cs` / `NexusChannelReader.cs` - Typed channel items: one MessagePack value each, preceded by a varint length (`ChannelFraming.cs`). The reader deserializes each item from exactly its slice; a partial item waits for more data, with an extra credit grant from the session budget if it is larger than the pipe window. Items over `MaxMessageSize` or that break the framing reset the pipe

### 1.11 Logging (src/NexNet/Logging/)

- `INexusLogger.cs` - Hierarchical logger: Behaviors, FormattedPath, Log(), CreateLogger()
- `NexusLogLevel.cs` - Trace, Debug, Info, Warning, Error, Critical
- `ConsoleLogger.cs` - Console output implementation
- `RollingLogger.cs` - File rotation support

### 1.12 Rate Limiting (src/NexNet/RateLimiting/)

- `ConnectionRateLimiter.cs` - Thread-safe: global limit, per-IP rate (sliding window), IP banning, whitelist
- `ConnectionRateLimitConfig.cs` - MaxConcurrentConnections, MaxConnectionsPerSecond, BanDurationSeconds
- `ConnectionRateLimitResult.cs` - Allowed, MaxConcurrentConnectionsExceeded, MaxConnectionsPerSecondExceeded, IpBanned

### 1.13 Serialization (src/NexNet/Serialization/)

NexNet's own MessagePack implementation (namespace `NexNet.Serialization`, compiled into the NexNet assembly; no external
serializer dependency).

- `MsgPackWriter.cs` - `ref struct` over `IBufferWriter<byte>`; always writes the smallest canonical form; `Flush()` commits
- `MsgPackReader.cs` - `ref struct` over `ReadOnlySequence<byte>`/`ReadOnlyMemory<byte>`; length checks before allocation, iterative `Skip`, depth tracking (`Enter`/`Exit`)
- `MsgPackCode.cs` - Format byte constants
- `NexusFormatter.cs` - `NexusFormatter<T>` (writes/reads exactly one value), `NexusFormatterCache<T>` (static slot), `NexusFormatterRegistry` (`Register` first-wins, `Get`, `TryGet`)
- `NexusSerializer.cs` - Convenience `Serialize<T>`/`Deserialize<T>` entry points
- `NexusSerializerOptions.cs` - `Untrusted` (default: depth 64, strict UTF-8, randomized comparers, 16 MiB channel item buffer) / `Trusted`
- `NexusSerializationException.cs` - The only exception type decoding throws for bad input
- `Attributes/NexusObjectAttribute.cs` - `[NexusObject]`, `[NexusKey]`, `[NexusIgnore]`, `[NexusConstructor]`, `[NexusUnion<T>]`, `[assembly: NexusSerializable<T>]`, `[assembly: NexusFormatter<TFormatter,T>]`
- `Formatters/` - Built-ins: `PrimitiveFormatters` (scalars, string, bin types, date/time, Guid, decimal, BigInteger, Uri, Version), `CollectionFormatters` (arrays, lists, sets, queues, stacks, dictionaries, nullable), `EnumFormatter`, `TupleFormatters`, `ValueTupleFormatters`; `BuiltInFormatters.RegisterAll()` registers them
- `Ext/PrimitiveArrayCodec.cs` - Ext type 78: primitive arrays/lists/memory as one kind byte + raw little-endian elements
- `Internal/NexusSerializationModule.cs` - Module initializer that registers the built-ins; `PooledArrayBufferWriter`, `RandomizedEqualityComparer`

Generated formatters for user types register from a `[ModuleInitializer]` in the user's assembly (see 2.4).

### 1.14 Diagnostics (src/NexNet/Diagnostics/)

Meter `NexNet` and session snapshots (docs/articles/metrics.md).
- `NexusMeter.cs` - Public meter name constant
- `NexusMetrics.cs` - Instruments on one `Meter` (one instance per meter: `Get(IMeterFactory?)`, null = process-wide meter); session table; `Register`/`Retire` (once per session: `DisconnectCore`, end of read loop, or a collection that finds the session disconnected); `Record*` for rare events. Observable instruments sum per-session fields in one pass per collection (`Refresh`, at most every `RefreshInterval`)
- `MetricsSessionTable.cs` - Slot array + free list, shrinks when a quarter full; owner's lock only
- `MetricsTagSet.cs` - One (role, transport) tag combination: precomputed tags, live sums, retired monotonic totals
- `IMeteredSession.cs` / `SessionMetricsTotals.cs` - What a session gives a collection (lock-free reads only)
- `NexusSessionSnapshot.cs` - Public snapshot records (session, budget, invocations, pipes, partial item, large messages, collections, server). `NexusSessionSnapshot.Collections` (`NexusCollectionSnapshot`: ID as on the wire, `NexusCollectionKind`, mode, this session's state and pipe IDs, count, version) is built by `NexusCollectionManager.GetSnapshots(session)` from each collection's `INexusCollectionSnapshotSource`: one `Volatile.Read` of the `VersionedList` state (count and version together) and a lock-free walk of the broadcaster's session set; nothing is added to the change path. It is C#-only: `SnapshotFieldsExportTests.CSharpOnly` keeps it out of `tests/golden/snapshot-fields.json`, so the Java/JS snapshots do not carry it
- `LogFormat.cs` - Byte and duration formatting for log messages
- `INexusTap.cs` - Transport tap seam (opt-in `ConfigBase.Tap`): `INexusTap.SessionStarted(in NexusTapSessionInfo)` returns a per-session `INexusSessionTap` or null; it gets `OnRead` (only the bytes each read adds, never the unconsumed remainder again), `OnWrite` (the bytes committed since the last flush, once per flush), `OnConnected(final id)`, `OnClosed(reason)`. Bytes are post-TLS from the preamble on, borrowed (no copy) until the call or its returned task completes; Stopwatch timestamps. An incomplete `ValueTask` holds the read loop/flush (lossless back-pressure); a throwing tap is logged and stops tapping that direction. Recording, rings, redaction and endpoints live in NexNet.Inspector.Tap, not core. The wrappers (`Internals/Pipelines/TapPipeReader`/`TapPipeWriter`) allocate nothing per operation once warm; with no tap the session keeps the raw transport pipes, so the cost is one null check per session
- `IInspectorSchemaSource.cs` - Static abstract `InspectorSchema` (MessagePack IR) + `InspectorSchemaHash`, implemented by generated nexuses when `NexNetEmitInspectorSchema=true` (see §2.3)
Per-frame counts (`_framesSent`/`_bytesSent` under the write mutex, `_framesReceived`/`_bytesReceived` once per read in `Process`) are single-writer fields updated with `Volatile.Write` (a plain store on 64-bit): never add `Interlocked` or allocations to the per-frame paths. Tags are fixed values only (role, transport via `ConfigBase.TransportName`, reasons); never IDs, addresses or peer data.

---

## 2. Source Generator (src/NexNet.Generator/)

### 2.1 Pipeline

`NexusGenerator.cs` implements IIncrementalGenerator using ForAttributeWithMetadataName for incremental caching.

**Three Phases:**

1. **Extraction:** `Extraction/NexusDataExtractor.cs` parses [Nexus<,>]; extracts namespace, type, modifiers, generics, interfaces, methods, collections, authorization data (permissions, enum type, cache duration).

2. **Validation:** `Validation/NexusValidator.cs` validates class partial/not nested/not generic/not abstract; method signatures; collection modes; version hashes; authorization (client nexus check, OnAuthorize override, mixed enum types, enum underlying type).

3. **Emission (Emission/):**
   - `NexusEmitter.cs` - Generates partial class: CreateServer/CreateClient factories, collection properties
   - `MethodEmitter.cs` - Generates method invoker delegates
   - `InvocationInterfaceEmitter.cs` - Generates proxy implementation
   - `CollectionEmitter.cs` - Generates collection property code

### 2.2 Data Models (Models/)

- `NexusGenerationData.cs` - Root record with all extracted data
- `NexusAttributeData.cs` - IsServer, NexusType
- `InvocationInterfaceData.cs` - Methods, collections, version
- `MethodData.cs` - Name, return type, parameters, method ID
- `MethodParameterData.cs` - Name, type, serializable flag
- `CollectionData.cs` - Name, type, mode, element type, AuthorizeData?
- `AuthorizeData.cs` - Permissions (ImmutableArray<int>), PermissionEnumFQN, IsUnderlyingTypeCompatible, CacheDurationSeconds

### 2.3 Utilities

- `IncrementalHasher.cs` - FNV-1a hasher used for method, collection, interface and shape hashes
- `Emission/InspectorSchemaEmitter.cs` + `Ir/IrMsgPack.cs` - Opt-in inspector schema (`NexNetEmitInspectorSchema=true`; a ProjectReference analyzer needs `<CompilerVisibleProperty Include="NexNetEmitInspectorSchema" />`): each generated nexus also gets `<Class>.InspectorSchema.g.cs` implementing `NexNet.Diagnostics.IInspectorSchemaSource` (static abstract `InspectorSchema` span = its IR as deterministic MessagePack, `InspectorSchemaHash` = FNV-1a 64). Off = output byte-identical. Layout: `docs/internals/nexnet-ir.md` §5
- `DiagnosticDescriptors.cs` - Error/warning/info definitions; NEXNET001-027 (024-027 are authorization: client nexus, missing OnAuthorize, mixed enums, non-int enum); NEXNET028-034 and 036-038 are serialization; NEXNET039 is the `SeparatePool` warning on a pipe/channel method. NEXNET040-049 are reserved for the multi-language targets; add descriptors append-only with those IDs: 040 type not representable in a target, 041 target name collision, 042 duplicate/overloaded method names (any nexus interface, inherited ones included; nexus not generated), 043 `Targets` on a client nexus, 044 target without output dir (warning), 045 invalid target configuration, 046 reference-equality set/dictionary key (warning), 047 output file collision, 048 invalid `NexusTargetFormatter`, 049 reserved. `FindTargetDescriptor` maps the IDs `Validation/TargetValidator.cs` reports
- `SymbolUtilities.cs` - Symbol inspection helpers

### 2.4 Serialization (Serialization/)

One shape walk per producer: each producer (a `[Nexus]` class, a non-generic `[NexusObject]`, the assembly attributes) creates one `ShapeBuilder`; code generation and hashing both read its shapes, so the `HashLock` is a function of exactly what goes on the wire. Shapes hold symbols and never leave the transform phase.

- `TypeShape.cs` - The shape model: how one type is serialized. `ObjectShape` (members by key, `[NexusIgnore]` excluded, plus NEXNET030 problems), `UnionShape` (cases), `EnumShape` (underlying type and values), `NullableShape` (`Nullable<T>`), `ArrayShape`, `NamedShape` (built-in, CLR, user-formatter or unsupported type by .NET identity), `TypeParameterShape`
- `ShapeBuilder.cs` - Builds shapes from symbols, cached per producer (nullable reference annotations ignored); a shape is cached before its members are filled, so cycles terminate. Generic `[NexusObject]` types get their constructed (substituted) members. Collects `[assembly: NexusFormatter]` user formatters
- `ShapeHasher.cs` - Canonical hash walk: pre-order over a shape from one root, object members by key, union cases by tag, type arguments by position; objects and unions get an index on first visit and later references hash `Ref #i`. FNV-1a over the token stream. Names of types, members and enum members and reference nullability are not hashed; built-in/CLR/user-formatter types hash their metadata name and arguments. `HashWithListing` also renders the walk as a shape listing (tests). `NexusDataExtractor` hashes parameter types, the return kind plus `ValueTask<T>`'s `T`, and the collection kind plus item type with it
- `SerializationBuilder.cs` - Walks every type reachable from nexus signatures (methods, collections, channels), from non-generic `[NexusObject]` types declared in the assembly, and from the assembly attributes (`NexusSerializable`, `NexusFormatter`); emits a `file sealed class __NexusFormatter_*` per `[NexusObject]` type plus registrations for built-in closed generics and user formatters; reports NEXNET028-034, 036, 037
- `FormatterSpec.cs` - Equatable per-formatter output (class code + registration); specs from every producer are deduplicated by `Merge` and written by `EmitSource` into one `NexNet.Formatters.g.cs` per assembly with one module initializer
- `PrimitiveCodec.cs` - Types written/read with direct `MsgPackWriter`/`MsgPackReader` calls instead of a formatter object
- `ChannelTypeAnalyzer.cs` - DiagnosticAnalyzer for NEXNET038 (`CreateChannel<T>`/`GetChannel<T>`/`GetChannelReader<T>`/`GetChannelWriter<T>` with an unregistered `T`)

---

## 3. Extensions

### 3.1 NexNet.Quic (src/NexNet.Quic/)

- `QuicTransport.cs` - ITransport wrapping QuicConnection; adapts QuicStream to pipes
- `QuicTransportListener.cs` - ITransportListener managing QuicListener
- `QuicConfigs.cs` - QuicServerConfig (SslServerAuthenticationOptions), QuicClientConfig

### 3.2 NexNet.Asp (src/NexNet.Asp/)

`NexNetMiddlewareExtensions.cs` provides AddNexusServer<>() for IServiceCollection, AuthenticateRequest() and ApplyAuthentication() (AuthenticateRequest plus a `401` answer on failure; used by HttpSocket). AuthenticateRequest promotes the opt-in `AccessTokenQueryParameter` query value to `Authorization: Bearer` when no header is sent, strips it from the query and never logs it.

**Multi-endpoint and hosting:**
- `AddNexusServer<>(o => ...)` takes `NexusServerOptions.cs` (`Configure(primary)`, `AddListener(config, name)`; the app's `ILogger`/`IMeterFactory` fill a primary that has none) and registers `NexusServerHostedService.cs`: starts the server on `ApplicationStarted` (after Kestrel binds; a start failure logs critical and stops the app) and stops it on `ApplicationStopping`
- `MapNexusWebSocket(server, config[, name])` / `MapNexusHttpSocket(server, config[, name])` map an ASP endpoint of an already configured server (`AddExternalEndpoint`; the config supplies that endpoint's session settings)
- `MapWebSocketNexus`/`MapHttpSocketNexus(config, server)`: the primary config behaves as before; another config on a configured server becomes its own endpoint
- `UseWebSocketNexusServerAsync`/`UseHttpSocketNexusServerAsync` configure the server, or attach to one already configured instead of throwing

**WebSocket:** `WebSocket/WebSocketServerConfig.cs` provides WebSocket-specific server config: `AllowedOrigins` (empty or `*` = any; checked per request by `MapWebSocketNexus`, 403 otherwise; `UseWebSocketNexusServerAsync` also passes it to `WebSocketOptions.AllowedOrigins`), `AccessTokenQueryParameter`. `WebSocket/WebSocketAuthenticationRefusal.cs`: a WebSocket request that fails ASP.NET authentication is not answered `401` (browsers cannot see it); `MapWebSocketNexus` accepts it, sends the preamble, reads the client greeting and answers with the disconnect byte `DisconnectReason.Authentication`, then closes (no session; spec §13.7).

**HttpSocket (HttpSocket/):**
- `HttpSocketMiddleware.cs` - ASP.NET middleware for dynamic HTTP/WebSocket upgrade
- Both `Map*Nexus` paths build the transport with `ProxyHeaderResolver.GetRemoteEndPoint` and `context.Connection.ClientCertificate`
- `HttpSocketServerConfig.cs` - HttpSocket transport config; `AccessTokenQueryParameter`. Origins are checked by `HttpSocketOptions.AllowedOrigins`

**Utilities:**
- `NexusILoggerBridgeLogger.cs` - Adapts Microsoft.Extensions.Logging to INexusLogger
- `ProxyHeaderResolver.cs` - Extracts client IP from X-Forwarded-For

---

### 3.3 NexNet.Inspector.Tap (src/NexNet.Inspector.Tap/)

Inspector tap, endpoint and recorder on top of the core seam (`ConfigBase.Tap`). Format details: `docs/internals/nntl-format.md` (§4.6.1 snapshot sections, §9 live streams).
- Opt in: `serverConfig.UseInspector<TNexus>(options)` / `clientConfig.UseInspector<TNexus>(options)` (`TNexus : IInspectorSchemaSource`, from `NexNetEmitInspectorSchema=true`; the non-generic overloads stream no schema) returns a `NexusInspector` (the `INexusTap`). `inspector.UseSnapshotsOf(server|client)` feeds attach snapshots. `InspectorOptions`: `AllowRecording` (default false; gates `RecordTo`, record-all and attaches with `Record`), `IncludeSecrets` (default false: token redacted), `RingCapacity` (per viewer, 4 MiB), `FlushInterval`
- `TappedSession` (per session `INexusSessionTap`): with nothing linked, `OnRead`/`OnWrite` are one volatile read + completed task (no allocation, no Interlocked; test `An_unwatched_session_allocates_nothing_per_op`). Copy-on-write `SinkLink[]`; op ids per direction. `GreetingRedactor` holds (copies) the client-to-server ops until the ClientGreeting frame is complete, finds the token with `MessageAssembler.TryFindGreetingToken`, replaces it with `InspectorRedaction`'s same-length marker (hex SHA-256 prefix, `*` past 64) and flags each op's range; unparsable body = whole body redacted
- `TapSink` (one per viewer/recorder): pre-allocated byte ring of 48-byte-header records (op, gap, control marker, wrap), producers copy under the sink lock, a background sender writes `.nntl` blocks through `NntlWriter` to a `SinkOutput` (`PipeSinkOutput` over a NexNet pipe, `StreamSinkOutput` for `RecordTo`). `InspectorOverflow.Drop` counts dropped ops into per-direction gaps (Gap block before the next op that fits); `Block` returns a pending `ValueTask` (`AsyncSignal`, reusable) until the sender copies the op in; ops larger than the ring are written directly from the borrowed bytes once the ring is empty. Session open/close travel as ring markers to stay in order
- `NexusInspector`: `GetSessions`, `GetServerInfo` (policy for clients), `RecordTo(Stream)` (all sessions into one stream, new ones from inception; stop/dispose writes the footer), `StartEndpointAsync(ServerConfig, token)`: a separate generated nexus server (`InspectorEndpointNexus`, internal) that refuses a tapped config (never taps itself), sets `Authenticate`, compares the token with `CryptographicOperations.FixedTimeEquals`
- Endpoint API (`IInspectorEndpointNexus`): `GetServerInfo`, `ListSessions`, `Attach(InspectorAttachRequest)` / `RecordAll(InspectorRecordRequest)` validate and arm (status `InspectorStatus`), `OpenStream(pipe)` streams the armed request (pipe methods cannot return values), `Detach`. Client helper `InspectorClient.ConnectAsync(clientConfig, token)` → `AttachAsync`/`RecordAllAsync` return an `InspectorStream` (`ReadAsync`/`Current` via Core's `NntlPipeReader`, or `CopyToAsync(file)` for a verbatim recording); throws `InspectorConnectionException` / `InspectorRequestException`
- Tests: `src/NexNet.Inspector.Tests/Tap/` (UDS/TCP/TLS end to end with I02/I03 parsing, redaction, Drop/Block, RecordTo + crash, record-all, policy, wrong token, detach, client side)

### 3.4 NexNet.Inspector.Cli (src/NexNet.Inspector.Cli/)

`nexnet-tap`, Native AOT (`PublishAot`; win-x64 publish has no trim/AOT warnings). All types internal; `InternalsVisibleTo` NexNet.Inspector.Tests, which references the project.
- Commands: `list` (policy + live sessions), `record --session <id|first|all> --out f.nntl [--duration] [--overflow drop|block] [--no-snapshot] [--wait] [--include-secrets] [--force]`, `info f.nntl [--json] [--messages]`, `repair f.nntl [--force]`, `tail <file|endpoint> [...]` (below), the queries (below), `help [command]`, `help format` (= `format`: `TextLegend.Text`), `mcp` (MCP server, below), `--version`. Connection options: `--endpoint tcp://host:port | tls://host:port | uds:/path`, `--token` or env `NEXNET_TAP_TOKEN`, `--insecure` / `--server-name` (tls only), `--connect-timeout`. Durations `250ms`/`30s`/`5m`/`1h30m` (bare number = seconds)
- Exit codes (`ExitCode`): 0 ok (record: also Ctrl+C/duration/session closed), 1 failure (unreachable, token or TLS refused, I/O, unreadable file, recording without footer), 2 usage, 3 refused by server config ("Recording disabled by server config", `--include-secrets` without `IncludeSecrets`), 4 session not found / none within `--wait`, 130 Ctrl+C before recording started
- **Command table** (`CliCommands.All`, one `CliCommandSpec` row per command: verb, `Allowed` options, bare-argument count (`CliArguments.Positionals`; the first is `File`), usage text, `CliRunner`): parsing (`TryParseCommand`/`Name`/`Allowed`), `help <command>` and `TapCli.RunAsync` dispatch all read it. A new command = a `CliCommand` value + a row + its usage + runner (+ new `CliOption`s, their `TakesValue`/`TrySetValue`/`TryValidate` cases); `CliCommandTableTests` checks every enum value has exactly one row
- `CliArguments.TryParse`: hand-written, enum-based (`CliCommand`, `[Flags] CliOption` with per-command `Allowed` masks); `EndpointAddress` (record struct) parses addresses and builds the `ClientConfig`. `TapCli(TextWriter output, TextWriter error).RunAsync(args, envToken, ct)` runs a command line; `RecordingStarted` callback and `DrainTimeout` exist for tests
- `record`: checks `GetServerInfo` policy first, then `RecordAllAsync` or `AttachAsync(Record: true)` (client role → `InspectorTargetKind.Client`; an ID matches `Id` or `ConnectedId`); `InspectorStream.CopyToAsync` straight into the file, not cancelable: Ctrl+C/duration calls `DetachAsync` and waits for the close blocks + footer; then verifies the footer with `NntlReader`
- `info`: `RecordingAnalyzer.Analyze(stream, name, messages)` → `RecordingSummary` (record; rows are record structs) in one pass over `Index.ToFileOrder()`: per session ops/bytes/redacted per direction, frames via the I02 `FrameParser` (preamble expected only when the first op starts with it; framing stops at a gap), gaps, snapshot, schema hash + embedded, close; `--messages` also runs the I03 `MessageAssembler` (keeps bytes in memory). `--json` via source-generated `CliJsonContext` (string enums, camelCase)
- `repair`: `NntlFile.RebuildIndex`; a file whose footer is intact is opened read-only and left untouched unless `--force`
- **Text renderer** (Core `Text/`; spec `docs/internals/inspector-text-format.md`, the LLM tooling's shared output): `new TextRenderer(session, TextOptions)` → `Render(TextWriter)` writes what is new since the last call (whole session the first time; keep the renderer for a live tail: later calls append, `res`/`cancel` lines for calls shown `-> …`, END once finished), `RenderToString`, `Collect(List<TextRecord>)` (records without budgets, for queries/findings), `Format(record)`. `TextOptions`: `Level` Summary/Messages/Frames/Bytes, `Format` Text/Ndjson, `Collapse`, `ValueWidth`, `Depth`, `ByteLimit`, `MaxLines`, `MaxTokens` (`TextBudget.Estimate`: chars/4, non-ASCII ×3), `Filter` (`TextFilter`: `Window` in session ticks (`WindowFromMilliseconds`), `Directions`, `Match` predicate + `MatchKey`; add filters as properties folded into `Fingerprint()`), `After` (`TextCursor`: session, level, phase, frame/gap position, skip, filter fingerprint; `Encode`/`TryParse` base64url; a mismatched cursor throws `ArgumentException`). `TextRenderResult(Lines, Withheld, Tokens, Next, Complete)`; a budget hit writes `… budget hit, N withheld, --after <cursor>` and the renderer moves to that cursor. `TextRecord` (kind, ticks, `TimeMs`, direction, `Id`/`Name`/`Value`/`Outcome`/`Result`, `Count`/`SpanMs` when collapsed, `Frame`/`Entity` indices, `CursorAt`); `TextRecordWriter` (text + ndjson via source-generated `TextJsonContext`, short keys; `SessionTag` prefixes lines / ndjson `s` for interleaved sessions; `WriteStop` = tail's `… stopped: …` line); `TextValues` (sizes, durations, cut values); stable IDs `InvocationId` (`#5`, `#5.2` for a reused wire id), `PipeId` (`≋4·4`), `FrameId` (`f18` = capture order + 1); `TextLegend.Text` (≤ 20 lines, for `help format`). Events are frames in `FrameOrder` merged with gaps by `GapMark.FrameIndex`. Goldens: `Inspector.Tests/Golden/Text/<fixture>.<level>.txt` (frames/bytes capped at 200 lines) compared in place by `TextGoldenTests`; regenerate with `NEXNET_INSPECTOR_UPDATE_GOLDEN=1 dotnet test src/NexNet.Inspector.Tests -c Release --treenode-filter "/*/*/TextGoldenTests/*"` and review the diff
- **`tail`** (I46; `TailCommand` + `CliArguments.Tail.cs` (partial: tail options, `TrySetTailValue`/`TryValidateTail`; `CliOption` is `ulong`)): the positional is a file or an endpoint (`tcp://`/`tls://`/`uds:`; or `--endpoint`). File replay = `RecordingFile` session by session in file order, `Collect` per session (`--session id|first` picks); `--follow` and live = `StreamAsync` over an `ITailSource` (`FollowFileSource`: `NntlPipeReader` over a stream that waits at EOF; `EndpointTailSource`: `InspectorStream`) fed by a pump into `LiveCaptureSet` (one `LiveCapture` per session-open block, header/schema blocks replayed) and rendered every 100 ms (`RenderInterval`); live `--session all` = `RecordAllAsync` (needs AllowRecording), single = `AttachAsync(Record: false, Drop, snapshot)`; interleaved output (live all, follow with `--session all`) tags lines `@0x…`; follow without `--session` renders sessions one after another. Filters: `TailFilter` = `TextFilter.Match` + `MatchKey` (method globs incl. cancel lines via `MethodName` and frame lines `#n Method`, pipe IDs `TailPipeId`, `--only` `TailOnly` errors/slow/cancel/gaps, grep on the text form; header/stat/END always pass; bytes lines follow their frame), `--dir` = `TextFilter.Directions`. `TailPrinter`: sampling (header/END/stat always), `--until` (`TailUntil`: `|` alternatives of `,` terms `method=`/`state=`/`error`/`gap`/`end`/`type=`, enums `TailUntilTerm`/`TailCallState`; prints session header + ring of `--context` lines + match + stop line), whole-tail budgets (budget line with `CursorAt` of the first withheld line), flush per line. Stop lines carry `--after` only for files; `--after` on a live tail is a usage error; the cursor's fingerprint is checked before reading (`TailFilter.Create(args, null, …)`). Exit: 0 ended/Ctrl+C/duration/budget/until met, 4 until never met, 2 bad cursor. `Program` sets UTF-8 console output (glyphs). `TapCli.TailStarted` for tests. Goldens `Inspector.Tests/Golden/Tail/<name>.txt` (`TailFileTests`, same `NEXNET_INSPECTOR_UPDATE_GOLDEN=1` switch); live tests `TailLiveTests` (TapHarness)
- **Queries** (I47, `QueryCommand.cs`; logic in Core `Text/TextQuery*.cs`): `summary`, `show <id> [--bytes]`, `trace <id>`, `around <t> [±w] [--level]`, `errors [--level]`, `slow [--over d]`, `pipes`, `collections [--contents]`, `schema` take `<file.nntl>` (all sessions, or `--session <id|@0xHEX|first>`) or a live `--endpoint` + `--session <id|first>` (`AttachAsync`: attach with `LiveCapture` for `--duration`, default 2s, then detach and query the snapshot); `diff a b [--session] [--top 5]` (`CompareSession` → `TextQuery.Diff`); `export a --from --to -o [--force]` (`CaptureExport.WriteRange`; live export attaches with `Record: true` and needs `AllowRecording`, exit 3 otherwise). Output options on all: `--format text|ndjson`, `--max-lines`, `--max-tokens`, `--after <cursor>`, `--value-width`, `--depth` (show/trace default 64/8). Times `+27.5`/`27.5`/`1.2s`/`±10` (bare = ms, `QueryCommand.TryParseTime`). `CliOption` is `ulong` (more than 32 options). Core: `new TextQuery(session, TextOptions)` → `Summary`/`Show`/`Trace`/`Around`/`Errors`/`Slow`/`Pipes`/`Collections`/`Schema`(`List<TextRecord>`), static `Diff(compare, options, top, list)`, `TryResolve(id, List<TextEntityRef>)` (`#5`/`5`, `5.2`, `≋4·4`/`p4·4`/`p4.4`/`p4`, `f18`), `Header()`, `IsError(record)`; built on the renderer (`Collect` with `Filter.Match`) plus record kinds `Field` (`fld`, `Depth`, `Offset`+`Bytes` → ` @o+n`), `Fragment` (`frag`), `Delta` (`delta`). `TextQueryWriter(options, queryText).Write(output, [(sessionId, records)])` applies budgets; its cursor is a `TextCursor` with `Skip` = lines of that session written and `Filter` = `TextQueryWriter.Fingerprint(queryText)` (a cursor of another query is refused). Goldens: `Inspector.Tests/Golden/Queries/*.txt` (`CliQueryTests`, same `NEXNET_INSPECTOR_UPDATE_GOLDEN=1` switch)
- **Findings** (Core `Findings/`, D6-C): `FindingsEngine(session, FindingsOptions)` → `Update(List<Finding>)` returns what was detected since the last call, in detection order (incremental: keep it for a live tail; end-of-session findings once `State != Open`; `IsComplete`); `Analyze(session)` = one update + `Rank` (severity desc, then `StartMs`, kind, `EndMs`, session, ids, message); `AnalyzeAcross(sessions)` = reconnect loops (per remote host: sessions starting ≤ `ReconnectWithinMs` after a failed close, or with ClientGreetingReconnection; ≥ `ReconnectLoopMin`). `Finding(Severity, Kind, Message, Ids, Range)` record struct (+ `SessionId`, `StartMs`/`EndMs`, `IdText`, `Cites`); `FindingSeverity` Info/Warning/Error; `FindingKind` (code via `FindingsText.Code`): ProtocolError `protocol`, Disconnect, SchemaMismatch `schema`, Gap, OrphanResult `orphan`, CancelRace, PendingAtEnd `pending`, PipeOpenAtEnd `pipe-open`, WindowStall, CreditStall, SlowCall `slow`, PingRtt, LargeMessage `large`, ChunkStorm, ReconnectLoop. Detectors read the assembler directly (partials `.Detectors` frames/gaps/pings/credit/windows/chunks, `.Calls` invocations/messages/items/end), IDs and method names via a `TextRenderer` (`InvocationId`/`PipeId`/`FrameId`/`MethodName`); each event is judged once against what came before (slow outliers of methods with < `OutlierMinSamples` earlier calls are re-judged at the end); windows: credit per (pipe, writer direction) from Ready/WindowUpdate `PipeEvents` of the other direction minus `DuplexPipeWrite` `BodyLength`, a stall = credit ≤ 0, a grant, then another write, waited ≥ `StallMs`; invocation credit from the peer greeting's `InvocationCredits` + `InvocationCredit` frames; pings pair a client ping with the server's next ping; `MaxPerKind` caps a kind per session (+ one "N more" info). `FindingsText`: `ToRecord` (`TextRecordKind.Finding`, ndjson `find`: `r` severity, `n` kind, `id` space-separated IDs, `v` message, `sp`), `Format`, `Tally`, `Write(output, findings, TextOptions, tally)`; text form `+t ! <error|warn|info> <kind> <ids> <message>` (spec §11). CLI `findings <file> [--session <id|first|all>] [--slow <d>] [--json]` (`FindingsCommand`; `CliOption.Slow`; header line from the renderer, ranked findings, tally, then `across N sessions` + reconnect loops; exit 4 for an unknown session). `tail --findings` (`CliOption.Findings`, `CliArguments.Findings.cs`; `TailFindings`: one engine per tailed session, `Update` after each printed batch, `!` lines through a `TextRecordWriter` with the session tag; extra lines, outside budgets and cursors). Tests: `Inspector.Tests/Findings/` (`CraftedSession`: a `CaptureSession` built op by op from `WireScript` frames on a µs clock with gaps/close/truncate and an `AfterStep` hook; one scenario per detector, clean capture, ranking, live = finished), `Cli/CliFindingsTests`
- **MCP server** (I49, D6-D/D7; `Mcp/`): `nexnet-tap mcp [--token]` (`CliCommand.Mcp`, runner `McpServer.RunCommandAsync`; token default = `NEXNET_TAP_TOKEN` via `CliArguments.TryValidate`; `TapCli.Input` = stdin as UTF-8, set by `Program`). `McpServer(input, output, log, defaultToken).RunAsync(ct)`: hand-rolled JSON-RPC 2.0, one message per line (`Utf8JsonWriter` with relaxed escaping, never an embedded newline; `\n` only), requests parsed with `JsonDocument`, results serialized through the source-generated `McpJsonContext` (`McpJson.cs` records). Targets MCP **2026-07-28** (`ProtocolVersion`: `server/discover`, requests must carry `_meta["io.modelcontextprotocol/protocolVersion"]` + `clientCapabilities`, results carry `resultType`/`_meta` serverInfo; tools/list `ttlMs`/`cacheScope`) and, dual-era, the legacy flow (`initialize` negotiates 2025-11-25/2025-06-18/2025-03-26 and sets `_legacy` for the process, `notifications/initialized`, `ping`). Errors: -32700 parse, -32600 invalid (batches, bad id/jsonrpc/method), -32601 method, -32602 params (unknown tool, missing `_meta` version), -32022 unsupported version (`data.supported/requested`), -32603 internal. `tools/call` runs on the pool with a CTS per id (`_inflight`); `notifications/cancelled` cancels it and no response is sent. stdin EOF: calls in flight are answered (`DrainTimeout` 30s), then cancelled, then `McpToolRunner.DisposeAsync` stops runs. Tools (`McpTools.All`, `McpToolKind` order; `McpToolSpec` with JSON Schema text; `McpTools.TryValidate` checks type/items/enum/minimum/maximum/required/unknown names → `isError` text, never echoing values; `McpArgs` typed reads): `list_sessions`, `attach`, `read`, `detach`, `summary`, `findings`, `show`, `trace`, `around`, `schema`, `diff`, `export_section`, `record_start`, `record_stop`. `McpToolRunner` turns each call into a `nexnet-tap` argv (options inline `--name=value`; token passed as `RunAsync`'s environment token, never on argv; no `--include-secrets`) run in process through `TapCli` with `StringWriter`s: output = stdout text, exit ≠ 0 → `isError` with cleaned stderr (`Clean`: no `nexnet-tap:` prefix / help pointer / Ctrl+C hint) + `(meaning)`. Queries default `--max-tokens 8000`; results capped at `MaxTextChars` 100,000. `attach`/`record_start` = a background `McpRun` (`tail` / `record` with `TailStarted`/`RecordingStarted`; ids `s1…`/`r1…`; ≤ 16 runs) writing into `McpLineBuffer` (thread-safe `TextWriter`, numbered lines, keeps the last 10,000, `Read(after, max, lines, out dropped)`, `Changed` task for `wait_ms`); `read` pages by line number (≤ 1000, footer `… next: after=N (…)`), `detach`/`record_stop` cancel the run as Ctrl+C (record: footer) and remove it. Tests: `Inspector.Tests/Mcp/` (`McpTestClient` in-process over a channel stdin + `McpLineBuffer` stdout; `McpProtocolTests` conformance, `McpToolsTests` every tool vs fixtures and a `TapHarness` endpoint incl. policy refusal, `McpProcessTests` spawns the apphost over real stdio)
- Tests: `src/NexNet.Inspector.Tests/Cli/` (argument parsing, list/record first|id|all over TCP and TLS endpoints via `TapHarness.StartEndpointAsync(transport)` + `EndpointAddress`, policy refusal, info vs reader counts, info JSON, repair truncated/intact)

### 3.5 NexNet.Inspector.Sample (src/NexNet.Inspector.Sample/)

The "Chat" sample (Exe, `NexNetEmitInspectorSchema=true`): the traffic the inspector screens show, and the generator of the test fixtures.
- `ChatNexus.cs`: `IChatServer` (`Join`, `SendMessage(…, CancellationToken)` held by a `/slow` text until canceled, `SendAttachment` (300 KB → MessageStart + 5 chunks), `UploadFile` raw FileUpload pipe, `TailLogs` `Channel<LogLine>`, `Users` ServerToClient `INexusList<long>`) / `IChatClient`. `LegacyChatNexus.cs`: an older API whose schema hash differs (open-recording mismatch). `Users` holds `long` user IDs only because lists of `[NexusObject]` types and strings could not send a non-empty reset before I35; that limitation is gone, and the type is kept to avoid fixture churn
- `ChatScenario`: Full / Light runs (pipe 1 Users, Join, a canceled SendMessage, pipe 2 small upload, pipe 3 FileUpload while the attachment streams, pipe 4 LogLine channel, optional burst on pipe 5). Clients send a 96-byte token and ping every 400 ms; `ChunkPacer` (an `INexusTap` wrapper in front of the client inspector) holds each MessageChunk flush 250 ms so pipe frames and a Ping interleave with the chunks
- `RecordingAnalysis` replays a `.nntl` through `NntlReader` → `FrameParser` → `MessageAssembler` → `SessionDecoder` (embedded schema); `Fixtures.Check(kind, analysis)` lists what a fixture lacks; `FixtureGenerator` records them (retrying a run whose timing missed)
- Fixtures (`src/NexNet.Inspector.Tests/Fixtures/`, binary in git): `chat-server.nntl` (Full session recorded server side, Drop mode, recorder stalled during the burst → gap blocks), `chat-client.nntl` (the same session recorded client side, Block mode; Compare pair), `chat-client-variant.nntl` (another Full run recorded client side, first message `hello everyone!` and an extra `one more thing` SendMessage after it: the Compare pair with real differences), `server-all.nntl` (endpoint record-all: 3 sessions, one with a snapshot), `legacy-schema.nntl` (2 legacy sessions, schema hash ≠ current Chat), `server-all-truncated.nntl` (no footer, torn last block), and the findings scenarios of `FindingsNexus.cs` (`IFindingsServer.Work(delayMs)`, `Upload(readDelayMs, pipe)`; recorded server side, Block mode, `FixtureGenerator.RecordFindingsAsync`): `findings-slow.nntl` (15 instant Work calls and one of 200 ms: a p99 outlier), `findings-stall.nntl` (64 KB upload through a 16 KB `PipeWindow` to a reader that waits 100 ms after each read: window stalls), `findings-orphan.nntl` (recorded from mid-call: a result without its request); `Fixtures.Findings` lists them. Regenerate after a Chat nexus, format or tap change: `dotnet run --project src/NexNet.Inspector.Sample -c Release -- fixtures` (default directory `src/NexNet.Inspector.Tests/Fixtures`; `fixtures DIR ChatClientVariant` re-records only the named `FixtureKind`s and leaves the other files alone); `FixtureTests` check the checked-in files, `FixtureGeneratorTests` that the command still produces valid ones
- Live (Phase-1 gate): `dotnet run --project src/NexNet.Inspector.Sample -c Release -- serve --transport tcp --port 7711 --endpoint tcp://127.0.0.1:7712 --token sample-token --allow-recording` (TLS: `--transport tls --port 7721 --endpoint tcp://127.0.0.1:7722`, self-signed certificate unless `--cert FILE.pfx`; UDS: `--transport uds --path SOCKET`; `--record FILE.nntl` records server side), then `… -- client --transport tcp --port 7711` (`--iterations N`, `--delay ms`, `--burst lines`). Record from the endpoint with `nexnet-tap record` and read the file with `nexnet-tap info FILE.nntl`; `… -- check FILE.nntl` replays a recording with the core and reports framing faults
- Cross-language capture: `IntegrationTests/InspectorCaptureTests.cs` (`[ClientMatrix]` on Tcp, TcpTls, WebSocket, WebSocketTls): a `NexusInspector` installed on every interop endpoint records a session driven by each client language; the server-side bytes must frame and assemble (preambles, both greetings, a call and its result)
- CI: `.github/workflows/inspector.yml` (ubuntu-26.04 linux-x64, windows-latest win-x64): see §3.6 (Local AOT check)

### 3.6 NexNet.Inspector.App (src/NexNet.Inspector.App/)

Desktop app `nexnet-inspector` (assembly name; resources are `avares://nexnet-inspector/...`): Avalonia 12.1.3 + Fluent, AvaloniaEdit 12.0.0, CommunityToolkit.Mvvm 8.4.2. Native AOT win-x64 + linux-x64, zero trim/AOT warnings. Compiled bindings only (`x:DataType` everywhere), no reflection, `InvariantGlobalization`, `BuiltInComInteropSupport=false`, `AllowUnsafeBlocks` (one `[LibraryImport]`). User guide: `docs/articles/inspector.md`. Tests: `src/NexNet.Inspector.App.Tests` (TUnit + Avalonia.Headless/Skia, `InternalsVisibleTo`; UI tests `[NotInParallel("avalonia")]` via `HeadlessSession.Run`). Gotcha: TUnit `IsEquivalentTo` on view-model objects hangs (walks the graph) — compare references/values.
- **Rules**: colours only from `Themes/Tokens.axaml` (DynamicResource) or, in drawn controls, `Controls/Primitives/ThemeBrushes.For(this)` → cached `ThemePalette` (indexer by `ThemeBrush`, `Ui`/`Mono` fonts, `Pen(brush, width, dashed)`), invalidated on `ActualThemeVariant`; `ThemeTokenTests` fails on any colour literal outside those two files and pins the token key list. Animate visibility with Opacity only. Every list virtualized. Drawn controls shape text on bind, never per frame (allocation tests for timeline/ladder).
- **Composition**: `Program` (`AppOptions.TryParse`: `--headless`, `--self-check` (needs `--headless`); bad args → exit 64). `App.axaml(.cs)`: styles Fluent → AvaloniaEdit Fluent theme → `Themes/Controls.axaml` (merges `Tokens.axaml` and one file per control theme); `StartDesktop` loads settings, `ThemeService`, `MainWindowViewModel`, `MainWindow { ConfirmsUnsavedClose = true }`, saves placement on Closing, disposes session area + documents on Exit. `Checks/SelfCheck` (main window, render, both themes' surface pixel, font weights, AvaloniaEdit; prints `self-check: PASS|FAIL`, exit 0/1, never touches settings).
- **Tokens**: `Tokens.axaml` = every tokens.json token as `Color.*`/`Brush.*` per Light/Dark ThemeDictionary, `Shadow.Menu|Popover|Dialog|Palette`, `Opacity.*` (`Opacity.Scrim.Dialog`/`.Palette`), `Font.Ui`/`Font.Mono`, `FontWeight.*`, `FontSize.*`, `LineHeight.*`, `Spacing.*`, `Size.*`, `Radius.*`, `Motion.*`; overrides Fluent's app-wide and TextControl/ComboBox keys. Fonts `Assets/Fonts/Atkinson` (4 weights) + `Assets/Fonts/JetBrainsMono` (3), OFL texts copied beside the exe; logo linked from `docs/images`.
- **Settings**: `SettingsPaths` (`%LOCALAPPDATA%\NexNetInspector\settings.json`, else `$XDG_CONFIG_HOME`/`~/.config/nexnet-inspector/settings.json`). `AppSettings` record (settable, not init: STJ source gen): `Theme` (`AppTheme` System=0/Light/Dark), `RecentFiles`/`RecentEndpoints` (newest first, max 10), `Window` placement, `Connect` (`ConnectDefaults`, never the token), `Bookmarks` (I44: `RecordingBookmarks(Key, LastUsed, Items)` of `SavedBookmark`/`SavedScope`, most recently used first; `WithBookmarks`/`BookmarksFor`; caps `MaxBookmarksPerRecording` 50, `MaxBookmarks` 500 total (least recently used recordings dropped whole); `Normalized` cleans them). `SettingsJsonContext`; `SettingsStore` Load (Missing/Loaded/Corrupt→.bak/Unreadable), atomic Save/Update. `ThemeService` (OQ15: System follows `IPlatformSettings` live).
- **Core model it reads** (Core `Session/`, no UI types): `CaptureSession` (per direction `TransportOpStore` + `FrameParser`, `MessageAssembler`, `SessionDecoder`, `FrameOrder`, gaps, snapshot, close, `Version`; readers take `Gate`), `RecordingFile` (Summarize, `CreateSession`, background indexing with progress, `RecordingWarnings`), `LiveCapture`, `CaptureExport.Write` (one session → .nntl), `SchemaResolution`; stores `LiveOpStore`/`RecordingOpStore`; `Core/Model/TimeWindow`; `FrameDetail`. Compare engine: Core `Compare/` (`CompareSession`: exactly 2 sessions, entities matched by wire flow, `Matches`/`Groups`/`Deltas`, `Align`, `CompareClock` (TimeSpan ticks, 0 = earlier aligned start, `Frequency`), lazy `GetFieldDiff`; tests `Inspector.Tests/Compare/`).
- **Services**: `IUiDispatcher`/`AvaloniaUiDispatcher` (only Avalonia edge), `ChangeCoalescer` (one post per burst), `SyncBus` (window, bounds, playhead, selection, hovered field, decode-as, follow-live, `Trace` resolved once per selection by `Model/MessageTrace`; `Changed` once per UI turn with `SyncChanges` flags (ushort); I43 scope: `Scope`/`SetScope` (`SyncChanges.Scope`), `Bounds` = the scope or else `CaptureBounds`, the window clamped strictly inside a scope, follow-live suspended while scoped (`ResumesFollowLive` restores it on clear; `OnLiveData` is a no-op)), `EndpointAddress`, `InspectorConnection` (connect, `ProbeAsync`, `ConnectAgainAsync`). `Services/Commands/`: `AppCommand` (declaration order = palette order; I43 appended `FilterToRange` Ctrl+Shift+F, `FilterToView`, `ScopeBack` Alt+Left, `ClearScope`, glyph `CommandGlyph.Scope` ⊡), `CommandDefinition` (title, `CommandGroup` File/Session/View/Help, glyph, `KeyGesture`, `IsWindowShortcut`), `CommandRegistry` (static `Definitions`; per instance `Bind(cmd, run, canRun)` by the owner, unbound = disabled, `CommandFor` cached `IRelayCommand`, `Refresh`/`Changed`); `MainWindow.BindShortcuts` binds every window shortcut from the registry.
- **Documents** (`ViewModels/`): `DocumentsViewModel` (open/activate/close, MRU `Cycle`, shared `Sync` rebased per document, recents; hooks `ClosePrompt` (OQ5 `UnsavedCloseDialog`), `SaveHandler`, `SavePicker`, `SessionPicker` (open-recording dialog), `SessionComparer` (Compare); `ConfirmDiscardAsync`, `SaveAsync`/`SaveToAsync` (temp beside target, then move), `ReplaceWithRecordingAsync`; Compare ticks `Compared`/`CanCompare`, `MaxCompared` = 2). `FileDocumentViewModel` (indexing state/progress, warnings). Scope (I43, D2): `Model/TimeScope` (readonly record struct Start/End/Label, `ScopeLabels`) and `Model/ScopeStack` (nested push cut to the parent, pop/clear return the window shown before; `Changed(ScopeChange)`; not persisted) on every `DocumentViewModel` (`Scopes`, `Scope`, `ScopeDepth`, `PushScope(scope, before)`/`PopScope`/`ClearScopes`, `ScopeChanged`); `DocumentsViewModel.PushScope`/`FilterToView`/`PopScope`/`ClearScope`/`CanScope` move the shared window with the active document's scope and `Sync.Restore` carries it across tab switches; `ReplaceWithRecordingAsync` copies the live tab's scopes. Panes: `EventsTableSource.Scope` and `LadderModel.SetScope` show one index range of the capture's rows (binary search on start ticks, capture numbers kept; `LadderModel.RowAt`/`TotalCount` for the projection's absolute indexes), `HexLayout.Update(…, scope)` lays out only ops whose ticks fall in it, `TraceStepRow.IsOutOfScope` (⊘, 40 %), `PaletteSearch.Run(…, scope)`, `ExportRangeKind.Scope` (the default with no explicit range), Compare `CompareRowsModel.SetScope` on the aligned axis (`CompareWorkspaceViewModel.ScopeOnAxis`: file A's scope, else B's, via `Clock.ToCompare`). `ScopeDialogViewModel` + `Views/Scope/ScopeDialog` (Filter to time range…: current view | selected or traced message | typed ms). `LiveDocumentViewModel` (attach/detach (detach resets), `LiveState`, `AttachedAt`/`EndedAt`/`EndReason`/`EndedBy`, `IsDirty`, `SavedPath`, `RecordingAllowed`). `ConnectViewModel` (probe after 500 ms or blur → sessions → Attach; `Prober` swappable). `MainWindowViewModel`: `Documents`, `Chrome`, `Inspect`, `Compare`, `Ended`, `Home`, `Palette`, `Commands`, `Dialog` (one at a time; DataTemplates in `MainWindow.axaml`), `ShowConnect`/`ShowPalette`/`ShowHelp`/`ShowGoToOffset`, `HasUnsavedDocuments` + `ConfirmCloseAsync` (window close: each dirty tab asks in turn; `MainWindow.OnClosing` holds the close while `ConfirmsUnsavedClose`, skipped on OS shutdown; tests/captures leave it off).
- **Shell** (`Views/Shell/`, `ShellChromeViewModel` = `Chrome`): `TitleBar` (logo → app menu, Inspect|Compare `Workspace`, `DocumentTabStrip` (tabs mirror `Active`; ✕/middle-click close through `ClosePrompt`; Compare checkboxes), session area, time window, caption buttons; every interactive element sets `WindowDecorationProperties.ElementRole`). `ChromeMode` Extended (Windows: client area under the OS frame, `Platform/TitleBarRecovery`) / Undecorated (Linux: own drag + `ResizeGrips`). OQ10 `Model/ShellLayoutPolicy.For(width)`: Full / RightPaneClosed (<1200) / InspectorNarrow (<1150, 280 px). `SessionAreaViewModel` (`Chrome.Session`: follows the active or last live doc; popover `SessionPopover` lists/switches sessions, Record all → `server-all-<ts>.nntl` on a second connection (ALL · n), REC timer = time since `AttachedAt`, frozen at `EndedAt` once ended). **Bookmarks** (I44, D3/D4): `Model/ViewBookmark` (`ViewBookmark(Name, Window, Scope?, CreatedUtc)`, `SameView`, `ToSaved`/`From`) and `RecordingKey(Value, IsPersistent)`: `Of(RecordingFile|path, sessionId)` = lowercase hex SHA-256 over the Header block body as stored + session id (i64 LE) + the session's transport-op count (i32) + last indexed tick (i64) from the index (header = identity; the extent separates section exports and later saves of a growing live session, which copy the header; a whole copy keys like its source), `Live(documentId, sessionId)` in memory. `DocumentViewModel.BookmarkKey` (file: computed once; live: from the attached session). `Services/BookmarkStore` (`DocumentsViewModel.Bookmarks`: `Get` (live view), `Add`/`Rename`/`Remove`/`CanAdd`, `CopyTo(from, to, within)` = re-key (dedupe by view, scope cut to `within`), `Forget` (live tab closed), `Changed`; persistent keys write `AppSettings.Bookmarks` on every change). `DocumentsViewModel`: `CanBookmark`, `ActiveBookmarks`, `AddBookmark(name?)` (default name `TimeWindowReadoutViewModel.Format`), `JumpToBookmark(i)` (pushes the bookmark's scope, nested when inside the current one, else clears first; a scope-less bookmark keeps the current scope only if its window fits; stops follow-live; `SetWindow`), `CopyBookmarksToRecordingAsync(document, path, sessionId, within)` (reads the written file's key) called by `SaveToAsync` and Export section (`ExportSectionDialogViewModel.IncludeBookmarks`, default on, `BookmarksText` "Include bookmarks in range · n of m"); `ReplaceWithRecordingAsync` copies live → file key. UI: `BookmarksViewModel` (`Chrome.Bookmarks`: `Items` of `BookmarkItem`, `IsOpen`, `Toggle`/`Add`/`Jump`/`Delete` commands, `BeginRename`/`CommitRename`/`CancelRename`, `EditRequested`), TitleBar `BookmarksGroup`/`BookmarksButton`/`BookmarksCount` after the readout, `MainWindow.BookmarksPopup` → `Views/Shell/BookmarksPopover` (rows `BookmarkRow`/`BookmarkName`/`BookmarkRange`/`ScopeBadge`/`DeleteButton`, editor `NameEditor`; F2 is a popover `KeyBinding` because Avalonia matches key bindings from the focused element up before routing the key, so it beats the window's F2 Legend; Enter/Esc/Delete in its KeyDown). Commands `AddBookmark` (Ctrl+Shift+B) and `JumpBookmark1..9` (Alt+1…9, `CommandRegistry.JumpBookmark(n)`; `CommandDefinition.ShortcutText` prints digit keys as "Alt+1"), glyph `CommandGlyph.Bookmark` ★; legend `LegendSymbol.Bookmarks` (Badges). `TimeWindowReadoutViewModel` (− range +; follows the compare window via `FollowCompare` while Compare shows; the scope chip `ScopeText` "Scope 1,240.0–1,290.0 ms", `ScopeDepthText` "‹2", `BackCommand`/`ClearScopeCommand`, `FormatScope`; TitleBar `ScopeChip`/`ScopeBackButton`/`ScopeClearButton`). `AppMenu` (280 px, built from the registry: File/Session/View/Help, Theme segmented, Recent/Switch session/Schema/Overflow submenus as `MenuChoice`). `SessionEndedViewModel` + `SessionEndedBanner` (34 px strip: Save .nntl…/Attach to another session/Discard). `HelpDialog` (F1: registry commands by group + the views' gestures, `HelpRow`). `LegendDialog` (F2, View group, `AppCommand.Legend`): `Model/Legend` is the one table of every symbol (`LegendSymbol`/`LegendSection`/`LegendEntry`; `For(FrameFlags)`/`For(HexCellRole)` checked by enum-coverage tests), drawn by `Controls/LegendSwatch` with the views' draw paths; docs/articles/inspector.md "Reading the views" mirrors it. Standalone virtual-view ScrollBars (hex, A ⇄ B hex, compare rows) get the rail via `ScrollRail.Attach(bar, host)`, overlaid on the content; timeline lanes never change height on hover (only pin / Expand).
- **Dialogs/home**: `Controls/DialogHost` (row 1 modal layer, scrim `Opacity.Scrim.Dialog`, focus trap, `DialogHost.InitialFocus`, never dismissed by the scrim), `Controls/DialogFrame` (title, body, hint, `Footer`/`FooterLead`; Enter = `PrimaryCommand`, Esc = `CancelCommand`), `Controls/Banner` (`BannerKind`, `StatusTone`). `Views/Home` (`ActionCard` incl. dashed drop card, `ListCard` recents). `Views/Connect/ConnectDialog` (`ConnectDialogViewModel`; one session attaches directly, several go to the popover via `SessionChooser` + `IsPicking` ("Pick a session in the list ↗"), canceled with the dialog; the DialogHost focus trap leaves focus inside popups alone, or a popover row's press would be released and its click lost). `Views/OpenRecording/OpenRecordingDialog` (`OpenRecordingDialogViewModel`: info strip, session table (`Controls/DataTable/Sessions/`), schema mismatch banner + Embedded/Workspace/Raw radio rows, "Compare two sessions…"; `Failed(path, error)` state; `Model/RecordingText`). `UnsavedCloseDialog`, `Views/Inspect/GoToOffsetDialog`.
- **Inspect workspace** (`InspectWorkspaceViewModel` = `MainWindowViewModel.Inspect`; `Views/Inspect/InspectView`): timeline, left/right `StreamPaneViewModel` (`StreamPaneKind` Events/Ladder/Hex, header view controls from `StreamPaneView` templates, `IsSplit`), `InspectorViewModel`. `Controls/PaneHeader` (Tabbed/Inspector/Titled, synced badge, split button).
  - Timeline `Controls/Timeline/`: `TimelineControl` (one drawn control: overview, ruler, lane groups, marks, trace band, gap hatch, playhead; Ctrl+Wheel zoom, Wheel pan, Shift+Wheel lanes, ←/→ frames, P pin, `ContextLane`; range drag (I42/D1): overview box edge handles (`OverviewPartAt`, 6 px `HandleZone`, colour-only hover `HotHandle`, resize cursors) move start/end, middle pans, outside press centres, double-click `FitAll`; ruler drag-select via the shared `RulerSelection` (4 px threshold, band = accent.trace-band + solid accent.trace edges; release → `TimeWindow.Between`; a click keeps the playhead behaviour); Esc `CancelDrag` restores window/playhead/follow-live; every clamp reads `RangeBounds` (the sync bus's scope when set, else the capture: the overview and ruler re-span to a scope, label `OverviewLabel` "scope a – b ms"); Left/Right step frames only unmodified (Alt+Left is the window's Back); min span `TimeWindow.MinSpan` (10 µs) and `MinHandleGap` 4 overview px; `DragTo` allocation-free; window changes re-measure only when the lanes' height changes), `TimelineModel`/`TimelineLayout`/`LanePacker`/`LaneAnimator` (OQ12)/`TimelineProjection` (incremental from a `CaptureSession`)/`TimelineText`; `TimelineViewModel`.
  - Data table `Controls/DataTable/`: `DataTableControl` (ListBox + VirtualizingStackPanel; `Columns`, `RowHeight`, `HeaderHeight`, `IsLoading` skeleton, `EmptyText`, `FollowTail`, `ActivateCommand`, `SelectionBar`; bind `SelectedIndex`), `DataTableSource` (IList adapter, rows read by index), drawn `DataTableRow`/`Header`. Variants: `Events/` (`EventsTableSource` over `FrameOrder`; `EventsFilter` All/Inbound/Outbound keeps capture numbers; `EventsViewModel.Filter` = the pane's "Filter ▾" select), `Sessions/`, `FieldDiff/`.
  - Ladder `Controls/Ladder/`: `LadderView` over virtualizing `LadderPanel` of drawn `LadderRowControl`s; `LadderModel`/`LadderLayout` (compact 18 px or time-scaled, OQ6)/`LadderProjection`/`LadderTrace`; `LadderViewModel` (`Mode`, `IsSynced`).
  - Hex `Controls/Hex/`: `HexLayout` (lines per op/read rule/frames line; incremental `Update(session, HexDirections)`, `Read(first, span)`), `HexRowBuilder`/`HexRow` (inline arrays; `HexCellRole`, `HexCellMarks`), `HexText`, `HexLinker` (byte ↔ field/`FieldHover`, `AddHoverRanges`), `HexView` (AvaloniaEdit `TextView` holding only the visible window; colorizer, annotation generator, background renderer; `Bookmarks` (`HexRange`s) drawn as a diamond in the offset gutter). `HexViewModel` (IN/OUT never both off, bookmarks → `BookmarkMarks`), `HexPane`/`HexPaneHeader`.
  - Inspector: `Model/InspectorProjection` (Build/BuildHandshake → sections), `Controls/PropertyTree/` (`PropertyTreeModel` flat pre-order nodes, `PayloadMap`, virtualized `PropertyTreeView`, `AssemblyBar` chunk/greeting segments); tabs Decoded / Message trace (virtualized `TraceStepRow`s) / Handshake. Tree ↔ hex hover: single-frame rows use frame coordinates (offset 0 = first body byte, header negative; `FieldHover.FrameBytes`).
  - Menus `Views/Inspect/InspectMenus`: lane/group menu (`LaneMenuViewModel`), hex selection menu (`HexSelectionViewModel`: copy hex/C#/Base64, Decode as ▸ via `SyncBus.Decode` + Core `SchemaDecoder.DecodeMessagePack`, go to frame start/offset, bookmark Ctrl+B, export), `Model/SelectionProjection`, `Model/ByteText`.
- **Palette** (Ctrl+Shift+P): `Model/Palette/` (`FuzzyMatcher` allocation-free, `PaletteQuery` OQ14 prefixes `>` `#` `@` `:`, `PaletteRow`/`PaletteTarget`), `PaletteSearch` (reused key buffer under `Gate`, best per group, Entities → Frames → Commands → Sessions; "Open invocation #n in Compare" carries that entity), `CommandPaletteViewModel` (`Run`: entity/frame select + reveal, time jump, session activate, command — selecting a command's entity first), `Views/Palette/CommandPalette` (600 px, 56 px from the top, `Opacity.Scrim.Palette`, dismissable).
- **Compare workspace** (`CompareWorkspaceViewModel` = `MainWindowViewModel.Compare`; `Views/Compare/`): builds a `CompareSession` from the two ticked documents when Compare shows (on the UI thread; live docs refresh at most once a second, coalesced), `Align`, `RowMode` Heat/Packed, `OnlyDifferences`, `Entities` sidebar (`CompareEntityItem`; group ticks = `CompareRowsModel.SetGroupShown`, invocation groups start unticked), `Select`/`Detail`, `OpenInInspect` (double-click), `Show()` (`AppCommand.OpenInCompare`; selects the Inspect selection's entity), `CompareRecordingAsync` (= `Documents.SessionComparer`; reopens from the path). `Controls/Compare/`: `CompareRowsControl` (drawn ruler with the same `RulerSelection` drag-select → `CompareRowsModel.SetWindow`, Esc cancels; Δ strip, per-file rows), `CompareRowsModel` (rows, window in compare time, pinned entity), `CompareDensity`, `CompareText`, `CompareLegendSwatch`. Bottom panel `CompareDetailViewModel`: `FieldDiffViewModel` (Field | A | B rows from `EntityFieldDiff`, differing rows tinted) + `CompareHexViewModel`/`Controls/Hex/CompareHexView` (A/B interleaved rows, selected field ringed, differing bytes marked; click picks the field).
- **Control themes** (`Themes/`, OQ1 states: hover raised/strong border, focus 2 px inbound ring 1 px outside, pressed selected, disabled opacity): `Button` (classes `primary`, `record`, `caption`, `caption-close`, `split`, `md`), `SegmentedControl` (ListBox `Theme="{StaticResource Segmented}"`, `sm`/`dialog`, bound to `EnumChoice<T>`), `TextInput` (`mono`/`search`/`password`, `TextBlock.field-label`), `Select` (ComboBox, `sm`), `CheckBox` (14 px; class `outline` = 11 px outline tick), `RadioCard` (`RadioCard`/`RadioRow`), `Menus` (ContextMenu/MenuFlyout/MenuItem; classes `header`/`group`/`embedded`, menu `app` 280 px), `ToolTip` (OQ9), `ScrollRail` (app-wide ScrollViewer look; `Platform/SystemMotion`), `Dialog`, `Banner`, `Card`, `WindowChrome`, `DataTable`, `PaneHeader`, `PropertyTree(Rows)`, `MessageTrace(Rows)`, `HexView`, `Compare`. `ControlHint.Text` attached hint; drawn chips `FlagChip`/`CountBadge`/`TransportOpChip` (static `Draw` for dense rows).
- **Capture harness** (§8 screen checks): `Capture/CaptureScenarios.cs` (`CaptureScenario` → `CaptureSetup(Stem, Create, Height)`): main, control-gallery (`ControlGallery.Sections`, one gallery section per control), connect, inspect-live, open-recording, inspect-session-start, context-menus, command-palette, inspect-app-menu, session-ended, compare, legend, export-section, scoped (I43: nested scope, chip, re-spanned overview, out-of-scope trace steps), bookmarks (I44: three bookmarks, one scoped, the ★ dropdown open) (live ones use `Capture/LiveConnection`: a real in-process Chat server). `HeadlessCapture` renders 1440 px @2x to `<stem>--<light|dark>.png`. Run: `NEXNET_INSPECTOR_CAPTURE_DIR=<abs dir outside the repo> dotnet test src/NexNet.Inspector.App.Tests -c Release --treenode-filter "/*/*/CaptureTests/*"` (skipped without the variable); compare with the design PNGs, never commit renders.
- **Local AOT check**: `PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH" dotnet publish src/NexNet.Inspector.App -c Release -r win-x64 -o <dir>` then `<dir>/nexnet-inspector.exe --headless --self-check` (same for `src/NexNet.Inspector.Cli` + `nexnet-tap info`). linux-x64: `mcr.microsoft.com/dotnet/sdk:10.0` + `apt-get install clang zlib1g-dev`; copy `global.json src docs/images clients LICENSE` (the generator embeds the client runtimes) without bin/obj into the container, publish there; the self-check needs no display or fontconfig (App.Tests' Skia JIT on Linux needs `libfontconfig1`). CI: `.github/workflows/inspector.yml` (both OSes: build, Inspector.Tests + App.Tests with `--minimum-expected-tests` — raise it when adding tests, AOT publish of both with the IL-warning gate, `nexnet-tap info` on fixtures, app self-check, artifacts).

## 4. Integration Tests (src/NexNet.IntegrationTests/)

### 4.1 Base Classes

- `BaseTests.cs` - Abstract base: lifecycle (`[Before(Test)] BaseSetUp` creates the logger, then calls the virtual `SetUp()`; `[After(Test)] BaseTearDown` awaits the virtual `TearDown()`, which disconnects the clients and stops the servers and ASP.NET apps the test created, then flushes the log when the test failed; overrides of `SetUp()`/`TearDown()` call the base and carry no hook attribute), UDS endpoints from `NewUdsEndPoint()` in a per-test subdirectory of one run-wide temp directory (deleted after the test), transport type enum (Uds/Tcp/TcpTls/Quic/WebSocket/HttpSocket), config creation, port allocation (`FreeTcpPort()`/`FreeUdpPort()` delegate to `TestPorts`), TLS cert loading, cleanup tracking; `AspListenOptions` configures the Kestrel endpoint (e.g. HTTPS) for one test; `CreateMultiEndpointServer(types...)` builds one stopped server with an endpoint per transport (first = primary, others via `AddListener` or `MapNexusWebSocket`/`MapNexusHttpSocket` on one ASP app; optional nexus callback, per-config `configure`, ASP service/app hooks), returned as `MultiEndpointServer(Server, Configs, Transports, CreateClientConfig)`
- `BasePipeTests.cs` - Pipe test base: LogMode enum (None/OnTestFail/Always), Setup() with auto server/client connection
- `NexusCollectionBaseTests.cs` (Collections/) - Collection base: ConnectServerAndClient(), CreateRelayCollectionClientServers() for relay topology
- `BaseAspTests.cs` - ASP.NET base: AspCreateAuthServices(), AspAppAuthConfigure()

### 4.2 Test Infrastructure

- `NexusServerFactory.cs` - Generic server wrapper tracking created nexuses in ConcurrentQueue
- Test nexus interfaces and classes live in `src/NexNet.TestNexus/TestInterfaces/` (see 4.5)
- `Utilities.cs` - Dequeue<T>(), GetBytes<T>(), GetValue<T>(), InvokeAndNotifyAwait()
- `TaskExtensions.cs` - Timeout() overloads, AssertTimeout()
- `Collections/CollectionHelpers.cs` - WaitForEvent() extension, WaitForActionHandler
- `PipeStateManagerStub.cs` - IPipeStateManager mock
- `TestPorts.cs` - Process-wide loopback port allocator: walks 12000-31999 (below the OS ephemeral ranges) from a random offset, never hands a port out twice, skips ports not bindable for both TCP and UDP. Use it instead of binding port 0 and closing (that races under parallel runs)
- `TestTimeouts.cs` - Shared hang guards in seconds for load-dependent steps: `Connect` (server start/stop, client connect; 5) and `CollectionEvent` (5). Use them in `.Timeout(...)` instead of `1` for such steps
- `IntegrationParallelLimit.cs` - TUnit `IParallelLimit` for the assembly: half the logical processors (at least 2), or `NEXNET_TEST_PARALLEL`

### 4.3 Test Categories

**Client Tests (7 files):**
- `NexusClientTests.cs` - Connection lifecycle
- `NexusClientTests_Cancellation.cs` - CancellationToken handling
- `NexusClientTests_InvalidInvocations.cs` - Error cases
- `NexusClientTests_ReceiveInvocation.cs` - Receiving server calls
- `NexusClientTests_SendInvocation.cs` - Sending method calls
- `NexusClientPoolTests.cs` - Connection pooling
- `InvocationIdTests.cs` - ID management

**Server Tests (7 files):**
- `NexusServerTests.cs` - Lifecycle and basic ops
- `NexusServerTests_Cancellation.cs` - CancellationToken
- `NexusServerTests_ReceiveInvocation.cs` - Receiving client calls
- `NexusServerTests_SendInvocation.cs` - Broadcasting
- `NexusServerTests_NexusInvocations.cs` - Nexus-to-nexus
- `NexusServerTests_NexusGroupInvocations.cs` - Group invocations
- `NexusServerTests_Versioned.cs` - Versioning
- `NexusServerTests_Authorization.cs` - Authorization: allow/deny/disconnect, collections, caching (TTL, expiry, invalidation, concurrent access), multi-permission
- `NexusServerTests_MultiEndpoint.cs` - One server, many endpoints (`BaseAspTests`): endpoint order, broadcasts and collections across transports, `Context.Endpoint` (incl. ASP over HTTPS), per-endpoint `MaxMessageSize`/`Authenticate`/`Timeout`, primary `Authenticate` rule, `nexnet.endpoint` metrics, bind-failure rollback, rate limit across transports, `AddListener`/`AddExternalEndpoint` validation, hosted service lifecycle, `Use*NexusServerAsync` attach, added ASP endpoints' `AllowedOrigins` and query-string token

**Collections (Collections/, 24 files):**
- `NexusCollectionAckTests.cs` - Acknowledgments
- `NexusCollectionEventTests.cs` - Change events
- `NexusCollectionRelayTests.cs` - Relay mode sync
- `Lists/NexusListTests.cs` - CRUD operations
- `Lists/NexusListTests_Events.cs` - List events
- `Lists/FuzzTests.cs` - Random operation testing
- `Lists/Transform*Tests.cs` - Operation transforms (Insert/Remove/Modify/Move/Clear)

**Pipes (Pipes/, 18 files):**
- `NexusChannelReaderTests.cs` / `NexusChannelWriterTests.cs` - Channel ops
- `NexusPipeReaderTests.cs` / `NexusPipeWriterTests.cs` - Pipe ops
- `NexusDuplexPipeReaderTests.cs` / `NexusDuplexPipeWriterTests.cs` - Duplex ops
- `NexusClientTests_NexusDuplexPipe.cs` / `NexusServerTests_NexusDuplexPipe.cs` - Integration
- `NexusPipeWriterCloseRaceTests.cs` / `PipeCloseWriteRaceTests.cs` - A pipe closed or reset while a write or flush is in flight: buffers stay valid, no data after the Complete, the session survives

**Security (Security/, 10 files):**
- `AuthenticationTokenTests.cs` - Token validation
- `ProtocolSecurityTests.cs` - Protocol-level
- `RateLimitingTests.cs` - Rate limiting
- `ConnectionRateLimiterUnitTests.cs` - Unit tests
- `ServerVersionValidationTests.cs` - Version validation
- `RawTcpClient.cs` - Raw TCP for protocol testing

**Session Management (SessionManagement/, 6 files):**
- `LocalSessionRegistryTests.cs` - Registry ops
- `LocalInvocationRouterTests.cs` - Routing
- `LocalGroupRegistryTests.cs` - Group management
- `MockNexusSession.cs` - INexusSession mock with ShouldFailSend flag

**TLS and transports:**
- `TlsCertificateTests.cs` - Client certificate reaches server `Context.RemoteCertificate`, client sees server certificate, fingerprint pinning, untrusted certificate rejection (WebSocket/HttpSocket/TcpTls/Quic), Host header port. HTTPS ASP host via `BaseTests.AspListenOptions`
- `WebSocketPipeTests.cs` - Receive completing after the pipe is completed (fake `WebSocket`)
- `RemoteEndpointTests.cs` - RemoteAddress/RemotePort per transport

**Sockets (Sockets/, 1 file):**
- `StreamSendPipeTests.cs` - Stream send loop: flush while a write is pending, coalescing, failure, drain/abort

`BufferWriterTests.cs` (test root) - `BufferWriter` writes, `ReleaseTo`, `Deallocate`
`AsyncMutexTests.cs` (test root) - Write lock: try-enter, FIFO hand-over without barging, cancellation, stale/double release, waiter reuse, mutual exclusion under 16 workers
`CreditGateTests.cs` (test root) - Credit gate: waiters served first and in queue order, timeout, cancellation, close, waiter reuse, concurrent stress

**Metrics (Metrics/):**
- `MetricsRecorder.cs` - Test `IMeterFactory` with a private meter and a `MeterListener`; `Events()` for counters, `Observe()`/`ObserveSum()` collect observable instruments (sets `RefreshInterval = 0`)
- `MetricsSessionTests.cs`, `MetricsIoTests.cs`, `MetricsFlowControlTests.cs` - Instruments end to end; churn test checks the session table empties and shrinks
- `MetricsSessionTableTests.cs` - Slot reuse, shrinking, retiring sessions a disconnect missed
- `SnapshotTests.cs` - Session and server snapshots (partial item, writer waiting for credit, invocations)
- `DiagnosticLogTests.cs` / `CapturingLogger.cs` - Stall warning content, rate-limited reset warnings

### 4.4 Test Patterns

**TUnit:** the suite runs on TUnit in source-gen mode.

**Parallelism:** tests run in parallel, up to `IntegrationParallelLimit` at once (`[assembly: ParallelLimiter<...>]` in `GlobalUsings.cs`; `NEXNET_TEST_PARALLEL` overrides it). A new test must be parallel-safe: take ports from `TestPorts` (via `FreeTcpPort()`/`FreeUdpPort()` or the `CreateServerConfig`/`CreateClientConfig` helpers), UDS paths from `NewUdsEndPoint()`, and never depend on session ID values or other process-wide state. Bound load-dependent steps with `TestTimeouts`. Opt out only where needed:
- `[NotInParallel("stress")]`: stress tests (`Parallel.Invoke`, `Thread.Sleep`, high volume), metrics, session-budget, rate-limit and snapshot tests. They run alongside other tests but never alongside each other.
- `[NotInParallel("static-state")]`: tests that read or reset process-wide state (object pools, `ObjectCache`, pooled completion sources, static counters), e.g. `PoolingTests`, `PooledResettableValueTaskCompletionSourceTests`. Prefer private types or injected state where a test can avoid the shared state.
- `[NotInParallel]` (no key): tests that assert on wall-clock time; they run on their own.

TUnit creates a new instance per test, so per-class state goes in static fields set by static `[Before(Class)]`/`[After(Class)]` hooks (see `TlsCertificateTests`).

**Multi-Transport:** `[Test]` plus `[Transports]` (`TransportsAttribute.cs`) runs a test once per transport, passing it as the first `Type` parameter: `[Transports]` is every transport, `[Transports(Type.Uds, Type.Tcp)]` only those, `[Transports(Except = new[] { Type.Quic })]` all but those. Rows with more arguments use `[Arguments(Type.Tcp, true)]`; generated rows use `[MethodDataSource]`.

**Asserts:** every TUnit assert is awaited (`await Assert.That(x).IsEqualTo(y)`). `IsEqualTo` on a collection compares references: compare items with `IsEquivalentTo(expected, CollectionOrdering.Matching)` (in order) or `IsEquivalentTo(expected)` (any order). `ThrowsExactly`/`ThrowsExactlyAsync` match the exact exception type; `Throws`/`ThrowsAsync` accept subtypes. Group asserts with `using (Assert.Multiple()) { await ...; }`. In a callback that cannot await (sync lambda, nexus event handler, transport hook), capture the value and assert after the await, or use the throwing `CallbackAssert` helpers. `[Repeat(n)]` runs n + 1 times, and each run is listed as its own test.

**Async Pattern:** All async ops wrapped with `.Timeout(seconds)` using TaskExtensions

**Setup Pattern:**
```
var (server, client, clientNexus) = CreateServerClient(config, config);
await server.StartAsync().Timeout(TestTimeouts.Connect);
await client.ConnectAsync().Timeout(TestTimeouts.Connect);
```

**Event Assertions:** Set delegates on test nexus before operations; capture values in the callbacks and assert after awaiting them

### 4.5 Shared Test Nexuses (src/NexNet.TestNexus/)

A library of the nexuses the tests use, referenced by IntegrationTests (and, through `InternalsVisibleTo`, by the interop
harness, the golden vector tool and Serialization.Tests). Not packaged.

**TestInterfaces/** (moved from IntegrationTests; namespace still `NexNet.IntegrationTests.TestInterfaces`):
- `BasicTestsInterfaces.cs` - IClientNexus, IServerNexus interfaces; ClientNexus, ServerNexus implementations with event callbacks
- `VersionedTestsInterfaces.cs` - Version-specific test interfaces
- `AuthorizationTestInterfaces.cs` - Authorization test nexuses with delegate handlers and cache invalidation helpers
- `SeparatePoolTestInterfaces.cs` - Nexuses for the separate-pool tests

**Interop/** (namespace `NexNet.TestNexus.Interop`): the interop type zoo for the cross-language harness. Wire layouts
are part of the interop contract: never change keys, tags or enum values; add members and types instead.
- `IInteropServerNexus.cs` / `InteropServerNexus.cs` - Echo method for every zoo type (spec §10), plus pipes, channels,
  collections, cancellation, authorization (`admin`/`reader` identities) and token validation via
  `NexNet.InteropServer`. `Targets = Java | JavaScript`
- `IInteropClientNexus.cs` / `InteropClientNexus.cs` - Callbacks the server invokes; echo by default, scripted per test
- `InteropTypes.cs` - Zoo types: enums (int, flags, long beyond 2^53), objects, unions, nullable and collection shapes,
  and `InteropPoint` with a user formatter mapped by `NexusTargetFormatter` to Java and JS
- `InteropVersionedNexus.cs` - Versioned pair: V2 server class plus a V1 `NexusClientExport`

The project imports the generator's `buildTransitive` props/targets and writes the Java and JS clients to
`artifacts/interop/<lang>/generated` (gitignored).

`IntegrationTests/Interop/InteropZooTests.cs` smoke-tests the zoo with the C# client over UDS, and the test-only raw
frame injection hook `NexusSession.InternalSendRawAsync` (`Internals/NexusSession.Sending.cs`), which writes a
pre-built frame to the session's transport.

---

## 5. Generator Tests (src/NexNet.Generator.Tests/)

### 5.1 Infrastructure

`CSharpGeneratorRunner.cs` runs NexusGenerator with source code via InitializeCompilation() and RunGenerator() returning Diagnostic[].

### 5.2 Test Files

- `GeneratorTests.cs` - Class modifier validation (partial, not abstract), interface validation
- `GeneratorChannelTests.cs` - Channel parameter generation
- `GeneratorPipeTests.cs` - Pipe parameter generation
- `GeneratorCollectionTests.cs` - Collection property generation
- `TypeHasherTests.cs` - Shape listings (`AssertWalk`) and hash rules (`HashOf`) of `ShapeBuilder` + `ShapeHasher`, called directly (no test generator)
- `ShapeBuilderTests.cs` - Shape model: key order, `[NexusIgnore]`, constructed generics, cycles, nullability
- `VersioningTests.cs` - Version string parsing, [NexusVersion] + [NexusMethod(id)] validation
- `GeneratorAuthorizationTests.cs` - Authorization attribute validation: client nexus error, missing OnAuthorize, mixed enums, non-int enum, cache duration, collection auth

**Test Pattern:** Pass C# source string to RunGenerator(), check Diagnostic[] for expected IDs (MustBePartial, etc.)

Also: `GeneratorSerializationTests.cs` - serialization diagnostics, formatter generation, merged-file deduplication and incremental caching.

---

## 5a. Serialization Tests (src/NexNet.Serialization.Tests/)

- `ReaderTests.cs`, `ReaderCursorTests.cs`, `WriterGoldenTests.cs` - Reader/writer behavior and golden byte vectors
- `FormatterTests.cs`, `PrimitiveArrayTests.cs`, `GeneratedFormatterTests.cs` - Built-in, ext 78 and generated formatters
- `ProtocolMessageTests.cs` - Hand-written message bodies
- `PropertyTests.cs` - Property-based round trips
- `FuzzSmokeTests.cs` - Replays the fuzz seeds and `NexNet.Fuzz/Corpus/*.hex` through every harness; a harness may only throw `NexusSerializationException`

MessagePack-CSharp is referenced here only, for interop cross-checks; shipping packages do not use it.

## 5b. Fuzzing (src/NexNet.Fuzz/)

Harnesses (`Harnesses.cs`, `SessionHarness.cs`): `reader`, `messages`, `channel`, `formatters`, `builtins`, `session`.
```
NexNet.Fuzz <harness>                     # run under libFuzzer (SharpFuzz-instrumented build)
NexNet.Fuzz --smoke [corpusDir]           # every harness over the corpus, no fuzzer
NexNet.Fuzz --repro <harness> <hex> [n]   # reproduce one input n times
NexNet.Fuzz --export-corpus <dir>         # write every seed as a file for a libFuzzer corpus
```
Add each new crash input to `Corpus/*.hex` so `FuzzSmokeTests` keeps replaying it.

---

## 5c. Java and JavaScript Clients (clients/)

Client-only runtimes; the server stays .NET. User docs: `docs/articles/multi-language-clients.md`,
`javascript-client.md`, `java-client.md`. Contracts (frozen; changes need a contract amendment):
`docs/internals/js-runtime-contract.md`, `java-runtime-contract.md`, `nexnet-ir.md`, `interop-control-protocol.md`,
`tests/golden/README.md`.

**Pipeline.** The generator builds a language-neutral IR (`NexNet.Generator/Ir/`, also written as `nexnet-ir.json`),
validates it per target (`Validation/TargetValidator.cs`: NEXNET040-048), and each `Targets/<Lang>/` emitter
(`ITargetLanguage`, `TargetRegistry`) writes proxies, client nexus bases, types and formatters. The runtime sources
under `clients/` are embedded in `NexNet.Generator.dll` (`Runtime/EmbeddedRuntime.cs`; the clients are not published, so the generator carries everything a consumer needs) and emitted beside the
generated code. Everything travels in the comment-only `NexNet.Targets.g.cs` bundle (`Bundle/`), unpacked after
`CoreCompile` by `build/NexNet.Generator.targets` (task in `build/NexNet.Generator.Tasks.cs`; the bundle parser is
duplicated in `Bundle/TargetBundle.cs`). `docs/internals/generator-emitted-runtime.md` covers the emitted runtime.

**JavaScript** (`clients/js`, npm workspace): `packages/nexnet` is the runtime: ESM, JSDoc, zero dependencies,
entries `src/index.js` (neutral), `src/browser.js` (WebSocket only, never imports `node:`) and `src/node.js` (adds
HttpSocket/TCP/TLS/UDS). `src/contract/` is the generated-code API. Tests: Vitest (`test/`, Node; browser mode with
Playwright). Gate: `npm ci --prefix clients/js`, `npm test --prefix clients/js`, `npm run typecheck --prefix clients/js`.

**Java** (`clients/java`, Gradle Kotlin DSL, wrapper committed): `nexnet-client` is the runtime (package
`com.dtronix.nexnet`, `--release 11`, Android API 24 via Animal Sniffer, no runtime dependencies; `spi` is the
generated-code API). Gate: `clients/java/gradlew -p clients/java check` (JDK 21).

Both runtimes version in lockstep with `src/NexNet/NexNet.csproj`. Editing a runtime file changes what the generator
emits, so it rebuilds the generator. Language gates run when a change touches `clients/<lang>/**`,
`Ir/**` or `Targets/<Lang>/**`.

## 5d. Cross-language Test Harness (src/NexNet.Interop/)

C#-authored TUnit tests run against every client language. `dotnet build src`/`dotnet test` never invoke npm,
Gradle, Node or Java unless `NEXNET_TEST_LANGS` or `NEXNET_REQUIRE_SDKS=1` is set.

- **Rows.** `[ClientMatrix(Transports = [...], CSharpTransports = [...], Requires = ClientCapabilities.X)]` makes one
  row per language of `NEXNET_TEST_LANGS` (`csharp` always, first) times transport; the test takes a `ClientTarget`.
  Only applicable rows are generated: a language that lacks the transport or a capability of `Requires` gets no row
  (`ClientLanguages.cs` holds each language's transports, `ClientCapabilities` and variants; a host's hello can only
  narrow them). Declare what the test needs in `Requires` (`ClientPool` for pools) instead of skipping in the body; a
  test of C#-only API by design sets `Languages = [ClientLanguages.CSharp]` and is a parity exception. A language's
  variants add their own rows: every Java `WebSocket`/`WebSocketTls` row also runs on OkHttp (`java/WebSocket(okhttp)`,
  `ClientTarget.Variant` → `ClientSpec.WebSocketConnector`). Filtering: see the top of this file (`[Variant=okhttp]`
  selects the variant rows).
- **Skips.** In CI a matrix row may be skipped only for a reason that cites a card: `Skip.Test("K60: ...")` (a card id,
  a colon and a space; `MatrixCoverage.IsCited`). With `NEXNET_MATRIX_REPORT=<dir>` the session writes the coverage
  report (`matrix-coverage.json`/`.md`: rows run, passed, failed and skipped per language and variant, and the
  combinations left out by reason), and in CI (`CI=true` or `NEXNET_REQUIRE_SDKS=1`) fails on an uncited skip. Local
  runs may still skip for their environment (no msquic).
- **Tests.** Derive from `IntegrationTests/MatrixTestBase.cs` (takes the `LanguageHostPool`): start an
  `InteropServer` (`NexNet.InteropServer`: one server with every endpoint kind), create the client with
  `Harness.CreateClientAsync(target, spec)` → `IClientUnderTest`, and assert on the server or through the client
  under test (invoke, pipes, channels, lists, scripted client nexus, `IPoolUnderTest` for pools).
- **Drivers.** `InProc/`: the C# client in process. `Remote/`: drives a language host over the JSON control protocol
  (`docs/internals/interop-control-protocol.md`); `Hosts/`: launches, pools, supervises and restarts host processes
  (`host.json` launch config, `HostBuildCommands`), with a per-test leak audit (`LeakReport`).
- **Hosts.** `clients/js/packages/interop-host-node`, `interop-host-browser` (Playwright: Chromium, Firefox, WebKit),
  `clients/java/interop-host`. They run clients generated by `src/NexNet.TestNexus` with `EmitTestScaffolding` into
  `artifacts/interop/<lang>/generated` (gitignored) and the workspace runtime.
- **Golden vectors.** `NexNet.GoldenVectors` writes `tests/golden/*.jsonl`; Serialization.Tests, the JS Vitest suite
  and the Java JUnit suite all check them. Regenerate after a serializer or message change.
- **Parity audit.** `IntegrationTests/Interop/ClientParityTests.cs` fails when a matrix test has no runnable row on a
  language (its transports or capabilities by the built-in table, or `Languages`), checks part of its behavior on C# only (`InProc`,
  a branch on `ClientLanguages.CSharp`, or a check or cast to an in-process driver type such as
  `client as InProcClientDriver<,>`), or branches on any other language (a `ClientLanguages` name or the `"browser-"`
  family, an `IsBrowser(target)` helper, `client is RemoteClientDriver`) and checks something else there. It scans the
  IL of the test and of the helpers it calls. Each gap needs an entry in `Interop/ClientParityExceptions.cs` with its
  reason (platform limit, by design, or the card that closes it); a helper whose driver-type check only adds a C# check
  on top of an all-language one is exempt in `ClientParityExceptions.DriverTypeChecks` (`AssertNotOutstanding`). The
  audit also fails on an entry no longer needed. Keep `docs/articles/multi-language-clients.md` "Parity exceptions" in
  step.
- `src/NexNet.Interop.Tests` tests the harness itself (framing, hello, host launch and crash handling, drivers).

---

## 6. Key Architectural Concepts

### Protocol
Spec: [docs/internals/protocol-specification.md](docs/internals/protocol-specification.md) (wire protocol version 1).
- **NnP Header:** 8 bytes `[N][n][P][0x14][reserved x3][Version=1]`; any mismatch -> `ProtocolError`
- **Framing:** type byte, little-endian `uint16` body length (types with a body), post-header (pipe ID for `DuplexPipeWrite`/`PipeWindowUpdate`, stream key for `MessageChunk`), body
- **Bodies:** MessagePack arrays, hand-written per message (src/NexNet/Messages)
- **Handshake:** Client greeting -> Server greeting with version hash validation

### Serialization
- **NexNet MessagePack serializer** (src/NexNet/Serialization) for arguments, results, channel and collection items
- Generator emits formatters for `[NexusObject]` types; registry lookup at runtime, no reflection (Native AOT clean; CI publishes the AOT sample and fails on any IL warning)
- Arguments and results up to `MaxMessageSize` (4 MiB untrusted / 16 MiB trusted); larger than one frame are streamed (MessageStart/Chunk). Flow control and limits: docs/internals/flow-control.md

### Session Lifecycle
1. Client: Create -> ConnectAsync -> Proxy.Method() -> DisconnectAsync
2. Server: Create -> StartAsync -> Accept connections -> OnConnected -> StopAsync
3. Session: ITransport -> NexusSession -> Handshake -> InvokeMethod routing -> Disconnect

### Proxy Modes
- All, AllExcept, Client, Clients, Group, Groups, GroupExceptCaller, GroupsExceptCaller

### Collection Sync
- ServerToClient: One-way push
- BiDirectional: Two-way sync
- Relay: Read-only hierarchical distribution

### Generated Code
- `CreateServer(config, nexusFactory)` / `CreateClient(config, nexus)` factory methods
- Proxy implementations with method invokers
- Collection property getters/setters

---

## 7. File Quick Reference

**Add new transport:** Implement ITransport + ITransportListener; create Config extending ClientConfig/ServerConfig

**Add hub method:** Define in interface; generator creates invoker automatically

**Add collection:** Define INexusList<T> property with [NexusCollection(mode)] attribute

**Add test:** Inherit from appropriate base (BaseTests/BasePipeTests/NexusCollectionBaseTests); mark it `[Test]`, add `[Transports]` (or `[Transports(Type.X, ...)]`) for transport coverage, make it `async Task` and await every assert

**Change the JS/Java runtime:** edit `clients/<lang>/`, keep the contract doc in step, run the language gate; the generator re-embeds the sources on its next build

**Add a cross-language test:** `[Test, ClientMatrix(...)]` on a `MatrixTestBase` subclass, taking `ClientTarget`; run other languages with `NEXNET_TEST_LANGS=node,java`

**Debug generator:** Use Generator.Tests with CSharpGeneratorRunner.RunGenerator(); check Diagnostic[] output

**Add built-in serializable type:** formatter in `Serialization/Formatters/`, register in `BuiltInFormatters`, teach `SerializationBuilder` (and `ChannelTypeAnalyzer` if pre-registered) about it, document it in spec §5.4

**Inspector:** tests `dotnet test src/NexNet.Inspector.Tests -c Release` and `src/NexNet.Inspector.App.Tests` (raise `--minimum-expected-tests` in `inspector.yml`); UI change → run the capture harness (§3.6) and compare with the design PNGs; Chat nexus/format/tap change → regenerate fixtures (§3.5)

**Change a message body:** edit the hand-written `Serialize`/`Deserialize` in `Messages/`, update `ProtocolMessageTests`, the spec, and the fuzz corpus if seeds change
