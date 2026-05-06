# Implementation Plan: add-nexnet-testing

## Goal

Ship `NexNet.Testing` — a new package providing an in-process transport (`InProcessTransport`), a test host (`NexusTestHost`), per-client handles with assertion APIs, recorders for invocations / pipes / channels, streaming convenience helpers, and a quiescence primitive — backed by two minimal optional internal hooks added to `NexNet` core.

`TimeProvider` integration is **deferred to issue #75** and is out of scope for this workflow.

## Key Concepts

**Quiescence** is the load-bearing primitive that makes negative assertions (`AssertNotReceived`) deterministic. The harness tracks four counters and `QuiesceAsync()` returns when all are zero, with an `await Task.Yield()` re-check pass to drain synchronously-scheduled continuations. Counters: `bytesInTransit` (instrumented in `InProcessTransport`), `inDispatch` (incremented/decremented by `IInvocationInterceptor` around `nexus.InvokeMethod`), `pendingResults` (read from `SessionInvocationStateManager.PendingInvocationCount`), `activePipes` (tracked by the harness's `IPipeFactory` on rent / pipe-completion).

**Outer-only interception.** `IInvocationInterceptor` wraps the call to `session._nexus.InvokeMethod(message)` in `NexusSession.Receiving.cs:682` (the `InvocationTask` static callback). It has access to the raw `InvocationMessage` (method ID + serialized arg bytes), not deserialized args. Generator code is not modified. For typed-argument assertion matching, the harness builds — at host startup — per-interface maps `(Type interface) → (ushort methodId) → (MethodInfo + Type<ValueTuple<...>>)` via reflection over `IServerNexus` / `IClientNexus`, then lazily MemoryPack-deserializes args from `InvocationMessage` only when an assertion needs to inspect them.

**Pipe tap records consumed bytes, not visible bytes.** The harness's `IPipeFactory` produces a `TappingPipeReader` / `TappingPipeWriter` pair that wraps the pooled `NexusDuplexPipe`'s underlying readers/writers. The reader records on `AdvanceTo` (capturing only the slice the user committed past — matches "what the handler saw"). The writer records on `FlushAsync` (matches "what was actually sent over"). On pipe return-to-pool, the wrappers detach and the underlying pipe resets normally.

**Channels are tapped by helpers, not by a core hook.** `INexusChannelReader<T>` / `INexusChannelWriter<T>` have no central creation point; the harness convenience methods (`ChannelCollect<T>`, `ChannelPublish<T>`, `TapChannel<T>`) own creation themselves and return tap-wrapped instances. For user code that instantiates channels directly, the underlying pipe-layer byte tap is still observable.

**Auth: dual mode.** `ServerConfig.OnAuthenticateOverride` is a new nullable `Func<ReadOnlyMemory<byte>?, ValueTask<IIdentity?>>?`. When set, `ServerNexusBase.Authenticate` consults it before falling back to the user's `OnAuthenticate`. The harness installs an override that maps fake tokens (issued via `TestIdentity.Of(name, roles)`) to `IIdentity` instances. Users who want to test their *own* auth code leave the override unset.

## Phase dependency graph

```
Phase 1 ────┐
            ├─→ Phase 8 (interceptor needs PendingInvocationCount)
Phase 2 ────┤
            ├─→ Phase 8 ─→ Phase 10 ─→ Phase 11 ─→ Phase 12 ─→ Phase 13
Phase 3 ────┼─→ Phase 9 ─→ Phase 10
            │
Phase 4 ────┴─→ Phase 10
Phase 5 ──────────────────→ Phase 6
Phase 5 ──────────────────────────→ Phase 10
Phase 7 ────────────────────────────→ Phase 8 (recorder primitives feed interceptor)
                                      Phase 12 (assertions consume recorder)
```

## Phase 1 — Add `PendingInvocationCount` accessor on `SessionInvocationStateManager`

Trivial enabling change. `SessionInvocationStateManager._invocationStates` is a `ConcurrentDictionary<int, RegisteredInvocationState>`. Add an `internal int PendingInvocationCount => _invocationStates.Count;` so the harness can read it for the `pendingResults` quiescence counter.

**Files:** `src/NexNet/Invocation/SessionInvocationStateManager.cs`.
**Tests:** No new tests; covered by Phase 8's quiescence tests later. Existing tests must pass unchanged.

## Phase 2 — Add `IInvocationInterceptor` hook in core

Define `internal interface IInvocationInterceptor` in `src/NexNet/Internals/IInvocationInterceptor.cs`:

```csharp
internal interface IInvocationInterceptor
{
    ValueTask WrapAsync(InvocationMessage message, Func<ValueTask> invoke);
}
```

Add `internal IInvocationInterceptor? InvocationInterceptor { get; init; }` to `ConfigBase`. Add a corresponding nullable field to the `NexusSessionConfigurations<TNexus, TProxy>` readonly struct, populated from `Configs.InvocationInterceptor` at session-construction sites. Wire the hook in `NexusSession.Receiving.cs` `InvocationTask` (around line 682):

```csharp
// before: await session._nexus.InvokeMethod(message).ConfigureAwait(false);
var interceptor = session._sessionConfigs.InvocationInterceptor;
if (interceptor is null)
    await session._nexus.InvokeMethod(message).ConfigureAwait(false);
else
    await interceptor.WrapAsync(message,
        () => session._nexus.InvokeMethod(message)).ConfigureAwait(false);
```

Add `[assembly: InternalsVisibleTo("NexNet.Testing")]` to NexNet.

**Files:** `src/NexNet/Internals/IInvocationInterceptor.cs` (new), `src/NexNet/Transports/ConfigBase.cs`, `src/NexNet/Internals/NexusSessionConfigurations.cs`, `src/NexNet/Internals/NexusSession.Receiving.cs`, `src/NexNet/NexNet.csproj` or `AssemblyInfo.cs` (InternalsVisibleTo), and the session construction sites that build the struct (find via grep on construction).

**Tests:** Add a unit test in `NexNet.IntegrationTests` that confirms a no-interceptor case behaves identically (existing tests already cover this). Add one targeted test that installs a counting interceptor and verifies it observes every invocation. Ensure all 2628 existing integration tests pass.

## Phase 3 — Add `IPipeFactory` hook in core

Define `internal interface IPipeFactory` in `src/NexNet/Internals/IPipeFactory.cs`:

```csharp
internal interface IPipeFactory
{
    INexusDuplexPipe WrapLocal(INexusDuplexPipe inner);
    INexusDuplexPipe WrapRemote(INexusDuplexPipe inner);
}
```

Add `internal IPipeFactory? PipeFactory { get; init; }` to `ConfigBase`. Plumb through `NexusSessionConfigurations` as in Phase 2. Hook into `NexusPipeManager.RentPipe()` and `NexusPipeManager.RegisterPipe(byte otherId)` — after constructing the inner pipe, if `_session.SessionConfigs.PipeFactory` is non-null, wrap and return the wrapper instead.

The wrapper must delegate every `INexusDuplexPipe` member to the inner pipe (including `Input`/`Output`, `ReadyTask`, `CompleteTask`, `CompleteAsync`, `Id`, `WriterCore`, `ReaderCore`). The harness's wrapper additionally exposes recording state.

**Files:** `src/NexNet/Internals/IPipeFactory.cs` (new), `src/NexNet/Transports/ConfigBase.cs`, `src/NexNet/Internals/NexusSessionConfigurations.cs`, `src/NexNet/Pipes/NexusPipeManager.cs`.

**Tests:** Add a unit test that installs a counting factory and verifies it wraps both local and remote pipe paths. Confirm all existing tests pass with null factory.

## Phase 4 — Add `OnAuthenticateOverride` to `ServerConfig`

Add `internal Func<ReadOnlyMemory<byte>?, ValueTask<IIdentity?>>? OnAuthenticateOverride { get; init; }` to `ServerConfig`. Modify `ServerNexusBase.Authenticate(ReadOnlyMemory<byte>? token)` to consult it first:

```csharp
internal async ValueTask<IIdentity?> Authenticate(ReadOnlyMemory<byte>? authenticationToken)
{
    var override = ((ServerConfig)Context.SessionContext.Configs).OnAuthenticateOverride;
    if (override is not null)
        return await override(authenticationToken).ConfigureAwait(false);
    return await OnAuthenticate(authenticationToken).ConfigureAwait(false);
}
```

(Verify the precise navigation path from `ServerNexusBase` to its `ServerConfig` against actual source before committing.)

**Files:** `src/NexNet/Transports/ServerConfig.cs`, `src/NexNet/Invocation/ServerNexusBase.cs`.

**Tests:** Add a test that installs an override and confirms it is consulted; another that leaves it unset and confirms `OnAuthenticate` is called as before. Existing tests must pass.

## Phase 5 — Create `NexNet.Testing` project + `InProcessTransport`

Create `src/NexNet.Testing/NexNet.Testing.csproj` targeting `net10.0`, referencing `NexNet`. Configure as `<IsPackable>true</IsPackable>` for NuGet output. Mirror authoring metadata (description, license) from the existing `NexNet.Quic.csproj` so the package is publishable.

Create the project structure:

```
src/NexNet.Testing/
├── NexNet.Testing.csproj
├── Transports/InProcess/
│   ├── InProcessTransport.cs            : ITransport
│   ├── InProcessTransportListener.cs    : ITransportListener
│   ├── InProcessServerConfig.cs         : ServerConfig
│   ├── InProcessClientConfig.cs         : ClientConfig
│   └── InProcessRendezvous.cs           : process-local listener registry
```

`InProcessRendezvous` is a `static ConcurrentDictionary<string, InProcessTransportListener>` keyed by endpoint string. `InProcessServerConfig.OnCreateServerListener()` registers a listener under `Endpoint`. `InProcessClientConfig.OnConnectTransport()` looks up the listener by `Endpoint` and asks it to produce a paired `InProcessTransport`.

Each pair shares two `System.IO.Pipelines.Pipe` instances cross-wired: `serverOut → clientIn` and `clientOut → serverIn`. The two `InProcessTransport` instances each hold one input `PipeReader` and one output `PipeWriter`, exposed via `IDuplexPipe.Input` / `Output`.

`CloseAsync(linger)` completes the writer; the peer's reader sees `IsCompleted = true` on next read, mirroring socket close semantics.

Create `src/NexNet.Testing.Tests/NexNet.Testing.Tests.csproj` (NUnit) with one focused test class verifying the transport's basic round-trip, multi-message ordering, and clean close behavior in isolation.

**Files:** all new under `src/NexNet.Testing/` and `src/NexNet.Testing.Tests/`. Update solution file `src/NexNet.sln` to include both new projects.

**Tests:** New `NexNet.Testing.Tests` covers the transport in isolation. A `dotnet build src` confirms the solution builds end-to-end.

## Phase 6 — Add `Type.InProcess` to `NexNet.IntegrationTests` matrix

Reference `NexNet.Testing` from `NexNet.IntegrationTests`. Add `InProcess` to the `Type` enum in `BaseTests.cs`. Extend the config-creation switch to produce `InProcessServerConfig` / `InProcessClientConfig` with a unique endpoint string per test (e.g., test name + `Guid.NewGuid()`). Extend port-allocation paths to no-op for InProcess.

Add `[TestCase(Type.InProcess)]` to a representative subset of existing test classes — start with `NexusClientTests`, `NexusServerTests`, `NexusServerTests_SendInvocation`, `NexusServerTests_ReceiveInvocation`, `NexusServerTests_NexusInvocations`, `NexusServerTests_Authorization`. Run and ensure all pass. If specific tests fail because they assume socket semantics (e.g., RemoteAddress format), adjust them to be transport-agnostic or skip them on InProcess with a clear annotation.

After the representative subset is green, expand to the full matrix where it makes sense (skip Sockets/* and Security/RawTcpClient.cs which are inherently socket-bound).

**Files:** `src/NexNet.IntegrationTests/NexNet.IntegrationTests.csproj`, `src/NexNet.IntegrationTests/BaseTests.cs`, plus per-test-class `[TestCase]` additions.

**Tests:** No new tests authored; existing tests gain a new transport. All previously-passing tests must continue to pass on existing transports; InProcess matrix must be all-green.

## Phase 7 — `NexusAssertionException`, `Arg` matchers, `InvocationRecorder`, expression machinery

Create the recorder primitives in `NexNet.Testing` (no dependency on the interceptor yet):

```
src/NexNet.Testing/
├── NexusAssertionException.cs
├── Recording/
│   ├── Arg.cs                       (Any<T>(), Is<T>(predicate))
│   ├── ArgMatcher.cs                (internal — wildcard or equality)
│   ├── InvocationRecord.cs          (methodId, raw args bytes, optional MethodInfo)
│   ├── InvocationRecorder.cs        (thread-safe append, snapshot, lock-free reads)
│   └── ExpressionParser.cs          (resolves Expression<Action<T>> to method + arg matchers)
```

`Arg.Any<T>()` and `Arg.Is<T>(predicate)` are sentinel calls intercepted by `ExpressionParser`. The parser walks the lambda body, identifies the `MethodCallExpression`, extracts the called `MethodInfo`, and converts each argument expression to an `ArgMatcher` (constant equality, wildcard, or predicate).

**Files:** all new under `src/NexNet.Testing/`.

**Tests:** Unit tests for `ExpressionParser` covering: simple constant args, `Arg.Any<T>()`, `Arg.Is<T>(predicate)`, mixed; methods with multiple parameters; unsupported expressions (throw with a clear message).

## Phase 8 — `TestInvocationInterceptor` + `QuiescenceTracker` + lazy arg deserialization

Implement the harness's `IInvocationInterceptor` impl in `NexNet.Testing`. On `WrapAsync`:

1. Increment `inDispatch`.
2. Create an `InvocationRecord` populated with `(message.MethodId, message.Arguments)` (the raw args bytes).
3. Append to the per-session `InvocationRecorder`.
4. `try { await invoke(); } finally { Decrement(inDispatch); SignalChange(); }`.

`QuiescenceTracker` aggregates counters across all sessions plus the in-process transport's `bytesInTransit` counter and the `IPipeFactory`'s `activePipes` counter (added in Phase 9). Exposes `Task QuiesceAsync(CancellationToken)` implementing the observe-zero / yield / re-observe-zero pattern from the design discussion, signaled by `AsyncManualResetEvent`.

Lazy arg deserialization: at `NexusTestHost` construction, walk `typeof(IServerNexus)` and `typeof(IClientNexus)` via reflection. For each method, compute the same method ID the generator computes (lookup key: `MethodInfo`). The harness needs the `ValueTuple<...>` argument shape per method to deserialize via MemoryPack — derive from `MethodInfo.GetParameters()`. Store map: `(Type interfaceType, ushort methodId) → (MethodInfo, Type tupleType)`.

When an assertion compares against a recorded `InvocationRecord`, the harness:
1. Resolves the assertion's `MethodInfo` to a method ID via the map.
2. Filters records by method ID.
3. For arg-matching records, deserializes raw args using the tuple type, compares each tuple element via `ArgMatcher`.

**Note:** Method-ID derivation must match the generator. `TypeHasher` is Roslyn-only, so the harness derives the same ID by reading the *generated* `IInvocationMethodHash` implementations on the proxy/nexus types (the source generator emits these). The harness reflects on generated classes, not on user interfaces, to retrieve actual method IDs.

**Files:** all new under `src/NexNet.Testing/Quiescence/` and `src/NexNet.Testing/Recording/`.

**Tests:** Unit tests for `QuiescenceTracker` (counter races, observe-zero correctness, signal-after-decrement). Unit tests for the method-ID resolution map. Tests combining a fake interceptor invocation log with `QuiesceAsync`.

## Phase 9 — `TappingPipeReader/Writer`, `PipeRecording`, `TestPipeFactory`, `activePipes` counter

Implement the pipe tap layer:

```
src/NexNet.Testing/
├── Streaming/
│   ├── TappingPipeReader.cs    (records on AdvanceTo)
│   ├── TappingPipeWriter.cs    (records on FlushAsync)
│   ├── PipeRecording.cs        (ConsumedBytes, WrittenBytes, IsCompleted, FaultedWith,
│   │                            WaitForBytesAsync, WaitForCompletionAsync)
│   └── TestPipeFactory.cs      (IPipeFactory impl: wraps inner pipe, registers recording,
│                                 increments activePipes on rent, decrements on CompleteTask)
```

`TappingPipeReader` extends `PipeReader`. Tracks last-returned buffer; on `AdvanceTo(consumed)`, computes the slice from prior consumed-watermark to new position, copies to `PipeRecording.ConsumedBytes`. `TappingPipeWriter` extends `PipeWriter`; records each `FlushAsync`-committed run.

`TestPipeFactory.WrapLocal` / `WrapRemote` create a small `INexusDuplexPipe` proxy that delegates everything to the inner pipe but substitutes `Input` and `Output` with tap wrappers. Hooks the inner pipe's `CompleteTask.ContinueWith(_ => Decrement(activePipes); SignalChange())` so the quiescence counter is correct even if the user code abandons a pipe.

**Files:** all new under `src/NexNet.Testing/Streaming/`.

**Tests:** Unit tests for `TappingPipeReader` / `TappingPipeWriter` (consumed-vs-visible recording, bidirectional duplex, completion propagation, exception capture). Test that `TestPipeFactory` correctly increments / decrements `activePipes` across rent / complete / abort cycles.

## Phase 10 — `NexusTestHost`, `NexusTestClient`, `ConnectAsync`, auth integration

Wire everything together into the entry-point API:

```
src/NexNet.Testing/
├── NexusTestHost.cs                    (static Create<TS, TC>(opts))
├── NexusTestHost`2.cs                  (NexusTestHost<TS, TC>)
├── NexusTestClient`2.cs                (NexusTestClient<TS, TC>)
├── NexusTestHostOptions.cs             (ServerNexusFactory, ClientNexusFactory, identities)
├── Authentication/
│   ├── TestIdentity.cs                 (Of(name, roles) → IIdentity + opaque token)
│   └── TestAuthenticationStore.cs      (token bytes ↔ IIdentity registry, used by override)
```

`NexusTestHost.Create<TS, TC>(configure)` constructs an `InProcessServerConfig` with `OnAuthenticateOverride` pointing at the harness's auth store, installs the `TestInvocationInterceptor` and `TestPipeFactory` on `ConfigBase`, starts the server. Returns `NexusTestHost<TS, TC>`.

`ConnectAsync(asUser?, roles?)` issues a `TestIdentity.Of(...)` token, stamps it on a fresh `InProcessClientConfig.Authenticate = () => token`, constructs a client via the user's generated `CreateClient` factory, connects, and returns a `NexusTestClient<TS, TC>` handle exposing `.Server` (proxy), `.Nexus` (the user's client nexus instance), `.SessionId` (resolved post-connect), and the per-session `InvocationRecorder`.

`QuiesceAsync()` delegates to the host's `QuiescenceTracker`.

**Files:** all new under `src/NexNet.Testing/`.

**Tests:** A minimal end-to-end test: spin up a host with a trivial test nexus, connect, invoke a server method, await `QuiesceAsync`, verify the invocation record exists. A multi-client test exercising 5+ concurrent connections.

## Phase 11 — Streaming helpers, `ChannelRecording<T>`, `TapChannel<T>`

Add the convenience-helper extension methods on `NexusTestHost<TS, TC>`:

```
src/NexNet.Testing/
├── Streaming/
│   ├── ChannelRecording.cs                     (Items, IsCompleted, WaitForCountAsync)
│   ├── PipeStreamingExtensions.cs              (PipeUpload, PipeDownload)
│   └── ChannelStreamingExtensions.cs           (ChannelCollect<T>, ChannelPublish<T>, TapChannel<T>)
```

Each helper signature follows the form `Func<INexusDuplexPipe, ValueTask> invoke` or `Func<INexusChannelWriter<T>, ValueTask> invoke` — a delegate the user supplies that calls the proxy method with the harness-provided pipe/channel.

`PipeUpload` creates a pipe via the proxy's pipe-creation API, kicks off `invoke(pipe)`, writes `data` to `pipe.Output`, completes, awaits the invocation. `PipeDownload` is the mirror — invoke, read until complete, return collected bytes. `ChannelCollect<T>` / `ChannelPublish<T>` are the typed channel equivalents. `TapChannel<T>(invoke)` returns a `ChannelRecording<T>` wired to a tap-wrapped channel.

**Files:** all new.

**Tests:** Each helper covered by a focused test on a representative pipe/channel-using nexus method.

## Phase 12 — `AssertReceived` / `AssertNotReceived` / `WaitFor`

Add the assertion API as instance methods on `NexusTestClient<TS, TC>` (and a server-recorder variant on `NexusTestHost<TS, TC>` for "what did the server see"):

```csharp
public void AssertReceived<TInterface>(Expression<Action<TInterface>> expr, int times = 1);
public void AssertNotReceived<TInterface>(Expression<Action<TInterface>> expr);
public Task WaitFor<TInterface>(Expression<Action<TInterface>> expr, TimeSpan? timeout = null);
```

`AssertReceived` (sync, post-`QuiesceAsync`): parses `expr` via `ExpressionParser`, looks up method ID, scans the recorder for matches, throws `NexusAssertionException` with a diagnostic message on mismatch. Includes the actual recorded arg values vs. the expected matchers in the message.

`AssertNotReceived` (sync, post-`QuiesceAsync`): expects zero matches; throws if any found.

`WaitFor` (async, with timeout): awaits a match arriving in the recorder up to `timeout` (default 5 seconds). Implemented via the recorder's change signal. Throws `TimeoutException` (which test frameworks already surface as a meaningful failure) if no match arrives.

Diagnostic messages must include: the expected method, expected matchers (rendered via `ArgMatcher.ToString()`), and a list of nearby recorded invocations (e.g., the most recent N on the same interface) to aid debugging.

**Files:** new on `NexusTestClient`, `NexusTestHost`. Diagnostics rendering helpers under `src/NexNet.Testing/Recording/`.

**Tests:** Unit tests for each of the three APIs, including: positive/negative matches, wildcard matchers, predicate matchers, multi-arg methods, the times-mismatch branch of `AssertReceived`, the timeout branch of `WaitFor`, the diagnostic message content.

## Phase 13 — Group introspection + end-to-end harness samples

Expose group membership for assertions:

```csharp
// On NexusTestHost<TS, TC>:
public IReadOnlyDictionary<string, IGroupView> Groups { get; }

public interface IGroupView
{
    IReadOnlyList<long> Members { get; }
}
```

Implementation reads from the server's `LocalGroupRegistry.GetLocalGroupMembers(name)` (returns `IEnumerable<INexusSession>`) and projects to session IDs.

Add an end-to-end sample test class `HarnessShowcaseTests.cs` in `NexNet.Testing.Tests` demonstrating each broadcast pattern from the design discussion: `GroupExceptCaller`, `AllExcept`, `Client(id)`, bulk broadcast over 50 clients. These also serve as in-tree documentation.

**Files:** group-introspection additions on `NexusTestHost`, the showcase test class.

**Tests:** The showcase class is itself the test surface — each broadcast pattern verified end-to-end.

## Out of scope (deferred)

- **TimeProvider integration** — issue #75.
- **Inner-layer `IInvocationInterceptor`** with typed args (would require generator changes). Outer layer is sufficient for v1.
- **Channel factory hook in core.** v1 ships helpers-only.
- **Test-framework-specific sugar** (NUnit/xUnit attributes). Core API throws standard exceptions; users wrap as needed.
- **`FakeTimeProvider`-driven tests of auth-cache TTL, ping, reconnect.** Blocked on #75.

## Phases-total: 13
