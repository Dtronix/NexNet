# NexNet LLM Development Reference

Token-condensed architecture reference for LLM navigation and modification.

---

## Project Structure

```
src/
├── NexNet/                    # Core library
├── NexNet.Generator/          # Roslyn source generator
├── NexNet.Quic/              # QUIC transport
├── NexNet.Asp/               # ASP.NET Core integration
├── NexNet.IntegrationTests/  # Integration tests (NUnit)
├── NexNet.Generator.Tests/   # Generator tests
├── NexNet.Serialization.Tests/ # Serializer, protocol message and fuzz smoke tests
├── NexNet.Fuzz/              # SharpFuzz harnesses (libFuzzer), smoke runner, regression corpora
├── NexNetBenchmarks/         # BenchmarkDotNet
└── Samples/                  # NexNetDemo, Asp and Native AOT samples
```

Build: `dotnet build src -c Release`. `src/Directory.Build.props` sets `TreatWarningsAsErrors` for every project under
`src/` (libraries, generator, tests, fuzz, benchmarks, samples), NuGet audit advisories (NU1901-NU1904) and warnings in
generated code included. Fix the cause of a warning; do not add `NoWarn` or `WarningsNotAsErrors`.
Test (all three suites must pass):
```
dotnet test src/NexNet.Serialization.Tests -c Release
dotnet test src/NexNet.Generator.Tests -c Release
dotnet test src/NexNet.IntegrationTests -c Release
```
Single test: `dotnet test ... --filter "FullyQualifiedName~TestName"`

---

## 1. Core Library (src/NexNet/)

### 1.1 Entry Points

- `NexusClient.cs` - Generic client managing connection, reconnection, proxy access, ping/timeout
- `NexusServer.cs` - Generic server with Listener/Receiver modes, rate limiting, session management
- `NexusClientPool.cs` - Connection pool with health checking, idle timeout, semaphore-controlled rental

### 1.2 Attributes (Source Generator Markers)

- `NexusAttribute.cs` - `[Nexus<TNexus,TProxy>]` marks classes for generation; NexusType=Server/Client
- `NexusMethodAttribute.cs` - Optional `[NexusMethod(id)]` for manual method ID assignment
- `NexusVersionAttribute.cs` - `[NexusVersion]` enables interface versioning with hash validation
- `NexusAuthorizeAttribute.cs` - `[NexusAuthorize<TPermission>(...)]` marks methods/collections for authorization; `CacheDurationSeconds` property (-1=server config, 0=never, >0=override)
- `AuthorizeResult.cs` - Enum: Allowed, Unauthorized, Disconnect

### 1.3 Invocation Layer (src/NexNet/Invocation/)

**Base Classes:**
- `NexusBase.cs` - Common base; implements IMethodInvoker; manages CTS registry, pipe registration, pooling
- `ServerNexusBase.cs` - Server base; OnAuthenticate, OnNexusInitialize, OnAuthorize virtuals; CheckAuthorization with ConcurrentDictionary cache; InvalidateAuthorizationCache() overloads
- `ClientNexusBase.cs` - Client base; OnReconnecting virtual

**Contexts:**
- `SessionContext.cs` - Base context with session info
- `ServerSessionContext.cs` - Server context with session ID, client identity
- `ClientSessionContext.cs` - Client context with server proxy
- `LocalSessionContext.cs` - Local session registry and key-value store

**Proxy Infrastructure:**
- `ProxyInvocationBase.cs` - Base for generated proxies; Configure() sets session/mode; supports All/Client/Group modes; handles Unauthorized state
- `ProxyUnauthorizedException.cs` - Thrown client-side when server denies method invocation
- `IProxyBase.cs` - Interface for proxy client selection (All, Client(id), Group(name), etc.)

**Routing & Registry:**
- `IInvocationRouter.cs` - Routes invocations to target sessions
- `LocalInvocationRouter.cs` - Single-server routing implementation
- `LocalSessionRegistry.cs` - ConcurrentDictionary<long, INexusSession> session tracking
- `LocalGroupRegistry.cs` - Group membership with LocalSessionGroup management
- `NexusCollectionManager.cs` - Manages synchronized collections per nexus
- `SessionStore.cs` - Per-session ConcurrentDictionary<string, object> for custom data

### 1.4 Messages (src/NexNet/Messages/)

**Protocol:**
- `MessageType.cs` - Enum: Ping, Disconnect variants (20-34 incl. DisconnectUnauthorized), DuplexPipeWrite (50), Greetings (100-105), Invocation (110-112)
- `IInvocationMessage.cs` - InvocationId, MethodId, Flags, Arguments; MaxArgumentSize=65,526 bytes (65,535 body minus 9 bytes worst-case overhead)
- `ClientGreetingMessage.cs` - Client handshake: protocol version, nexus hash, auth token
- `ServerGreetingMessage.cs` - Server response: session ID, server nexus hash
- `InvocationMessage.cs` - Remote method invocation payload
- `InvocationResultMessage.cs` - Return value, exception, or Unauthorized from invocation; StateType: Unset, CompletedResult, Exception, Unauthorized
- `InvocationCancellationMessage.cs` - Cancel ongoing invocation
- `DuplexPipeUpdateStateMessage.cs` - Pipe state changes

All message bodies are hand-written MessagePack (`Serialize(ref MsgPackWriter)` / `Deserialize(ref MsgPackReader)`): fixed-length
arrays with exact element counts, no trailing bytes. User values inside them (arguments, results) are "embedded values":
exactly one inline MessagePack value. Collection sync messages live in `Collections/NexusCollectionMessage.cs` and
`Collections/Lists/NexusCollectionListMessages.cs`. Exact byte layouts: [wire protocol spec](docs/internals/protocol-specification.md).

### 1.5 Session Management (src/NexNet/Internals/)

**Core Session:**
- `NexusSession.cs` - Central lifecycle: protocol, framing, routing; NnP protocol header [N][n][P][0x14][3 reserved][Version=1] (`_protocolHeader`)
- `NexusSession.Sending.cs` - Write logic with MutexSlim serialization
- `NexusSession.Receiving.cs` - Read loop and message dispatch; `ConfirmProtocol` rejects wrong magic, non-zero reserved bytes or another version with `ProtocolError`
- `PayloadSerializer.cs` - Serializes user payload values through `NexusFormatterRegistry` for runtime-typed paths (results, collections, broadcasts)
- `NexusSessionConfigurations.cs` - Immutable session config passed at creation
- `MessageHeader.cs` - Mutable struct: Type + BodyLength for framing
- `RegisteredInvocationState.cs` - Pending invocation with TaskCompletionSource<Memory<byte>>

**Internal State Flags:** ProtocolConfirmed, InitialClientGreetingReceived, InitialServerGreetingReceived, NexusCompletedConnection, ReconnectingInProgress

### 1.6 Transports (src/NexNet/Transports/)

**Abstraction:**
- `ITransport.cs` - Extends IDuplexPipe; Input/Output pipes, RemoteAddress, RemotePort, CloseAsync
- `ITransportListener.cs` - Accepts incoming connections

**Implementation:**
- `SocketTransport.cs` - Wraps System.Net.Sockets.Socket via SocketConnection
- `SocketTransportListener.cs` - Socket-based listener

**Configuration:**
- `ConfigBase.cs` - Base: Timeout, PingInterval, Logger, PipeOptions, `SerializerOptions` (default `NexusSerializerOptions.Untrusted`)
- `ClientConfig.cs` - ConnectionTimeout, ReconnectionPolicy, Authenticate func
- `ServerConfig.cs` - AcceptorBacklog, Authenticate bool, RateLimiting config, AuthorizationCacheDuration (nullable TimeSpan)

**Socket Pipeline (src/NexNet/Internals/Pipelines/):**
- `SocketConnection.cs` - Low-level socket-to-pipe adapter with read/write loops
- `SocketConnection.Connect.cs` - Connection establishment
- `SocketConnection.Receive.cs` - Receive loop
- `SocketConnection.Send.cs` - Send loop

### 1.7 Collections (src/NexNet/Collections/)

**Modes:** `NexusCollectionMode` enum - Unset, ServerToClient (one-way), BiDirectional (two-way), Relay (read-only hierarchical)

**Core:**
- `INexusCollection.cs` - Base interface for synchronized collections
- `INexusList.cs` - List interface: Add, Remove, Insert, Clear, Count
- `NexusCollectionAttribute.cs` - Decorates properties with sync mode

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
- `BufferWriter.cs` (Internals/Pipelines/Buffers/) - Custom IBufferWriter<T> for message payloads

**Session Pooling:**
- `SessionPoolManager.cs` - Manages proxy instance pooling per session
- `ProxyPool.cs` - Pool of proxy instances

### 1.9 Synchronization (src/NexNet/Internals/Threading/)

- `MutexSlim.cs` - Lock-free async mutex; LockToken (sync), PendingLockToken (async)
- `MutexSlim.LockState.cs` - Lock state management
- `MutexSlim.AsyncDirectPendingLockSlab.cs` - Efficient async waiting

### 1.10 Pipes (src/NexNet/Pipes/)

- `INexusDuplexPipe.cs` - Bidirectional pipe: Id, ReadyTask, CompleteTask, CompleteAsync()
- `NexusPipeManager.cs` - Manages duplex pipes for large data transfers
- `NexusChannelWriter.cs` / `NexusChannelReader.cs` - Typed channel items: one MessagePack value each, concatenated on the pipe. The reader deserializes optimistically away from the buffer end; near the end it measures the next item with `MsgPackReader.TryGetNextValueLength` so a partial item waits for more data (bounded by `MaxBufferedItemSize`)

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
- `MsgPackReader.cs` - `ref struct` over `ReadOnlySequence<byte>`/`ReadOnlyMemory<byte>`; length checks before allocation, iterative `Skip`, depth tracking (`Enter`/`Exit`), `TryGetNextValueLength`
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
- `DiagnosticDescriptors.cs` - Error/warning/info definitions; NEXNET001-027 (024-027 are authorization: client nexus, missing OnAuthorize, mixed enums, non-int enum); NEXNET028-034 and 036-038 are serialization
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

`NexNetMiddlewareExtensions.cs` provides AddNexusServer<>() for IServiceCollection and ApplyAuthentication().

**WebSocket:** `WebSocket/WebSocketServerConfig.cs` provides WebSocket-specific server config.

**HttpSocket (HttpSocket/):**
- `HttpSocketMiddleware.cs` - ASP.NET middleware for dynamic HTTP/WebSocket upgrade
- `HttpSocketServerConfig.cs` - HttpSocket transport config

**Utilities:**
- `NexusILoggerBridgeLogger.cs` - Adapts Microsoft.Extensions.Logging to INexusLogger
- `ProxyHeaderResolver.cs` - Extracts client IP from X-Forwarded-For

---

## 4. Integration Tests (src/NexNet.IntegrationTests/)

### 4.1 Base Classes

- `BaseTests.cs` - Abstract base: lifecycle (SetUp/TearDown), transport type enum (Uds/Tcp/TcpTls/Quic/WebSocket/HttpSocket), config creation, port allocation, TLS cert loading, cleanup tracking
- `BasePipeTests.cs` - Pipe test base: LogMode enum (None/OnTestFail/Always), Setup() with auto server/client connection
- `NexusCollectionBaseTests.cs` (Collections/) - Collection base: ConnectServerAndClient(), CreateRelayCollectionClientServers() for relay topology
- `BaseAspTests.cs` - ASP.NET base: AspCreateAuthServices(), AspAppAuthConfigure()

### 4.2 Test Infrastructure

- `NexusServerFactory.cs` - Generic server wrapper tracking created nexuses in ConcurrentQueue
- `TestInterfaces/BasicTestsInterfaces.cs` - IClientNexus, IServerNexus interfaces; ClientNexus, ServerNexus implementations with event callbacks
- `TestInterfaces/VersionedTestsInterfaces.cs` - Version-specific test interfaces
- `TestInterfaces/AuthorizationTestInterfaces.cs` - Authorization test nexuses with delegate handlers and cache invalidation helpers
- `Utilities.cs` - Dequeue<T>(), GetBytes<T>(), GetValue<T>(), InvokeAndNotifyAwait()
- `TaskExtensions.cs` - Timeout() overloads, AssertTimeout()
- `Collections/CollectionHelpers.cs` - WaitForEvent() extension, WaitForActionHandler
- `PipeStateManagerStub.cs` - IPipeStateManager mock

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

**Sockets (Sockets/, 7 files):**
- `BufferWriterTests.cs` - Buffer ops
- `ConnectTests.cs` - Connection establishment
- `MutexSlimTests.cs` - Sync primitives
- `SequenceTests.cs` - ReadOnlySequence ops

### 4.4 Test Patterns

**Multi-Transport:** `[TestCase(Type.Uds)]`, `[TestCase(Type.Tcp)]`, etc. for cross-transport coverage

**Async Pattern:** All async ops wrapped with `.Timeout(seconds)` using TaskExtensions

**Setup Pattern:**
```
var (server, client, clientNexus) = CreateServerClient(config, config);
await server.StartAsync().Timeout(1);
await client.ConnectAsync().Timeout(1);
```

**Event Assertions:** Set delegates on test nexus before operations, verify in callbacks

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

## 6. Key Architectural Concepts

### Protocol
Spec: [docs/internals/protocol-specification.md](docs/internals/protocol-specification.md) (wire protocol version 1).
- **NnP Header:** 8 bytes `[N][n][P][0x14][reserved x3][Version=1]`; any mismatch -> `ProtocolError`
- **Framing:** type byte, little-endian `uint16` body length (types with a body), pipe ID for `DuplexPipeWrite`, body
- **Bodies:** MessagePack arrays, hand-written per message (src/NexNet/Messages)
- **Handshake:** Client greeting -> Server greeting with version hash validation

### Serialization
- **NexNet MessagePack serializer** (src/NexNet/Serialization) for arguments, results, channel and collection items
- Generator emits formatters for `[NexusObject]` types; registry lookup at runtime, no reflection (Native AOT clean; CI publishes the AOT sample and fails on any IL warning)
- **65,526-byte limit** for serialized arguments per invocation (use INexusDuplexPipe or channels for larger)

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

**Add test:** Inherit from appropriate base (BaseTests/BasePipeTests/NexusCollectionBaseTests); use [TestCase(Type.X)] for transport coverage

**Debug generator:** Use Generator.Tests with CSharpGeneratorRunner.RunGenerator(); check Diagnostic[] output

**Add built-in serializable type:** formatter in `Serialization/Formatters/`, register in `BuiltInFormatters`, teach `SerializationBuilder` (and `ChannelTypeAnalyzer` if pre-registered) about it, document it in spec §5.4

**Change a message body:** edit the hand-written `Serialize`/`Deserialize` in `Messages/`, update `ProtocolMessageTests`, the spec, and the fuzz corpus if seeds change
