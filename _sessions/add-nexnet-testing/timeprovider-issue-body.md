## Description

Adopt `System.TimeProvider` (.NET 8+) across NexNet core so time-dependent behavior — ping interval, reconnection backoff, authorization cache TTL, rate-limiter sliding window, handshake timeout, disconnect delay, mutex timeouts, etc. — can be deterministically controlled in tests via `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`.

This was scoped out of the `add-nexnet-testing` workflow (which adds an in-process transport, the test harness, and recorder/quiescence infrastructure). The harness ships without time control as a known limitation; this issue tracks the follow-up.

## Location

Surface to add:

- `ConfigBase.Time { get; init; } = TimeProvider.System;`

Refactor sites (verified by grep on master at the time of writing — exact list to be re-validated when work begins):

- `src/NexNet/NexusClient.cs` — `_pingTimer = new Timer(...)`, reconnect `Task.Delay(delay.Value)`, timeout calc.
- `src/NexNet/NexusClientPool.cs` — `_healthCheckTimer`, `_lastUsed = DateTime.UtcNow`, idle-timeout comparisons.
- `src/NexNet/NexusServer.cs` — `_watchdogTimer`, timeout calc.
- `src/NexNet/RateLimiting/ConnectionRateLimiter.cs` — `_cleanupTimer`, sliding-window timestamps.
- `src/NexNet/Internals/NexusSession.cs` — `Task.Delay(_config.DisconnectDelay)`.
- `src/NexNet/Internals/NexusSession.Receiving.cs` — handshake timeout `Task.Delay`, `LastReceived = Environment.TickCount64`.
- `src/NexNet/Internals/Threading/MutexSlim.cs` — `GetTime()`, `Task.Delay(localTimeout)`.
- `src/NexNet/Pipes/NexusPipeReader.cs` — `Task.Delay` retry loop.
- `src/NexNet/Invocation/ServerNexusBase.cs` — auth cache TTL (currently uses `TickCountOverride?.Invoke() ?? Environment.TickCount64`; consolidate into `TimeProvider`, remove the ad-hoc override).
- `src/NexNet/Invocation/SessionInvocationStateManager.cs` — `state.Created = Environment.TickCount64`.
- `src/NexNet/Collections/Lists/NexusListRelay.cs` — `Task.Delay(TimeSpan.FromMilliseconds(500))`.
- `src/NexNet/Transports/SocketTransport.cs` — connection timeout `Task.Delay`.
- `src/NexNet/Transports/WebSocket/WebSocketPipe.cs` — close timeout `Task.Delay`.
- `src/NexNet/Logging/CoreLogger.cs` — `Stopwatch.StartNew()`.

## Diagnostics

Without `TimeProvider` injection, end-user tests of time-sensitive logic (auth cache expiry, reconnection behavior, ping-driven heartbeat, rate-limiter sliding windows) must use real `Task.Delay` and are inherently slow / flaky.

The existing `TickCountOverride` on `ServerNexusBase` is a one-off seam that already proves the value of injection but only addresses authorization cache testing.

## What Has Been Tried

Nothing yet — this is the tracking issue for the deferred work.

## Gathered Information

- .NET 8+ ships `TimeProvider` in BCL.
- `Microsoft.Extensions.TimeProvider.Testing` provides `FakeTimeProvider` with `Advance(TimeSpan)` for deterministic tests.
- `TimeProvider.CreateTimer(...)` substitutes for `new Timer(...)`.
- `TimeProvider.GetTimestamp()` substitutes for `Stopwatch.GetTimestamp()`.
- `TimeProvider.GetUtcNow()` substitutes for `DateTime.UtcNow`.
- Cancellation-aware `Task.Delay` with TimeProvider: `Task.Delay(delay, timeProvider, ct)` (single-overload available).
- `Environment.TickCount64` has no direct `TimeProvider` equivalent; sites using it for elapsed-duration comparisons should switch to `GetTimestamp()` + `GetElapsedTime(start)`.

## Suggested Approach

1. Add `TimeProvider Time { get; init; } = TimeProvider.System;` to `ConfigBase`.
2. Plumb `Configs.Time` through to every refactor site listed above.
3. Replace `new Timer(...)` with `Configs.Time.CreateTimer(...)`.
4. Replace `DateTime.UtcNow` with `Configs.Time.GetUtcNow()`.
5. Replace `Environment.TickCount64` (used for elapsed-duration math) with `Configs.Time.GetTimestamp()` + `Configs.Time.GetElapsedTime(start)`.
6. Replace `Task.Delay(timeSpan, ct)` with `Task.Delay(timeSpan, Configs.Time, ct)`.
7. Remove the ad-hoc `TickCountOverride` on `ServerNexusBase`; tests should use `FakeTimeProvider` instead.
8. Surface `TimeProvider` on `NexusTestHost.Options` so harness users can pass `FakeTimeProvider`.
9. Add integration tests that exercise auth-cache TTL expiry, reconnection backoff, and ping behavior under `FakeTimeProvider`.

Phase atomically — refactor one subsystem at a time (auth cache → ping → reconnect → rate limiter → mutex → infrastructural timers) so each commit is independently verifiable.

## Related

- Deferred from workflow `add-nexnet-testing` (PR pending).
