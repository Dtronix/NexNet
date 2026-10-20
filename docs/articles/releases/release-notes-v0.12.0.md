# NexNet v0.12.0

*Released 2025-12-17*

This Release introduces a significant overhaul of the collection system architecture, adds a new client connection pooling mechanism, implements relay mode for collections, refactors the logging system, and modernizes the source generator infrastructure.

Added a llm.md document for reasons.

## Major Changes

### 1. Collection System Modularization

The collection system has been completely restructured from a monolithic `NexusList<T>` class into separate, purpose-built implementations:

- `NexusListServer` - Server-side collection management with broadcasting
- `NexusListClient` - Client-side collection with enable/disable lifecycle
- `NexusListRelay` - New relay mode for proxy/middleware scenarios
- `NexusCollectionListMessages` - Dedicated message types with union serialization

**Functional Changes:**
- Collections now require explicit activation via `EnableAsync()` before receiving updates
- `DisableAsync()` cleanly disconnects from the collection channel
- `ConnectAsync()` provides a convenience method that enables and waits for ready state
- `ReadyTask` completes when initial synchronization finishes (client receives full state from server)
- `DisconnectedTask` completes when the collection loses its connection
- `DisabledTask` completes when the collection is explicitly disabled
- Server collections automatically broadcast changes to all connected clients
- Client collections maintain local synchronized copies with change notifications

### 2. NexusClientPool

New connection pooling system (`NexusClientPool<TClientNexus, TServerProxy>`) for managing client connections similar to gRPC channels.  This is only for usage by Channel relays and not for normal consumption.  The main purpose is for servers to talk directly to each other via a Nexus client connection.  Good for cases where authentication should not need to happen.

- Automatic connection health monitoring and reconnection
- Configurable pool size, idle timeout, and minimum idle connections
- Rent/return pattern via `IRentedNexusClient<TProxy>`
- Collection connector integration via `GetCollectionConnector()`

**Functional Changes:**
- `RentClientAsync()` returns a healthy connection from the pool or creates a new one
- Rented clients are automatically returned to the pool when disposed
- Background health checks remove unhealthy connections and enforce idle timeouts
- `MinIdleConnections` ensures a minimum number of warm connections are maintained
- `GetCollectionConnector()` provides access to collections through pooled connections
- `ClientPoolConnectionException` thrown when connection attempts fail during rent

### 3. Broadcast Infrastructure

New pipe-based broadcast system in `NexNet/Pipes/Broadcast/`:

- `NexusBroadcastServer` - Server-side broadcast management
- `NexusBroadcastClient` - Client-side broadcast subscription
- `NexusBroadcastConnectionManager` - Multi-client message distribution
- `NexusBroadcastMessageProcessor` - Sequential message processing with bounded channels
- Object pooling for broadcast message wrappers

**Functional Changes:**
- Server broadcasts messages to all connected clients through dedicated pipe channels
- Messages are processed sequentially per-client to maintain ordering guarantees
- Bounded channels prevent memory exhaustion from slow consumers
- Acknowledgment messages sent back to originating clients for bidirectional collections
- Automatic client removal when pipes complete or fail
- Message cloning for source client acknowledgments vs broadcast messages
- Potential fix of long standing issue where data could be written to a duplex pipe twice.  Added a semaphore to ensure boundaries are maintained.
- Prevented nexus from being reused on servers.  Refactored tests to accommodate this

### 4. Logging System Refactoring

Complete rework of the logging infrastructure:

- `CoreLogger<TLogger>` now uses hierarchical path nodes instead of category strings
- `FormattedPath` and `PathSegment` properties for dynamic path building
- Cached path formatting with version tracking for performance
- `StringBuilderPool` for reduced allocations
- All loggers (Console, Rolling, ILogger bridge) updated to new pattern

**Functional Changes:**
- Log entries now show hierarchical paths like `Server|S123|P00456|BR1` instead of flat categories
- `PathSegment` can be updated at runtime and automatically propagates to child loggers
- Path caching with version tracking avoids rebuilding paths on every log call
- Formatted log output includes timestamp, level abbreviation, path, and message
- Child loggers inherit parent configuration but can have their own path segments

### 5. Source Generator Modernization

The generator has been restructured for better maintainability:

- Extraction layer (`NexusDataExtractor`) separated from emission layer
- New `Models/` namespace with immutable data records (`NexusGenerationData`, `MethodData`, `CollectionData`, etc.)
- `Validation/NexusValidator` for centralized validation
- Emitters (`NexusEmitter`, `MethodEmitter`, `CollectionEmitter`, `InvocationInterfaceEmitter`) work entirely on cached data
- Removed old `MetaGenerator/` classes
- `TypeHasher` significantly enhanced with V2 incremental hashing

**Functional Changes:**
- Improved incremental generation - extraction caches data between compilations
- Better error reporting with location tracking through `LocationData`
- Type hashing now handles more edge cases (generics, nested types, arrays)
- Validation errors include precise source locations
- Generated code unchanged in functionality but generation is more efficient

### 6. New Internal Utilities

- `ConcurrentRemovableQueue<T>` - Thread-safe queue with O(1) removal via node references
- `PooledResettableValueTaskCompletionSource<T>` - Pooled, resettable async completion source for reduced allocations
- `SemaphoreSlimDisposable` - Disposable wrapper for cleaner semaphore usage patterns
- `SnapshotList<T>` - Enhanced with `Clear()` method and improved enumeration

### 7. Relay Collection Mode

Collections can now be configured as relays that receive data from a parent collection:

- `NexusCollectionMode.Relay` for middleware/proxy patterns
- `ConfigureRelay(INexusCollectionClientConnector)` for setup
- Automatic reconnection and state synchronization

**Functional Changes:**
- Relay collections connect to an upstream server collection via `INexusCollectionClientConnector`
- Changes from the parent collection are automatically propagated to relay subscribers
- Relays can serve as intermediate caching/distribution points in multi-tier architectures
- `ConfigureRelay()` must be called before `EnableAsync()` for relay-mode collections
- Relay collections support the same change events as regular client collections

## Test Coverage

Comprehensive test additions covering:

- `NexusClientPoolTests` - Pool lifecycle, health checks, connection management, idle eviction
- `NexusCollectionRelayTests` - Relay connectivity, state synchronization, reconnection
- `NexusCollectionBroadcasterTests` - Multi-client broadcasting, message ordering, acknowledgments
- `ConcurrentRemovableQueueTests` - Thread-safety, concurrent operations, removal correctness
- `PooledResettableValueTaskCompletionSourceTests` - Pooling behavior, reset semantics
- `SnapshotListTests` - Enumeration during modification, clear operations
- `TypeHasherV2Tests` - Generic types, nested types, arrays, edge cases

## Breaking Changes

### INexusLogger Interface

| Removed | Replacement |
|---------|-------------|
| `string? Category { get; }` | `string FormattedPath { get; }` |
| `string? SessionDetails { get; set; }` | `string? PathSegment { get; set; }` |
| `CreateLogger(string? category, string? sessionDetails = null)` | `CreateLogger(string? pathSegment = null)` |

### CoreLogger Abstract Class

| Removed | Replacement |
|---------|-------------|
| `CreatePrefixedLogger(string? category, string prefix, string? sessionDetails)` | Use `CreateLogger(string? pathSegment)` and set `PathSegment` property |
| Non-generic `CoreLogger` base | Generic `CoreLogger<TLogger>` base class |

## What's Changed
* Epic: Collection Configuration, Logging, Incremental Generation, Client Pooling, Relays & More! by @DJGosnell in https://github.com/Dtronix/NexNet/pull/52


**Full Changelog**: https://github.com/Dtronix/NexNet/compare/v0.11.0...v0.12.0
