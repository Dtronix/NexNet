## Summary
- Closes #75
- Routes app-logic time-dependent sites in `src/NexNet/` through a single `ConfigBase.Time` surface so tests can drive time deterministically with `FakeTimeProvider`.
- Removes the ad-hoc `ServerNexusBase.TickCountOverride` test seam in favor of the unified mechanism.
- 21 commits on the branch, 13 implementation phases + REMEDIATE + Option A clarification, +515/-125 lines across 27 source files.

## Reason for Change
The old code mixed `Environment.TickCount64`, `new Timer(...)`, `Task.Delay(int)`, `DateTime.UtcNow`, and `Stopwatch.GetTimestamp()` across the codebase. Time-dependent features (auth cache TTL, reconnect backoff, rate-limiter sliding windows, ping interval, idle pool reaping) could only be tested with real-time waits — slow and flaky. The `TickCountOverride` seam on `ServerNexusBase` was a one-off escape hatch.

`TimeProvider` (.NET 8+) is the BCL-blessed abstraction; pairing it with `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider` gives deterministic tests across every subsystem with a single config surface.

## Impact
- Default behavior unchanged. `ConfigBase.Time` defaults to `TimeProvider.System`, so any consumer who doesn't touch the new property sees zero behavioral or performance change.
- The internal `TimeProviderExtensions.GetTickCount64()` extension on `TimeProvider.System` is Stopwatch-backed (~15-25 ns), preserving the existing `Environment.TickCount64` cost profile.
- Test count: 2628 → 2637 integration (+9 net: 1 auth-cache migration + 2 new auth-cache + 2 new rate-limiter + 1 pool idle reaping + 1 ping cadence + 1 relay reconnect + 1 reconnect-delay + 1 server watchdog; offset by 1 dead-code test removed by Phase 12). 149 generator tests unchanged.

## Design clarification: app-logic time vs. wait-for-the-kernel
During the test-expansion pass, the test suite hung for 22 minutes in CI. Root cause: the initial migration routed every `Task.Delay` through `TimeProvider`, including ones that exist purely to give the OS real wall-clock time for real network operations. Faking those waits doesn't accelerate any real kernel work and deadlocks teardown when tests stop advancing `FakeTimeProvider`.

The PR ends with this clean split:

**Routed through `TimeProvider`** (app-logic time decisions — legitimately fakeable):
- Reconnect backoff (`NexusClient.cs:235`, `NexusListRelay.cs:153`)
- Pipe back-pressure polling (`NexusPipeReader.cs:115`)
- All `CreateTimer` sites: ping timer, server watchdog, pool health check, rate-limit cleanup
- All elapsed-duration tick math: auth cache TTL, session `LastReceived`, rate-limiter sliding window, pool `_lastUsed`

**Kept on real time** (wait for the OS network stack):
- `NexusSession.DisconnectDelay` — TCP buffer flush grace period
- `NexusSession.Receiving.HandshakeTimeout` — real handshake deadline
- `SocketTransport.ConnectionTimeout` — TCP/Uds/Quic connect
- `WebSocketPipe` close timeout — WebSocket close-ack deadline

## Plan items implemented as specified
All 13 plan phases landed:
1. `ConfigBase.Time` surface + internal `GetTickCount64()` helper.
2. `NexusClient` ping timer, reconnect delay, idle-timeout calc.
3. `NexusServer` watchdog timer (deferred from parameterless ctor to `Configure()`).
4. `NexusSession` `LastReceived` and (later reverted) disconnect/handshake delays.
5. `NexusClientPool` health timer + last-used timestamps.
6. `ConnectionRateLimiter` ctor + sliding window + cleanup timer.
7. `ServerNexusBase` auth cache via `TimeProvider`; `TickCountOverride` deleted; `AuthCache_AttributeTtl_ExpiredEntry_ReChecks` migrated to `FakeTimeProvider`.
8. `NexusPipeReader` back-pressure delay.
9. `NexusCollectionManager` → `NexusListRelay` threading.
10. `SocketTransport` connect timeout (later reverted to real time).
11. `WebSocketPipe` close timeout (later reverted to real time).
12. Delete dead `RegisteredInvocationState.Created` field.
13. `Microsoft.Extensions.TimeProvider.Testing` package added; new FakeTimeProvider-driven tests.

## Deviations from plan implemented
- **`ConfigBase.Time` is `{ get; set; }` not `{ get; init; }`** — matches every other ConfigBase property and lets tests override `Time` on an already-constructed config returned from helper methods. Documented in the property's XML doc.
- **`Microsoft.Extensions.TimeProvider.Testing` package added in phase 7** rather than phase 13.
- **`NexusPipeReader.time` is an optional ctor parameter** (defaults to `TimeProvider.System`) while `ConnectionRateLimiter.time` is required — `NexusPipeReader` has 30+ direct test ctor sites and back-pressure timing isn't commonly fake-time-tested.
- **Four network-wait Task.Delay sites reverted to real time** (the design clarification above).

## Gaps in original plan implemented
Review surfaced two correctness pitfalls caused by the `init → set` deviation and four documentation gaps:
- **`NexusClientPool.PooledClient`** reads `Time` dynamically via the shared pool config so `_lastUsed` writes stay consistent with `PerformHealthAndIdleCheck` reads even if `Config.Time` is mutated.
- **`NexusClient._pingTimer`** construction deferred from ctor to `TryConnectAsyncCore` so the timer uses whatever `TimeProvider` is current at connect-time.
- **`NexusServer.DisposeAsync`** now disposes `_watchdogTimer` (pre-existing oversight surfaced by the review).
- **`ConfigBase.Time` XML doc** explains the "set before lifecycle methods" contract and what mutating it later does and doesn't affect.
- **`INexusSession.DisconnectIfTimeout` doc** updated to reference `Config.Time.GetTickCount64()`.

Additional FakeTimeProvider coverage delivered beyond the original plan:
- `PerIpWindow_FakeTimeProvider_ExpiredEntriesAllowNewConnections`
- `BanExpiration_FakeTimeProvider_ExpiredBanReleasesIp`
- `Pool_FakeTimeProvider_DisposesIdleClientsDeterministically`
- 7 existing `NexusClientPoolTests` migrated to `FakeTimeProvider.Advance` instead of `Task.Delay` (eliminates ~1.5s of real-time waits and several non-deterministic boundary assertions; also fixes the pre-existing `Pool_RespectsMinIdleConnections` Linux-CI flake).
- `AuthCache_AttributeTtl_SecondCallUsesCachedResult` hardened with `FakeTimeProvider`.
- `Client_PingTimer_FiresAtConfiguredInterval_DrivenByFakeTime` — precise multi-interval cadence.
- `RelayReconnect_500msBackoff_DrivenByFakeTime` — pins the `NexusListRelay` 500ms boundary.
- `Client_ReconnectDelay_DrivenByFakeTime` — exercises a 30-second `DefaultReconnectionPolicy` delay deterministically.
- `Server_ConnectionWatchdog_DisconnectsTimedOutSessions_DrivenByFakeTime` — first direct coverage of `NexusServer.ConnectionWatchdog`.

## Migration Steps
None — purely additive for external consumers. Add `Time = fakeTime` to your `ServerConfig` / `ClientConfig` object initializer to opt in.

## Performance Considerations
- `TimeProvider.System.GetTimestamp()` is Stopwatch-backed; `GetTickCount64()` extension adds one integer division per call. Net cost in `LastReceived` write path and watchdog read: ~15-25 ns each, dominated by the Stopwatch read.
- All timers go through `TimeProvider.CreateTimer` which under `TimeProvider.System` wraps `System.Threading.Timer` — byte-equivalent default behavior.
- No new allocations in steady-state code paths.

## Security Considerations
- `ConfigBase.Time` widens mutation surface (from `init` to `set`), but `ConfigBase` is the canonical mutable configuration class — every other property is already `{ get; set; }`. No security boundary crossed; the time source is local to the server/client instance.
- `Microsoft.Extensions.TimeProvider.Testing` is added only to `NexNet.IntegrationTests` (test-only). Production `NexNet` core has no new dependencies.

## Breaking Changes
- **Consumer-facing**: None. `ConfigBase.Time` is purely additive with a `TimeProvider.System` default.
- **Internal**: `ConnectionRateLimiter`, `NexusCollectionManager`, `NexusListRelay<T>`, `NexusPipeReader` ctor signatures changed. All are `internal` types; all call sites (production + test) are updated within this PR.

## Intentional non-scope
Issue #75's body lists `MutexSlim` (internal lock-contention machinery, no test value) and `CoreLogger.Stopwatch` (cosmetic diagnostic timestamps) as refactor sites. Both were intentionally skipped during DESIGN — refactoring either would touch load-bearing or low-value code with no test payoff. Recorded in `workflow.md` Decisions (2026-05-06 entries).

## Follow-up work surfaced but not in this PR
- `B3` client-side timeout disconnect test — client pings race with `FakeTimeProvider.Advance`; server-side `B4` covers the same architectural property safely.
- `B5` server `DisconnectDelay` and `B6` `WebSocketPipe` close-timeout tests — would require raw-TCP/raw-WebSocket harness scaffolding for an unresponsive-peer scenario.
- Additional `ConnectionRateLimiter` window scenarios beyond the two added here.
