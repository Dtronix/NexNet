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
- Commands: `list` (policy + live sessions), `record --session <id|first|all> --out f.nntl [--duration] [--overflow drop|block] [--no-snapshot] [--wait] [--include-secrets] [--force]`, `info f.nntl [--json] [--messages]`, `repair f.nntl [--force]`, `help [command]`, `--version`. Connection options: `--endpoint tcp://host:port | tls://host:port | uds:/path`, `--token` or env `NEXNET_TAP_TOKEN`, `--insecure` / `--server-name` (tls only), `--connect-timeout`. Durations `250ms`/`30s`/`5m`/`1h30m` (bare number = seconds)
- Exit codes (`ExitCode`): 0 ok (record: also Ctrl+C/duration/session closed), 1 failure (unreachable, token or TLS refused, I/O, unreadable file, recording without footer), 2 usage, 3 refused by server config ("Recording disabled by server config", `--include-secrets` without `IncludeSecrets`), 4 session not found / none within `--wait`, 130 Ctrl+C before recording started
- `CliArguments.TryParse`: hand-written, enum-based (`CliCommand`, `[Flags] CliOption` with per-command `Allowed` masks); `EndpointAddress` (record struct) parses addresses and builds the `ClientConfig`. `TapCli(TextWriter output, TextWriter error).RunAsync(args, envToken, ct)` runs a command line; `RecordingStarted` callback and `DrainTimeout` exist for tests
- `record`: checks `GetServerInfo` policy first, then `RecordAllAsync` or `AttachAsync(Record: true)` (client role → `InspectorTargetKind.Client`; an ID matches `Id` or `ConnectedId`); `InspectorStream.CopyToAsync` straight into the file, not cancelable: Ctrl+C/duration calls `DetachAsync` and waits for the close blocks + footer; then verifies the footer with `NntlReader`
- `info`: `RecordingAnalyzer.Analyze(stream, name, messages)` → `RecordingSummary` (record; rows are record structs) in one pass over `Index.ToFileOrder()`: per session ops/bytes/redacted per direction, frames via the I02 `FrameParser` (preamble expected only when the first op starts with it; framing stops at a gap), gaps, snapshot, schema hash + embedded, close; `--messages` also runs the I03 `MessageAssembler` (keeps bytes in memory). `--json` via source-generated `CliJsonContext` (string enums, camelCase)
- `repair`: `NntlFile.RebuildIndex`; a file whose footer is intact is opened read-only and left untouched unless `--force`
- Tests: `src/NexNet.Inspector.Tests/Cli/` (argument parsing, list/record first|id|all over TCP and TLS endpoints via `TapHarness.StartEndpointAsync(transport)` + `EndpointAddress`, policy refusal, info vs reader counts, info JSON, repair truncated/intact)

### 3.5 NexNet.Inspector.Sample (src/NexNet.Inspector.Sample/)

The "Chat" sample (Exe, `NexNetEmitInspectorSchema=true`): the traffic the inspector screens show, and the generator of the test fixtures.
- `ChatNexus.cs`: `IChatServer` (`Join`, `SendMessage(…, CancellationToken)` held by a `/slow` text until canceled, `SendAttachment` (300 KB → MessageStart + 5 chunks), `UploadFile` raw FileUpload pipe, `TailLogs` `Channel<LogLine>`, `Users` ServerToClient `INexusList<long>`) / `IChatClient`. `LegacyChatNexus.cs`: an older API whose schema hash differs (open-recording mismatch). A list of a `[NexusObject]` type cannot send a non-empty reset to a second client yet (no `Memory<T>` formatter in `NexusListServer.SnapshotMessages`), hence `long` user IDs
- `ChatScenario`: Full / Light runs (pipe 1 Users, Join, a canceled SendMessage, pipe 2 small upload, pipe 3 FileUpload while the attachment streams, pipe 4 LogLine channel, optional burst on pipe 5). Clients send a 96-byte token and ping every 400 ms; `ChunkPacer` (an `INexusTap` wrapper in front of the client inspector) holds each MessageChunk flush 250 ms so pipe frames and a Ping interleave with the chunks
- `RecordingAnalysis` replays a `.nntl` through `NntlReader` → `FrameParser` → `MessageAssembler` → `SessionDecoder` (embedded schema); `Fixtures.Check(kind, analysis)` lists what a fixture lacks; `FixtureGenerator` records them (retrying a run whose timing missed)
- Fixtures (`src/NexNet.Inspector.Tests/Fixtures/`, binary in git): `chat-server.nntl` (Full session recorded server side, Drop mode, recorder stalled during the burst → gap blocks), `chat-client.nntl` (the same session recorded client side, Block mode; Compare pair), `server-all.nntl` (endpoint record-all: 3 sessions, one with a snapshot), `legacy-schema.nntl` (2 legacy sessions, schema hash ≠ current Chat), `server-all-truncated.nntl` (no footer, torn last block). Regenerate after a Chat nexus, format or tap change: `dotnet run --project src/NexNet.Inspector.Sample -c Release -- fixtures` (default directory `src/NexNet.Inspector.Tests/Fixtures`); `FixtureTests` check the checked-in files, `FixtureGeneratorTests` that the command still produces valid ones
- Live (Phase-1 gate): `dotnet run --project src/NexNet.Inspector.Sample -c Release -- serve --transport tcp --port 7711 --endpoint tcp://127.0.0.1:7712 --token sample-token --allow-recording` (TLS: `--transport tls --port 7721 --endpoint tcp://127.0.0.1:7722`, self-signed certificate unless `--cert FILE.pfx`; UDS: `--transport uds --path SOCKET`; `--record FILE.nntl` records server side), then `… -- client --transport tcp --port 7711` (`--iterations N`, `--delay ms`, `--burst lines`). Record from the endpoint with `nexnet-tap record` and read the file with `nexnet-tap info FILE.nntl`; `… -- check FILE.nntl` replays a recording with the core and reports framing faults
- Cross-language capture: `IntegrationTests/InspectorCaptureTests.cs` (`[ClientMatrix]` on Tcp, TcpTls, WebSocket, WebSocketTls): a `NexusInspector` installed on every interop endpoint records a session driven by each client language; the server-side bytes must frame and assemble (preambles, both greetings, a call and its result)
- CI: `.github/workflows/inspector.yml` (ubuntu-26.04 linux-x64, windows-latest win-x64): build src, `dotnet test src/NexNet.Inspector.Tests`, Native AOT publish of `nexnet-tap` with the IL-warning gate, `nexnet-tap info` on two fixtures, binaries uploaded as artifacts

### 3.6 NexNet.Inspector.App (src/NexNet.Inspector.App/)

The desktop app (`nexnet-inspector`, Avalonia 12.1.3 + Fluent, AvaloniaEdit 12.0.0, CommunityToolkit.Mvvm 8.4.2), Native AOT on win-x64 and linux-x64 (zero trim/AOT warnings on both). Compiled bindings only (`x:DataType` everywhere), no reflection; `InvariantGlobalization`, `BuiltInComInteropSupport=false`. Tests: `src/NexNet.Inspector.App.Tests` (TUnit + Avalonia.Headless/Skia; `InternalsVisibleTo`).
- Layout: `Program.cs` (`AppOptions.TryParse`: `[Flags] AppSwitches`; bad arguments → exit 64), `App.axaml(.cs)` (composition root; style order Fluent → `avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml` → `Themes/Controls.axaml`), `Themes/Controls.axaml` (merge point: merges `Themes/Tokens.axaml`, later control themes go here), `Themes/Tokens.axaml` (tokens as `Color.*`/`Brush.*` per Light/Dark ThemeDictionary, `Font.Ui`/`Font.Mono`, `FontSize.*`, `Radius.*`, `Size.TitleBar`; views use colours only via DynamicResource), `Views/MainWindow` (MinWidth/MinHeight 1100×700, the `Views/Shell/TitleBar`, empty state; `Restore(WindowPlacement?)` / `Placement`), `ViewModels/MainWindowViewModel` (`[ObservableProperty]` partial properties, `[RelayCommand]`), `Settings/`, `Services/ThemeService`, `Checks/SelfCheck`
- Assets: fonts in `Assets/Fonts/Atkinson` (Atkinson Hyperlegible Next Regular/Medium/SemiBold/Bold) and `Assets/Fonts/JetBrainsMono` (JetBrains Mono NL Regular/Medium/SemiBold), only the weights tokens.json uses, as AvaloniaResource (`avares://nexnet-inspector/Assets/Fonts/<folder>#<family>`); each `OFL.txt` is copied beside the exe (`AtkinsonHyperlegibleNext-OFL.txt`, `JetBrainsMonoNL-OFL.txt`). The logo is `docs/images/logo-128.png`/`logo-256.png` linked as `Assets/Logo/*` (window icon: logo-256)
- Settings: `SettingsPaths` → `%LOCALAPPDATA%\NexNetInspector\settings.json` (Windows) or `$XDG_CONFIG_HOME/nexnet-inspector/settings.json` (absolute only), else `~/.config/nexnet-inspector/settings.json`. `AppSettings` (record, setters not `init`: the STJ source generator would zero init-only properties a file omits): `Theme` (`AppTheme` System/Light/Dark), `RecentFiles`/`RecentEndpoints` (record structs, newest first, `MaxRecent` 10, `WithRecentFile`/`WithRecentEndpoint`), `Window` (`WindowPlacement`), `Connect` (`ConnectDefaults`: endpoint, `InspectorTargetKind`, `InspectorOverflow`, snapshot; never the token). `SettingsJsonContext` (source-generated, camelCase, enums by name, comments/trailing commas allowed). `SettingsStore`: `Load` → `SettingsLoadOutcome` (Missing / Loaded / Corrupt: copied to `settings.json.bak`, defaults used / Unreadable), `Normalized()` trims hand-edited lists; `Save`/`Update` write `settings.json.tmp`, flush to disk, move over (atomic), return false on I/O failure
- Theme (OQ15): `ThemeService(Application, IOsThemeSource, AppTheme)` sets `RequestedThemeVariant` to an explicit Light/Dark; System re-resolves on every OS change (`PlatformThemeSource` wraps `IPlatformSettings.ColorValuesChanged`; off-thread changes are posted to the UI thread). Tests drive a `FakeOsTheme`
- Documents and sync (I18): the capture model is in Core `Session/` (no UI types): `CaptureSession` (one session: `TransportOpStore` per direction, `FrameParser`s, `MessageAssembler`, `SessionDecoder`, `FrameOrder` across both directions, gaps, snapshot, close; fed block by block under its `Gate`, which readers also take), `RecordingFile` (`SharedNntlReader`; `Summarize`, `CreateSession(id, workspace, SchemaChoice)`, background `Index`/`IndexAsync` in batches with `IndexingProgress`; `RecordingWarnings` IndexFromScan/Damaged, OQ3), `LiveCapture` (attach stream blocks → session), `SchemaResolution` (`SchemaStatus` Missing/Embedded/Mismatch/Unreadable, `SchemaSource`). Stores: `LiveOpStore` (slab copies, bounded, trims oldest) and `RecordingOpStore` (block offsets + LRU byte cache; never loads the file). Gaps: `FrameParser.Gap` + `FrameParserStart.Resync` (header-chain evidence; outbound ops start at frames; probation re-resyncs) and `MessageAssembler.Gap`; attach snapshots: `MessageAssembler.Seed(SnapshotSeed)`. `Core/Model/TimeWindow` (ticks; zoom/pan/clamp/pixels/follow/rebase). App: `Model/` (`Selection`, `FieldHover`, document enums), `Services/` (`IUiDispatcher` + `AvaloniaUiDispatcher` (the only Avalonia edge), `ChangeCoalescer` (one UI post per burst), `SyncBus` (window, bounds, playhead, selection, hover, follow-live; `Changed` once per UI turn with `SyncChanges` flags), `EndpointAddress` (port of the CLI's, plus a default scheme), `InspectorConnection` (connect, `ProbeAsync` → `EndpointProbeResult`)), `ViewModels/` (`DocumentsViewModel`: open/activate/close, Ctrl+Tab in MRU order, `ClosePrompt`/`SaveHandler`/`SessionPicker` hooks for I29/I24, shared window rebased per document, recents; `FileDocumentViewModel`; `LiveDocumentViewModel`: attach/detach (detach resets), `LiveState`, `IsDirty`, `RecordingAllowed`; `ConnectViewModel`: probe after 500 ms or on blur, Connect → sessions → Attach). Tests: `src/NexNet.Inspector.Tests/Session/`, `src/NexNet.Inspector.App.Tests/Documents/` (`ChatServerHarness`: tapped in-process Chat server + endpoint; `TestUi`/`QueuedUi` dispatchers)
- Self-c- Window chrome (I16): `Views/Shell/TitleBar` (x:DataType `ShellChromeViewModel` = `MainWindowViewModel.Chrome`): logo → `RequestCommand(ShellRequest.AppMenu)`, Inspect|Compare `Segmented` on `EnumChoice<Workspace>`, `DocumentTabStrip` (a `DocumentTabList : TabStrip` with `SelectionMode.Single`, so it mirrors `DocumentsViewModel.Active` and never auto-activates; ✕/middle-click → `CloseDocumentCommand`, which asks `ClosePrompt` (OQ5); Compare checkboxes ↔ `DocumentViewModel.IsIncludedInCompare`, max `DocumentsViewModel.MaxCompared` = 2 (OQ11)), `SessionArea`/`TimeWindowArea` slots (empty state: Connect… and "— no timeline —"; I28/I19 fill them), caption buttons. Every interactive element in the bar needs `WindowDecorationProperties.ElementRole` (User or its caption role; `TitleBarTests` checks). `ChromeMode` (`WindowChromePolicy`): Extended (Windows/macOS: BorderOnly + `ExtendClientAreaToDecorationsHint`, `Platform/TitleBarRecovery` re-applies it after a display/session change) or Undecorated (Linux: `WindowDecorations.None`, the bar drags/double-click maximizes, `ResizeGrips`). OQ10 collapse: `Model/ShellLayoutPolicy.For(width)` → `ShellLayoutState` Full / RightPaneClosed (<1200) / InspectorNarrow (<1150, inspector 280); `MainWindow` feeds `Chrome.UpdateWidth`. Ctrl+Tab/Ctrl+Shift+Tab: `MainWindow` tunnel KeyDown → `Documents.Cycle`. Tab titles: `Live · 0x3A21`, `file.nntl › 0x3A21`; markers `TabMarker` (files alternate inbound/outbound in opening order)
heck: `nexnet-inspector --headless --self-check` (headless platform drawn by Skia + HarfBuzz; `--self-check` alone is refused) runs `SelfCheck.Run` on the UI thread: main-window (bound, 36 px title bar, min size), render (a frame with content), themes (bottom-left pixel = `Color.Surface.App` in Light and in Dark), fonts (every token weight resolves to the bundled family and weight), editor (AvaloniaEdit templated, 3 visual lines, drawn). Prints one line per step + `self-check: PASS|FAIL`, exit 0/1; never touches the user's settings. CI runs it on both AOT binaries
- Menus, tooltip, scroll rail (I15): `Themes/Menus.axaml` (type-keyed ControlThemes for `ContextMenu`, `MenuFlyoutPresenter`, `MenuItem`, app-wide: surface.panel + border.strong, 8 px corners, `Shadow.Menu`, 26 px items with an 18 px icon/✓ column, mono 10.5 px gesture hints, state.selected hover/selection, 1 px inbound ring on `:focus-visible`; MenuItem classes `header` (24 px context header), `group` (10 px bold capitals via `MenuText.Upper`), `embedded` (28 px row hosting any control as its Header, `StaysOpenOnClick`); menu class `app` = 280 px, no icon column; opacity fade `MenuMotion.Fade`), `Themes/ToolTip.axaml` (OQ9 menu surface, 11 px, wraps at 320 px; the 400 ms delay is Avalonia's `ToolTip.ShowDelay` default, which does not inherit), `Themes/ScrollRail.axaml` + `Controls/ScrollRail.cs` / `RailScrollMath.cs` (Foreman port: every ScrollViewer gets the rail via an app-wide style; attached `ScrollRail.IsEnabled`/`ProximityZone`; OQ7 thumb colours `Brush.Scroll.ThumbHover/ThumbDrag`), `Platform/SystemMotion.cs` (`ShowsAnimations()`/`ReducesMotion()`: Windows `SPI_GETCLIENTAREAANIMATION` via `[LibraryImport]` (hence `AllowUnsafeBlocks`); elsewhere a no-op reporting animations on), `Controls/MenuMetrics.cs` (widths/delay no token names). Numbers no token names stay in `MenuMetrics`/`RailScrollMath`
- Inspect workspace + inspector (I23): `MainWindowViewModel.Inspect` (`InspectWorkspaceViewModel(documents, chrome, paneFactory?)`: `Timeline` (I19), `Left`/`Right` `StreamPaneViewModel` (`View` = `EnumChoice<StreamPaneKind>` Events/Ladder/Hex, `Content` = the view's model made once per kind by `paneFactory(kind, side)`, default `EventsViewModel`/`LadderViewModel`/`HexViewModel` over the documents; a ladder's `IsSynced` follows the pane's; `IsSynced` → badge; `SplitAction`/`SplitCommand`/`CanSplit`), `Inspector`, `IsSplit` (user choice, kept across OQ10 collapse), `ShowsRightPane` = split && `Chrome.ShowsRightPane`, `InspectorWidth`, `IsShown` = documents && Inspect). `Views/Inspect/InspectView` (timeline ≤ `TimelineShare` of the height; panes `Grid` whose 2nd column collapses in code; inspector column) + `StreamPaneView` (pane view from `InspectView`'s DataTemplates keyed by the content type: `EventsView`, `LadderView`, `HexPane`; header view controls from the pane's own `ViewControls` DataTemplates: Filter ▾ · Follow live, the `sm` rows select, `HexPaneHeader`) + `InspectorPane`. `Controls/PaneHeader` (TemplatedControl, `Themes/PaneHeader.axaml`: `Variant` Tabbed/Inspector/Titled, `Tabs` labels + two-way `SelectedIndex`, `ViewControls`, `Trailing`, `SyncState` None/Synced/Unsynced, `SplitAction` None/Single/Split/Close + `SplitCommand`/`CanSplit`; pseudo-classes per state). Property tree (`Controls/PropertyTree/`): `PropertyTreeModel` (flat pre-order `PropertyNode` structs with `SubtreeEnd`; `Section`/`Field`/`Decoded(DecodedValue, PayloadMap, rawName)` (arguments flattened, raw = one muted field), `Complete()`; `Rows` = `PropertyRowList` (IList + range Add/Remove on `Toggle`/`SetExpanded`; `PropertyRow` objects made when read, decoded text via `FormatValue`/`GetText` on first `Value` read: `FormattedCount`/`RowsMade`); `Reveal(direction, streamOffset)` = deepest field + opens ancestors), `PayloadMap` (message coords → stream: frame body offset, or fragments, clipped to one fragment), `PropertyTreeView` (virtualized ListBox: `HoveredRow`, section press folds (tunnel handler), ▸ glyph/double-click/Left/Right), `AssemblyBar` (drawn; `AssemblySegment` Start/Chunk/Abort, proportional `Layout`, interleaving gaps ticked), `Themes/PropertyTree.axaml` + `PropertyTreeRows.axaml` (row text styles: a ControlTheme cannot style into an item template). `Model/InspectorProjection.Build(session, frame)` under the gate → `InspectorContent` (Frame header with wire bytes, Transport ops, Invocation/Pipe/Greeting/Body section, `AssemblyInfo`, `IsHandshake`, `IsFinal`). `InspectorViewModel` follows `SyncBus.Selection` (frame / message / entity / byte range → frame; a byte range selects the covering row), `HoveredRow` → `SyncBus.SetHover(FieldHover(message, field index or row node, offset, length))` (frame rows: `MessageRef.ForFrame`, offsets relative to the body, header bytes negative; `HexLinker.AddHoverRanges` maps both kinds), `SelectedRow` → `Selection.OfBytes(...) with { Frame, Message }`; second tab "Message trace"/"Handshake" (content is I25's). Capture: `CaptureScenario.InspectLive` (`Capture/InspectLiveScreen`), gallery `Capture/InspectGallery`; tests `src/NexNet.Inspector.App.Tests/Inspector/`
- Capture harness (orchestrator screen checks): `Capture/CaptureScenarios.cs` (`CaptureScenario` enum + registry of `CaptureSetup(Stem, Create(CaptureContext) → Window, Height = 900)`), `Capture/ControlGallery.cs` (`ControlGallery.Sections`: control WOs add a `GallerySection` per control; tokens bound with `ControlGallery.Bind`), `Capture/HeadlessCapture.cs` (1440 logical px wide at render scaling 2, writes `<stem>--<light|dark>.png`). Opt-in run: `NEXNET_INSPECTOR_CAPTURE_DIR=/abs/dir dotnet test src/NexNet.Inspector.App.Tests -c Release --treenode-filter "/*/*/CaptureTests/*"` (without the variable those tests are skipped; `CaptureHarnessTests` always renders one scenario to a temp dir). To add a screen: a `CaptureScenario` member and its registry entry
- Primitive control themes (I14), one file per control under `Themes/`, merged from `Themes/Controls.axaml`; states per OQ1 (hover = `surface.raised` fill or `border.strong` border, keyboard focus = 2 px `direction.inbound` ring 1 px outside, drawn by the template with `ClipToBounds=False` and `FocusAdorner` null; pressed = `state.selected`; disabled = opacity): `Button.axaml` (the default Button theme; classes `primary`, `record` (dot), `caption` + `caption-close`, `split`, `md`; `Glyph.Caption.*` geometries for PathIcon), `SegmentedControl.axaml` (ListBox `Theme="{StaticResource Segmented}"`, classes `sm`/`dialog`; bind `ItemsSource`/`SelectedIndex` to a `Controls/Primitives/EnumChoice<TEnum>` (labels + enum values in order, `Value`, `ValueChanged`)), `TextInput.axaml` (Styles over Fluent's TextBox: 28 px, 1.5 px focus/error borders, classes `mono`/`search`/`password`, the DataValidationErrors message as an 11 px status.error line, `TextBlock.field-label`; its colours are the `TextControl*` keys in Tokens.axaml), `Select.axaml` (default ComboBox/ComboBoxItem themes: ▾ caret, menu-surface popup, check on the current item, class `sm`), `CheckBox.axaml` (default CheckBox theme, 14 px box), `RadioCard.axaml` (keyed RadioButton themes `RadioCard`, `RadioRow`); `Controls/Primitives/ControlHint.Text` (attached) is the hint/description a checkbox, card or row shows and its accessible help text. Drawn chips in `Controls/`: `FlagChip` (one `FrameFlags` bit; static `Draw`/`MeasureWidth`/`DisplayOrder` for dense rows), `CountBadge` (`CountBadgeTone`), `TransportOpChip` (`OpId`, `TapDirection`, `IsSplit`). Tests: `PrimitiveThemeTests`, `EnumChoiceTests`; gallery sections in `Capture/PrimitiveGallery.cs` force states with `PrimitiveGallery.Force(control, ":pointerover")`
- Dialog layer, banner, home (I17): `Controls/DialogHost` (the modal layer in `MainWindow` row 1, under the title bar: `Dialog` (a view-model the window's DataTemplates turn into a view; null closes), scrim = `Brush.Overlay.Scrim` at `Opacity.Scrim.Dialog`, Opacity fade `MenuMotion.Fade`, focus trap (Tab cycles, focus that leaves is sent back), attached `DialogHost.InitialFocus`, focus returns to the invoker; the scrim never dismisses), `Controls/DialogFrame` (title row, body, footer hint + `Footer` buttons; Enter = `PrimaryCommand`, Esc = `CancelCommand` unless a focused control used the key), `Controls/Banner` (`BannerKind` Status / SessionEnded / SchemaMismatch, `StatusTone` Neutral / Ok / Busy (spinner) / Error; pseudo-classes per kind and tone), themes `Themes/Dialog.axaml`, `Banner.axaml`, `Card.axaml`. Home: `Views/Home/HomeView` (hero, `ActionCard` (a Button; `:droptarget` dashed, `:dragover`), `ListCard` (a ListBox; header, `:empty` text, `ItemCommand` on click/Enter)), `HomeViewModel` (recent rows as record structs with relative dates, drag/drop of `.nntl`, `OpenError`). Connect dialog: `Views/Connect/ConnectDialog` over `ConnectDialogViewModel` (wraps I18's `ConnectViewModel`: EnumChoice Transport/Overflow, Server/Client, one status line from probe + connect (OQ2), `SessionChooser` hook (default: first session; I28's popover replaces it), closes once attached). `MainWindowViewModel`: `Home`, `Dialog`, `ShowConnect(RecentEndpoint?)`, Ctrl+K / Ctrl+O; I16's `Chrome.Requested` Connect → `ShowConnect`, Open → the file picker, AppMenu → nothing yet (I28). `ConnectViewModel.Prober` (internal) swaps the probe for tests and the capture harness. Tests: `Dialogs/`, `Home/`, `ManualTime` (fake clock with timers); capture scenario `Connect` (`Capture/ConnectScreen.cs`, fixed clock) and gallery sections in `Capture/HomeGallery.cs`
- Timeline (I19): `Controls/Timeline/` — `TimelineControl` (one drawn Control: overview strip, ruler, groups, 12/24 px lanes, marks, trace band, gap hatch, playhead; `Model`, `Sync` (I18 `SyncBus`: Ctrl+Wheel zooms around the pointer, Wheel pans, Shift+Wheel scrolls lanes, click selects/traces, overview drag moves the window), `ShowAxis`, `PinCommand`, `HoveredLane`, `ContextLane` for the I26 lane menu, Left/Right step frames, P pins), `TimelineModel` (lanes keyed by `LaneKey(LaneKind, index)` in `LaneGroupKind` groups, `TimelineMark` struct marks sorted by start for binary search, gaps, string labels, pins, collapsed groups, `GetTrace(Selection)`), `LanePacker` (first-fit Gantt rows), `TimelineLayout` (rows per window: invocation rows empty in view collapse, `LaneVisibility.Lifetime` lanes come and go, pins stay expanded, empty-group hint, badges), `LaneAnimator` (OQ12: 160 ms `ScrollRail.Ease` height motion, instant under reduced motion), `TimelineProjection` (`CaptureSession` → model, incremental for ops/frames/pipe events, invocations re-packed per update), `TimelineText` (shaped-label cache). `ViewModels/TimelineViewModel` follows `DocumentsViewModel.Active` (`TogglePinCommand`, `ToggleGroupCommand`). A steady redraw allocates nothing of its own (only Skia's per-glyph-run cost; `TimelineAllocationTests`). Tests build models with `Timeline/TimelineDataBuilder`; gallery section `Capture/TimelineGallery`
- Ladder (I21): `Controls/Ladder/` — `LadderView` (header + ScrollViewer over the virtualizing `LadderPanel`, which realizes and recycles a `LadderRowControl` per row in view; `Model`, `Sync` (click selects a frame and moves the playhead, double-click/Enter traces its message as `Selection.OfMessage`, Up/Down/PgUp/PgDn/Home/End step, the selection scrolls into view), `Mode` (`LadderMode.Compact` 18 px rows / `TimeScaled`, OQ6), `IsSynced` (scrolling pans the shared window to the top row's time; a window moved elsewhere scrolls its first frame to the top), `HoveredIndex`), `LadderRowControl` (drawn: bracket, time, lifelines, arrow broken around its label, flag chips via `FlagChip.Draw`, op chips via `TransportOpChip.Draw`/`CreateLabel`; text shaped on bind, never per frame), `LadderHeader`, `LadderModel` (`LadderRow` struct rows in completion order, method-name labels, chunk totals, message refs, `IndexOf(FrameKey)`, `IndexAtOrAfter(ticks)`, `GetTrace(Selection)` → `LadderTrace` span with member/interleaved rows), `LadderLayout` (compact `18 i`; time-scaled gap `12 px · log10(Δt / 10 µs)` capped 0–48 px, incremental), `LadderProjection` (`CaptureSession` → model, incremental; rows of assembling messages re-read until they end), `LadderText` (titles, targets, notes, bytes, times, accessible names). `ViewModels/LadderViewModel` follows `DocumentsViewModel.Active` (`Mode` as `EnumChoice<LadderMode>` for the pane header's select, `IsSynced`). A steady redraw allocates nothing of its own (`LadderAllocationTests`). Tests build models with `Ladder/LadderDataBuilder`; gallery section `Capture/LadderGallery`
- Hex view (I22): `Controls/Hex/` — `HexLayout` (the lines of one `CaptureSession` over its op stores, never text: one entry per shown op with the 16-byte rows it owns (a row belongs to the first op of a run that touches it; a direction switch or a gap starts a new run on a new row), a `ReadRule` line above a row an op starts in and a `Frames` line above a row frames start in; `Update(session, HexDirections)` is incremental (new ops merged by ticks, new frames counted, line numbers recomputed from the first changed op), `Read(firstLine, Span<HexLine>)` generates any window, `FindLine`/`FindLineAtTicks`; call under the session's `Gate`), `HexRowBuilder` (`HexRow`: inline-array bytes, `HexCellRole` fills — preamble/ping, type, length, post-header, body; other-frame and body-selected once something is selected — and `HexCellMarks` redacted/read-start/selected/hovered), `HexText` (columns: ≥6 offset digits, 3 spaces, 16 cells of "XX ", 2 spaces, ASCII; rule and frame annotation text), `HexLinker` (byte → `HexFieldHit`: header field, or the decoded field via `DecodedValue.FindField` on the invocation's arguments/result → `FieldHover`; `FieldHover`/selection → stream ranges via `GetFragments`; builds the `HexHighlight`), `HexView` (AvaloniaEdit `TextView` whose `TextDocument` holds only the visible window, its own `ScrollBar` over the layout's line count; `HexColorizer` line transformer, `HexAnnotationGenerator` element generator (UI-font annotations), `HexBackgroundRenderer` (fills, stripes, read borders, rules, rings); hover → `SyncBus.SetHover` or the local header-field ring (`HoveredBytes`), click → `Selection.OfBytes(field) with { Frame }`, drag → byte range, a selection or playhead set elsewhere scrolls into view, user scrolling pans the time window). `ViewModels/HexViewModel` (follows `DocumentsViewModel.Active`; `ShowsInbound`/`ShowsOutbound` never both off), `Views/Inspect/HexPane` (body) and `HexPaneHeader` (▼ IN | ▲ OUT · 16 B/row, `Themes/HexView.axaml` `HexDirectionToggle`) for I23 to place. Tests `Hex/` (`HexCaptureBuilder` writes frames and feeds a session op by op); gallery section `Capture/HexGallery`
- Local AOT check: `PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH" dotnet publish src/NexNet.Inspector.App -c Release -r win-x64 -o publish/nexnet-inspector` then `publish/nexnet-inspector/nexnet-inspector.exe --headless --self-check`. linux-x64 builds in `mcr.microsoft.com/dotnet/sdk:10.0` with `clang zlib1g-dev`; the headless self-check needs no display (nor fontconfig)

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

**Change a message body:** edit the hand-written `Serialize`/`Deserialize` in `Messages/`, update `ProtocolMessageTests`, the spec, and the fuzz corpus if seeds change
