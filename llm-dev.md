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
└── NexNet.Generator.Tests/   # Generator tests
```

Build: `dotnet build src -c Release`
Test: `dotnet test src/NexNet.IntegrationTests -c Release`
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

### 1.3 Invocation Layer (src/NexNet/Invocation/)

**Base Classes:**
- `NexusBase.cs` - Common base; implements IMethodInvoker; manages CTS registry, pipe registration, pooling
- `ServerNexusBase.cs` - Server base; OnAuthenticate, OnNexusInitialize virtuals
- `ClientNexusBase.cs` - Client base; OnReconnecting virtual

**Contexts:**
- `SessionContext.cs` - Base context with session info
- `ServerSessionContext.cs` - Server context with session ID, client identity
- `ClientSessionContext.cs` - Client context with server proxy
- `LocalSessionContext.cs` - Local session registry and key-value store

**Proxy Infrastructure:**
- `ProxyInvocationBase.cs` - Base for generated proxies; Configure() sets session/mode; supports All/Client/Group modes
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
- `MessageType.cs` - Enum: Ping, Disconnect variants (20-33), DuplexPipeWrite (50), Greetings (100-105), Invocation (110-112)
- `IInvocationMessage.cs` - InvocationId, MethodId, Flags, Arguments; MaxArgumentSize=65,521 bytes
- `ClientGreetingMessage.cs` - Client handshake: protocol version, nexus hash, auth token
- `ServerGreetingMessage.cs` - Server response: session ID, server nexus hash
- `InvocationMessage.cs` - Remote method invocation payload
- `InvocationResultMessage.cs` - Return value or exception from invocation
- `InvocationCancellationMessage.cs` - Cancel ongoing invocation

### 1.5 Session Management (src/NexNet/Internals/)

**Core Session:**
- `NexusSession.cs` - Central lifecycle: protocol, framing, routing; NnP protocol header [N][n][P][0x14][3 reserved][Version=1]
- `NexusSession.Sending.cs` - Write logic with MutexSlim serialization
- `NexusSession.Receiving.cs` - Read loop and message dispatch
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
- `ConfigBase.cs` - Base: Timeout, PingInterval, Logger, PipeOptions
- `ClientConfig.cs` - ConnectionTimeout, ReconnectionPolicy, Authenticate func
- `ServerConfig.cs` - AcceptorBacklog, Authenticate bool, RateLimiting config

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

### 1.11 Logging (src/NexNet/Logging/)

- `INexusLogger.cs` - Hierarchical logger: Behaviors, FormattedPath, Log(), CreateLogger()
- `NexusLogLevel.cs` - Trace, Debug, Info, Warning, Error, Critical
- `ConsoleLogger.cs` - Console output implementation
- `RollingLogger.cs` - File rotation support

### 1.12 Rate Limiting (src/NexNet/RateLimiting/)

- `ConnectionRateLimiter.cs` - Thread-safe: global limit, per-IP rate (sliding window), IP banning, whitelist
- `ConnectionRateLimitConfig.cs` - MaxConcurrentConnections, MaxConnectionsPerSecond, BanDurationSeconds
- `ConnectionRateLimitResult.cs` - Allowed, MaxConcurrentConnectionsExceeded, MaxConnectionsPerSecondExceeded, IpBanned

---

## 2. Source Generator (src/NexNet.Generator/)

### 2.1 Pipeline

`NexusGenerator.cs` implements IIncrementalGenerator using ForAttributeWithMetadataName for incremental caching.

**Three Phases:**

1. **Extraction:** `Extraction/NexusDataExtractor.cs` parses [Nexus<,>]; extracts namespace, type, modifiers, generics, interfaces, methods, collections.

2. **Validation:** `Validation/NexusValidator.cs` validates class partial/not nested/not generic/not abstract; method signatures; collection modes; version hashes.

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
- `CollectionData.cs` - Name, type, mode, element type

### 2.3 Utilities

- `TypeHasher.cs` - xxHash32-based interface signature hashing for versioning
- `DiagnosticDescriptors.cs` - Error/warning/info definitions
- `SymbolUtilities.cs` - Symbol inspection helpers

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
- `TypeHasherTests.cs` - Type hashing for method IDs
- `VersioningTests.cs` - Version string parsing, [NexusVersion] + [NexusMethod(id)] validation

**Test Pattern:** Pass C# source string to RunGenerator(), check Diagnostic[] for expected IDs (MustBePartial, etc.)

---

## 6. Key Architectural Concepts

### Protocol
- **NnP Header:** 8 bytes `[N][n][P][0x14][reserved x3][Version=1]`
- **Framing:** Length-prefixed messages with MessageType
- **Handshake:** Client greeting -> Server greeting with version hash validation

### Serialization
- **MemoryPack** for method arguments
- **65,535-byte limit** per invocation (use INexusDuplexPipe for larger)

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
