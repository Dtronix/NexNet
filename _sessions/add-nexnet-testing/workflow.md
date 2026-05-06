# Workflow: add-nexnet-testing

## Config
platform: github
remote: https://github.com/Dtronix/NexNet.git
base-branch: master

## State
phase: IMPLEMENT
status: active
issue: discussion
pr:
session: 2
phases-total: 13
phases-complete: 1

## Problem Statement

Add an in-memory transport to NexNet core (`MemoryTransport` / `MemoryTransportListener` under `src/NexNet/Transports/Memory/`) and a new `NexNet.Testing` package providing a test harness on top of it. The harness lets end users (developers building apps on NexNet) test their own nexus implementations — server methods, client callbacks, authorization rules, broadcasts, group routing, pipes, and channels — without standing up sockets, ports, TLS, or fake auth providers.

Three optional internal hooks are added to `NexusSessionConfigurations` and default to `null` pass-through, so production hot paths remain zero-cost:

- `IInvocationInterceptor` — wraps invocation dispatch (used by harness for quiescence counters and the invocation recorder).
- `IPipeFactory` — produces pipe instances at creation time (used by harness to wrap pipes with `TappingPipeReader` / `TappingPipeWriter`).
- `IChannelFactory` — produces channel reader/writer pairs (used by harness to wrap channels with item-recording wrappers).

Public end-user surface (single namespace `NexNet.Testing`):

- `NexusTestHost.Create<TServerNexus, TClientNexus>(opts)` — entry point.
- `NexusTestHost<TS, TC>` — `ConnectAsync`, `ConnectAsAsync(roles)`, `QuiesceAsync`, `Groups`, `ServerNexus`, plus `PipeUpload` / `PipeDownload` / `ChannelCollect` / `ChannelPublish` / `TapNextPipe` / `TapChannel` extensions.
- `NexusTestClient<TS, TC>` — per-client handle: `.Server` (proxy), `.Nexus`, `.SessionId`, `.AssertReceived<I>(expr)`, `.AssertNotReceived<I>(expr)`, `.WaitFor<I>(expr)`.
- `Arg.Any<T>()`, `Arg.Is<T>(predicate)` — expression matchers.
- `TestIdentity.Of(name, roles)` — fake principal helper.
- `PipeRecording`, `ChannelRecording<T>` — observation surfaces with `WaitForBytesAsync` / `WaitForCountAsync`.

Quiescence is the load-bearing primitive that makes negative assertions (`AssertNotReceived`) meaningful: a `QuiesceAsync` that tracks three per-session counters (`bytesInTransit`, `inDispatch`, `pendingResults`) plus an `activePipes` lifecycle counter for pipes whose handler returned but whose stream is still open.

## Test baseline (pre-change)

- `dotnet test src/NexNet.Generator.Tests -c Release` → 149/149 passing.
- `dotnet test src/NexNet.IntegrationTests -c Release` → 2628/2628 passing.
- No pre-existing failures.

## Decisions

- 2026-05-06 — **Scope is bundled.** Ship `MemoryTransport` and `NexNet.Testing` together in one workflow (per user). Note: branch named `add-nexnet-testing` even though scope includes the transport.
- 2026-05-06 — **`MemoryTransport` lives in `NexNet` core, not in `NexNet.Testing`.** Reason: in-process RPC is a legitimate non-test use case (monolith with in-process modules); putting it in a Testing package would force production code to take a Testing dependency. It also lets the existing integration test suite validate it as just another transport. *(REVISED 2026-05-06: see Revisions section.)*
- 2026-05-06 — **Three core hooks (`IInvocationInterceptor` / `IPipeFactory` / `IChannelFactory`) are `internal`** with `InternalsVisibleTo("NexNet.Testing")` rather than `public`. Reason: only the harness needs them in v1; promoting them to public is a one-way door. Defer until external demand exists.
- 2026-05-06 — **`NexNet.Testing` is test-framework agnostic.** `AssertReceived` throws `NexusAssertionException` — NUnit/xUnit/MSTest surface that as a failure with no integration package needed.
- 2026-05-06 — **Quiescence model: three counters + pipe-lifecycle counter.** `bytesInTransit` (write-side increment, reader-AdvanceTo decrement), `inDispatch` (interceptor entry/exit), `pendingResults` (existing `RegisteredInvocationState` registry count), `activePipes` (incremented when a pipe is opened, decremented when it completes — covers the case where the invocation that opened the pipe has returned but the stream is still in use). `QuiesceAsync` waits for all four to be zero, with an `await Task.Yield()` re-check to drain synchronously-scheduled continuations. Documented residual: pure `Task.Run` fire-and-forget inside a handler is undetectable.
- 2026-05-06 — **Pipe tap records consumed bytes, not visible bytes.** `TappingPipeReader` records on `AdvanceTo`, capturing only the slice the user actually committed past — matches "what the handler saw."
- 2026-05-06 — **Recorder API uses LINQ expressions** (`AssertReceived<I>(n => n.M(arg, Arg.Any<T>()))`). Method ID lookup via the same `TypeHasher` machinery the generator uses; arg matching via `ExpressionVisitor`.
- 2026-05-06 — **Rollout is staged.** Step 1 ships `MemoryTransport` only and validates it by adding `Type.Memory` to existing `[TestCase]` matrices. Step 2 adds the hooks (pure refactor, no behavior change). Step 3 ships `NexNet.Testing`. Steps will be reflected as plan phases.
- 2026-05-22 — **Plan approved.** User approved `plan.md` as drafted. IMPLEMENT begins with Phase 1.

### Revisions after source verification (2026-05-06)
- **Hook location:** Two nullable fields added to the `NexusSessionConfigurations` readonly struct (per user choice). Authoritative install point is on `ConfigBase` (so users set hooks once and all sessions inherit); the struct's session-construction site copies them in, giving `NexusSession` direct field access.
- **Hooks reduced to two, not three.** Channels do not need a core factory in v1 — harness convenience helpers (`ChannelCollect<T>` / `ChannelPublish<T>` / `TapChannel<T>`) own channel creation and return tap-wrapped instances. Pipe-layer byte tap still observes any user-instantiated channels.
- **`IInvocationInterceptor` is "outer" only.** Wraps `nexus.InvokeMethod(message)` in `NexusSession.Receiving.cs` (around line 682). Has access to `InvocationMessage` (method ID + serialized arg bytes). Sufficient for quiescence; for assertion arg matching, harness lazily MemoryPack-deserializes args using a per-interface `methodId → ITuple<...>` map built via reflection over the user's `IServerNexus` / `IClientNexus` interfaces. NO generator changes required.
- **Auth model: dual.** New nullable `Func<ReadOnlyMemory<byte>?, ValueTask<IIdentity?>>? OnAuthenticateOverride` added to `ServerConfig` (or `ConfigBase`-server side). When set, `ServerNexusBase.Authenticate` consults it before falling back to `OnAuthenticate`. Test harness installs an override mapping fake tokens (issued via `TestIdentity.Of(...)`) to `IIdentity` instances. Users testing their own auth leave override unset.
- **`TypeHasher` reuse not needed.** Recorder/assertion API resolves expressions to `MethodInfo`; matching is on method-name+signature against entries the interceptor records as `(InvocationMessage, MethodInfo)` pairs. The MethodInfo lookup is from a pre-built map (interface type → MethodId → MethodInfo) constructed via reflection at host startup.
- **`IPipeFactory` hook point:** `NexusPipeManager.RentPipe` (local) and `NexusPipeManager.RegisterPipe(byte)` (remote). Factory wraps the rented `RentedNexusDuplexPipe` / `NexusDuplexPipe` with a tap. On return-to-pool, the wrapper detaches and the underlying pipe resets normally.
- **Quiescence counter sources confirmed:**
  - `bytesInTransit` — instrumented inside `MemoryTransport`'s pipe writer/reader pair (the harness owns this transport, so it can count framed messages without touching session code).
  - `inDispatch` — incremented/decremented by the harness's `IInvocationInterceptor` around `InvokeMethod`.
  - `pendingResults` — read from `SessionInvocationStateManager._invocationStates.Count`. Will need a small internal accessor (e.g., `internal int PendingInvocationCount => _invocationStates.Count;`) added to that class.
  - `activePipes` — tracked by the harness's `IPipeFactory` (increment on rent, decrement on the pipe's `CompleteTask`).
- **`MemoryTransport` rendezvous design:** `MemoryServerConfig` registers the listener under a named endpoint (e.g., a string key) in a process-local registry. `MemoryClientConfig.OnConnectTransport()` looks up the listener and asks it to produce a paired transport. The pair share two `System.IO.Pipelines.Pipe` instances cross-wired (server-out → client-in, client-out → server-in). Each `MemoryTransport` instance implements `IDuplexPipe` directly over its assigned input/output pipe ends.
- **Group introspection:** `LocalGroupRegistry.GetLocalGroupMembers(string)` already returns `IEnumerable<INexusSession>`; the harness exposes a thin wrapper `host.Groups[name].Members` that yields `SessionId` values. No core change.
- **Transport home revised: `InProcessTransport` lives in `NexNet.Testing`, not core.** Reason: production use cases are thin (modular monoliths, debug workflows) and not common in .NET. The integration-test-validation argument is preserved by having `NexNet.IntegrationTests` take a project reference to `NexNet.Testing` so the InProcess transport joins the existing transport `[TestCase]` matrix.
- **Type names:** `NexusTestHost` / `NexusTestHost<TS, TC>` / `NexusTestClient<TS, TC>` (matches existing `NexusServer`/`NexusClient`/`NexusBase` naming pattern).
- **Test framework integration:** Framework-agnostic. `AssertReceived` throws `NexusAssertionException`; NUnit/xUnit/MSTest surface it as a test failure with no integration package needed.
- **TimeProvider injection deferred to follow-up issue #75.** Verification revealed the actual scope (14 time refs + 5 timers + 9 `Task.Delay` calls + the existing `TickCountOverride` test seam on `ServerNexusBase`) is large enough to warrant its own focused workflow. Harness ships in v1 *without* time control as a known limitation. Tests of time-sensitive logic (auth cache TTL, reconnect, ping) remain real-time-bound until #75 lands. Issue body lives in `_sessions/add-nexnet-testing/timeprovider-issue-body.md` for reference.

## Suspend State

_(none — workflow resumed and active. WIP commit `77553c3` will be amended on first real commit.)_

### Context carried from Session 1 (recorded for durability)
- User opted to defer TimeProvider integration to a follow-up issue (#75 created at https://github.com/Dtronix/NexNet/issues/75).
- User picked `NexusSessionConfigurations struct` for hook location even though Claude's recommendation was `ConfigBase`. Plan adapts: hooks are stored on `ConfigBase` (authoritative install point) and copied into the struct at session-construction time so the runtime read happens via the struct as the user requested.
- User picked `InProcessTransport` (revised from `MemoryTransport`) and chose `NexNet.Testing` as its home (revised from `NexNet` core).
- User-confirmed type names: `NexusTestHost`, `NexusTestClient`. Test framework: agnostic. Auth: dual mode.
- Method-ID derivation strategy in Phase 8: harness reflects on the *generated* `IInvocationMethodHash`-implementing classes (proxy/nexus types) to read actual method IDs at runtime — `TypeHasher` is Roslyn-only and not reusable from runtime test code.

## Session Log
| # | Phase Start | Phase End | Summary |
|---|------------|-----------|---------|
| 1 | 2026-05-06 INTAKE | 2026-05-06 DESIGN | Bootstrapped workflow from discussion. Worktree + branch `add-nexnet-testing` created. Baseline tests green (149 generator + 2628 integration). |
| 1 | 2026-05-06 DESIGN | 2026-05-06 PLAN | Verified core surfaces against actual source via Explore agent. Recorded 7 design decisions and revised them after verification (transport home moved to `NexNet.Testing`; hooks reduced to two; outer-only interceptor; channels via helpers only; auth dual-mode). Created issue #75 to track deferred TimeProvider work. |
| 1 | 2026-05-06 PLAN | 2026-05-06 PLAN (suspended) | Drafted `plan.md` with 13 atomic phases, dependencies, file paths, and per-phase tests. User issued handoff before plan approval. Suspended for handoff to next session. |
| 2 | 2026-05-22 PLAN (resumed) | 2026-05-22 IMPLEMENT | Resumed suspended workflow. Plan approved by user. Phase 1 complete: `PendingInvocationCount` added to `ISessionInvocationStateManager` + concrete + test mock. All 149 generator + 2628 integration tests pass. Amended WIP commit `77553c3` into Phase 1 commit. |
