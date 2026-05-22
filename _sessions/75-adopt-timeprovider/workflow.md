# Workflow: 75-adopt-timeprovider

## Config
platform: github
remote: https://github.com/Dtronix/NexNet.git
base-branch: master

## State
phase: IMPLEMENT
status: active
issue: #75
pr:
session: 2
phases-total: 13
phases-complete: 10

## Problem Statement

Adopt `System.TimeProvider` (.NET 8+) across NexNet core so time-dependent behavior can be deterministically controlled in tests via `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`.

Time-dependent surfaces affected:
- Ping interval (NexusClient `_pingTimer`)
- Reconnection backoff (NexusClient reconnect `Task.Delay`)
- Authorization cache TTL (`ServerNexusBase` — currently uses `TickCountOverride` ad-hoc seam)
- Rate-limiter sliding window (`ConnectionRateLimiter`)
- Handshake timeout (`NexusSession.Receiving`)
- Disconnect delay (`NexusSession`)
- Watchdog timer (`NexusServer`)
- Mutex timeouts (`MutexSlim`)
- Pipe reader retry loop (`NexusPipeReader`)
- Invocation state timestamps (`SessionInvocationStateManager`)
- List relay delay (`NexusListRelay`)
- Socket transport connect timeout (`SocketTransport`)
- WebSocket close timeout (`WebSocketPipe`)
- Logger stopwatch (`CoreLogger`)
- NexusClientPool health check + idle timeout

Surface to add: `TimeProvider Time { get; init; } = TimeProvider.System;` on `ConfigBase`.

This was deferred from the `add-nexnet-testing` workflow. Removes the existing `TickCountOverride` test seam on `ServerNexusBase`.

## Test baseline (pre-change)

- `dotnet test src/NexNet.Generator.Tests -c Release` → 149/149 passing.
- `dotnet test src/NexNet.IntegrationTests -c Release` → 2628/2628 passing (2m 45s).
- No pre-existing failures.

## Decisions

- 2026-05-06 — **Hook location: `TimeProvider Time { get; init; } = TimeProvider.System;` on `ConfigBase`.** Inherited by both `ServerConfig` and `ClientConfig`. Single point of configuration for both sides.
- 2026-05-06 — **Skip `MutexSlim` refactor.** Its `(uint)Environment.TickCount` and `Task.Delay(localTimeout)` are internal lock-contention machinery (write mutex on session send), not user-observable behavior. Refactoring touches a load-bearing primitive for no test benefit.
- 2026-05-06 — **Skip `CoreLogger.Stopwatch` refactor.** Logger timestamps are cosmetic for diagnostics; threading config into every CoreLogger ctor is not worth the value.
- 2026-05-06 — **Delete `RegisteredInvocationState.Created` field.** It is set in `SessionInvocationStateManager` but never read anywhere. Dead code; remove it rather than refactor.
- 2026-05-06 — **Ticks emulation helper (chosen approach):** Internal extension `internal static long GetTickCount64(this TimeProvider time) => (time.GetTimestamp() * 1000L) / time.TimestampFrequency;`. Fastest path on `TimeProvider.System` (Stopwatch-backed, ~15-25 ns/call). Accepted overflow bound: ~29 years uptime at 10MHz Stopwatch frequency. No `Math.BigMul`. Drop-in replacement at all `Environment.TickCount64` call sites: `Environment.TickCount64` → `_config.Time.GetTickCount64()`. Preserves the existing `long timeoutTicks` comparison pattern and `LastReceived` field type — no signature changes to `DisconnectIfTimeout(long timeoutTicks)`.
- 2026-05-06 — **Rate limiter and server watchdog wiring:** `ConnectionRateLimiter` constructor adds a `TimeProvider time` parameter. `NexusServer` defers `_watchdogTimer` creation from the parameterless ctor to `Configure()` so it can use `_config.Time.CreateTimer(...)`. `NexusClient._pingTimer` likewise moves from ctor to `TryConnectAsyncCore` (or wraps in `_config.Time.CreateTimer`).
- 2026-05-06 — **`NexusListRelay` and `WebSocketPipe` Task.Delay sites refactored.** `NexusListRelay<T>` ctor adds a `TimeProvider time` parameter (passed through `NexusCollectionManager` from the owning client/server config). `WebSocketPipe` already has `_config` so uses `_config.Time` directly.
- 2026-05-06 — **Existing `TickCountOverride` test seam removed.** `ServerNexusBase.TickCountOverride` field deleted. The single test using it (`AuthCache_AttributeTtl_ExpiredEntry_ReChecks`) is migrated to `FakeTimeProvider`.
- 2026-05-06 — **Test framework integration:** Add `Microsoft.Extensions.TimeProvider.Testing` PackageReference to `NexNet.IntegrationTests`. NexNet core does NOT depend on it.
- 2026-05-06 — **Harness exposure deferred.** This branch only lands TimeProvider on `ConfigBase`. The `add-nexnet-testing` workflow (which has not shipped) will surface `Time` on `NexusTestHost.Options` when it lands.
- 2026-05-06 — **All `Task.Delay` sites that take a CancellationToken switch to the `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` overload.** Sites without a token use `Task.Delay(TimeSpan, TimeProvider)`.
- 2026-05-06 — **All `new Timer(...)` sites switch to `Time.CreateTimer(...)` returning `ITimer` (still IDisposable).**
- 2026-05-22 — **Deviation from design: `ConfigBase.Time` is `{ get; set; }` not `{ get; init; }`.** Every other ConfigBase property is `{ get; set; }` (Logger, the int-property setters, etc.), and the auth-cache test needed to override `Time` on an already-constructed `ServerConfig` returned from helper methods. Mutability of Time post-construction is documented as supported but limited: timers/delays already in flight retain the original provider.
- 2026-05-22 — **`Microsoft.Extensions.TimeProvider.Testing` package added in phase 7** (not deferred to phase 13 as originally planned). Phase 7 needed `FakeTimeProvider` to migrate the auth-cache test in the same commit; phase 13 will reuse the already-present package for the new tests.

## Suspend State

**Phase:** DESIGN, post-decision-recording, just before drafting `plan.md`.

**In progress:** All design decisions captured in `## Decisions`. Last user interaction confirmed the ticks helper choice (Option B without `Math.BigMul`). Asked the user whether they were ready for the phased plan; user invoked Handoff instead.

**Immediate next step on resume:** Confirm with user whether to proceed straight to PLAN phase (draft `plan.md` with phased implementation) or revisit any design points. If proceeding: transition to PLAN, write `_sessions/75-adopt-timeprovider/plan.md` covering numbered phases roughly grouped as:
1. `ConfigBase.Time` surface + internal `TimeProvider.GetTickCount64()` helper.
2. `NexusClient` (ping timer, reconnect Task.Delay, LastReceived/timeoutTicks via helper).
3. `NexusServer` (defer watchdog timer to Configure(), use `Time.CreateTimer`, helper-based timeoutTicks).
4. `NexusSession` (handshake Task.Delay, disconnect-delay Task.Delay, `LastReceived = Time.GetTickCount64()` in Receiving.cs).
5. `NexusClientPool` (health-check timer, DateTime.UtcNow → `Time.GetUtcNow()`).
6. `ConnectionRateLimiter` (ctor adds TimeProvider param, cleanup timer + sliding window via helper).
7. `ServerNexusBase` (auth cache TTL via `Time.GetTickCount64()`, **delete `TickCountOverride`**, migrate `AuthCache_AttributeTtl_ExpiredEntry_ReChecks` test to `FakeTimeProvider`).
8. `NexusPipeReader` (back-pressure backoff Task.Delay).
9. `NexusListRelay` (ctor adds TimeProvider param via NexusCollectionManager threading; reconnect Task.Delay).
10. `SocketTransport` (connection timeout Task.Delay).
11. `WebSocketPipe` (close timeout Task.Delay using `_config.Time`).
12. `SessionInvocationStateManager` — **delete unused `RegisteredInvocationState.Created` field** (it is dead code, not a TimeProvider migration).
13. Add `Microsoft.Extensions.TimeProvider.Testing` PackageReference to `NexNet.IntegrationTests`. Add new tests exercising auth-cache TTL expiry, reconnection backoff, and ping behavior under `FakeTimeProvider`.

**WIP commit:** `8a67fcb` on branch `75-adopt-timeprovider` — `[WIP] DESIGN: capture TimeProvider scope + decisions`. No in-progress code changes; only session-doc state.

**Test status:** Baseline pre-change still valid — 149/149 generator + 2628/2628 integration. No code modified yet.

**Unrecorded context:**
- User asked for a pros/cons style answer twice during the design Q&A (once for the ticks helper, once when the first AskUserQuestion phrasing was insufficiently detailed). Future questions in this workflow should lean toward presenting tradeoffs explicitly.
- The active workflow `add-nexnet-testing` (DESIGN phase, separate worktree at `Z:/Projects/NexNet/add-nexnet-testing/`) has a decision noting that TimeProvider injection is deferred to this issue. When this lands, that workflow's "limitations" sections need updating to reflect that time control is now available — but not in this branch.
- No clarification was raised about `IPostConfigureOptions`-style validation of `Time` (no need; `TimeProvider.System` default is always safe).

## Session Log
| # | Phase Start | Phase End | Summary |
|---|------------|-----------|---------|
| 1 | 2026-05-06 INTAKE | 2026-05-06 DESIGN | Bootstrapped workflow from issue #75. Worktree + branch `75-adopt-timeprovider` created. Baseline tests green (149 generator + 2628 integration). |
| 1 | 2026-05-06 DESIGN | 2026-05-06 DESIGN (suspended) | Explored all refactor sites listed in #75. Recorded 11 design decisions covering surface, scope, helper, wiring, removed seams, and test framework integration. Suspended at user request immediately before drafting plan.md. |
| 2 | 2026-05-22 DESIGN (resumed) | 2026-05-22 IMPLEMENT | Resumed from suspend. Re-verified refactor sites against current source. Drafted plan.md with 13 phases. User approved plan; transitioned to IMPLEMENT. |
