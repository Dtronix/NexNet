# Plan: Adopt `TimeProvider` across NexNet core

Implements issue [#75](../../../). Migrates every user-observable time-dependent site in `src/NexNet` from `Environment.TickCount64` / `new Timer(...)` / `Task.Delay(timeSpan[, ct])` / `DateTime.UtcNow` to `TimeProvider`-routed equivalents, with `ConfigBase.Time` as the single configuration surface.

Hard scope decisions from DESIGN (do **not** revisit here):

- `MutexSlim` is **not** migrated — internal lock-contention machinery, no test value.
- `CoreLogger` is **not** migrated — cosmetic diagnostic timestamps only.
- `RegisteredInvocationState.Created` is dead code (write-only). Phase 12 deletes the field rather than migrating its assignment.
- Harness exposure (`NexusTestHost.Options.Time`) is **deferred** to the `add-nexnet-testing` branch.

## Key concepts

### `ConfigBase.Time`
A single `TimeProvider Time { get; init; } = TimeProvider.System;` property on `Transports/ConfigBase.cs` becomes the contract that both `ServerConfig` and `ClientConfig` inherit. Every refactored site in this plan reaches `Time` either directly through `_config` (sessions, pipe reader via state manager, list relay) or through a constructor-injected `TimeProvider` parameter on internal helpers that don't already hold a config (`ConnectionRateLimiter`).

Default `TimeProvider.System` guarantees zero behavioral change for callers that don't opt in. Tests pass `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` instead.

### Internal `GetTickCount64()` extension
NexNet uses `Environment.TickCount64` in five places for elapsed-duration math against `long`-typed fields (`LastReceived`, `timeoutTicks`, the rate limiter sliding window, the auth cache `ExpiresAtTicks`). `TimeProvider` does not expose a direct equivalent.

Add an internal extension method in `src/NexNet/Internals/`:

```csharp
internal static class TimeProviderExtensions
{
    /// <summary>
    /// Milliseconds since an arbitrary fixed reference — drop-in replacement for
    /// <see cref="Environment.TickCount64"/>. Monotonic for a given TimeProvider instance.
    /// </summary>
    /// <remarks>
    /// On <see cref="TimeProvider.System"/> this is Stopwatch-backed (~15-25 ns/call).
    /// Wrap-around bound: ~29 years uptime at 10 MHz Stopwatch frequency.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetTickCount64(this TimeProvider time)
        => (time.GetTimestamp() * 1000L) / time.TimestampFrequency;
}
```

This deliberately mirrors the existing `long`-tick arithmetic. We do **not** rewrite call sites to use `GetTimestamp()` + `GetElapsedTime()` — that would change every comparison and field type for no behavior gain.

### Timer field type change
`new Timer(callback)` returns `System.Threading.Timer` (sealed, IDisposable). `Time.CreateTimer(callback, state, dueTime, period)` returns `System.Threading.ITimer` (also IDisposable, with `Change(...)`). Every existing `private readonly Timer _foo` field becomes `private readonly ITimer _foo` (or `private ITimer? _foo` where now lazily created). Public surface is unaffected — all timer fields are private.

`TimeProvider.System.CreateTimer(...)` produces a wrapper over `Timer`, so default-config behavior is byte-equivalent.

### `Task.Delay` overloads
Two overloads are used:
- With cancellation token: `Task.Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)` — replaces existing `Task.Delay(timeSpan, ct)`.
- Without cancellation token: `Task.Delay(TimeSpan delay, TimeProvider timeProvider)` — replaces `Task.Delay(timeSpan)` / `Task.Delay(int)`.

Sites that pass a raw `int` ms count become `TimeSpan.FromMilliseconds(value)`-shaped, since the `TimeProvider` overloads only take `TimeSpan`.

## Phase dependencies

```
1 (ConfigBase.Time + GetTickCount64 extension)
  ├── 2 (NexusClient)
  ├── 3 (NexusServer)
  ├── 4 (NexusSession + Receiving)
  ├── 5 (NexusClientPool)
  ├── 6 (ConnectionRateLimiter)                      depends on 3 (ctor wiring)
  ├── 7 (ServerNexusBase auth cache + test migration)
  ├── 8 (NexusPipeReader)
  ├── 9 (NexusListRelay via NexusCollectionManager)  depends on 2 + 3
  ├── 10 (SocketTransport)
  ├── 11 (WebSocketPipe)
  └── 12 (Delete dead RegisteredInvocationState.Created field)
13 (Add Microsoft.Extensions.TimeProvider.Testing PackageRef + new FakeTimeProvider-driven tests)
  depends on whichever earlier phases the tests exercise (2, 6, 7 at minimum).
```

Phases 2–12 are independently committable. Phase 1 must land first; phase 13 lands last (or alongside the phase it exercises).

---

## Phase 1 — Surface + helper

**File:** `src/NexNet/Transports/ConfigBase.cs`, new file `src/NexNet/Internals/TimeProviderExtensions.cs`.

Add to `ConfigBase`:
```csharp
/// <summary>
/// Time source for all time-dependent behavior on this server/client (timers, delays,
/// timeouts, cache expiry). Defaults to <see cref="TimeProvider.System"/>. Tests can pass
/// <c>FakeTimeProvider</c> from <c>Microsoft.Extensions.TimeProvider.Testing</c>.
/// </summary>
public TimeProvider Time { get; init; } = TimeProvider.System;
```

Add `TimeProviderExtensions` per "Key concepts" above.

**Tests:** None for this phase alone — it adds dormant surface. Phase 13 exercises it end-to-end.

**Commit:** `Add TimeProvider surface on ConfigBase and GetTickCount64 helper`

---

## Phase 2 — `NexusClient`

**File:** `src/NexNet/NexusClient.cs`.

Sites:
- Line 94: `_pingTimer = new Timer(PingTimer);` — `_pingTimer` is constructed inside the ctor after `_config` is assigned, so switch to `_pingTimer = _config.Time.CreateTimer(PingTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);`. Field type `Timer` → `ITimer`.
- Line 233 (reconnect loop): `await Task.Delay(delay.Value).ConfigureAwait(false);` — confirm whether a cancellation token is in scope; if yes use the 3-arg overload, else the 2-arg overload. `delay.Value` may be `TimeSpan` already; otherwise wrap.
- Line 301 (timeout calc): `var timeoutTicks = Environment.TickCount64 - _config.Timeout;` — replace with `var timeoutTicks = _config.Time.GetTickCount64() - _config.Timeout;`.

**Tests:** Existing reconnect/ping tests must remain green under `TimeProvider.System`. New `FakeTimeProvider`-driven coverage is added in phase 13.

**Commit:** `Route NexusClient ping timer, reconnect delay, and timeout through TimeProvider`

---

## Phase 3 — `NexusServer`

**File:** `src/NexNet/NexusServer.cs`.

Sites:
- Line 91 (parameterless ctor): `_watchdogTimer = new Timer(ConnectionWatchdog);` — **defer** to `Configure(...)` because the ctor has no `_config`. Field type `Timer` → `ITimer?`; declare nullable. In `Configure` (after `_config = config;`), add:
  ```csharp
  _watchdogTimer = _config.Time.CreateTimer(ConnectionWatchdog, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
  ```
  Any code that calls `_watchdogTimer.Change(...)` must already be in a post-`Configure` flow (verify during implementation by re-grepping `_watchdogTimer\.Change\|_watchdogTimer\.Dispose`).
- Line 398: `var timeoutTicks = Environment.TickCount64 - _config!.Timeout;` — replace with `_config!.Time.GetTickCount64()`.

The `Configure` method already builds `_collectionManager` and `_rateLimiter` — phase 6 will thread `_config.Time` into the rate-limiter ctor, and phase 9 into the collection manager ctor.

**Tests:** Existing watchdog tests must pass under `TimeProvider.System`. Validate that constructor → `Configure` lifecycle still produces a working server.

**Commit:** `Defer NexusServer watchdog timer to Configure() and route through TimeProvider`

---

## Phase 4 — `NexusSession` + `NexusSession.Receiving`

**Files:** `src/NexNet/Internals/NexusSession.cs`, `src/NexNet/Internals/NexusSession.Receiving.cs`.

Sites:
- `NexusSession.cs:311`: `await Task.Delay(_config.DisconnectDelay).ConfigureAwait(false);` — replace with `await Task.Delay(TimeSpan.FromMilliseconds(_config.DisconnectDelay), _config.Time).ConfigureAwait(false);`.
- `NexusSession.Receiving.cs:17`: `_ = Task.Delay(Config.HandshakeTimeout, cancellationToken).ContinueWith(CheckHandshakeComplete, cancellationToken);` — replace with `_ = Task.Delay(TimeSpan.FromMilliseconds(Config.HandshakeTimeout), Config.Time, cancellationToken).ContinueWith(CheckHandshakeComplete, cancellationToken);`.
- `NexusSession.Receiving.cs:29`: `LastReceived = Environment.TickCount64;` — replace with `LastReceived = Config.Time.GetTickCount64();`.

`DisconnectIfTimeout(long timeoutTicks)` signature stays unchanged because callers (`NexusServer.ConnectionWatchdog`, `NexusClient` timeout calc) already pass the helper-derived tick value after their phases.

**Tests:** Existing disconnect/handshake-timeout integration tests stay green.

**Commit:** `Route NexusSession disconnect/handshake delays and LastReceived through TimeProvider`

---

## Phase 5 — `NexusClientPool`

**File:** `src/NexNet/NexusClientPool.cs`.

Sites:
- Line 59: `_healthCheckTimer = new Timer(PerformHealthAndIdleCheck, null, healthCheckInterval, healthCheckInterval);` — replace with `_config.ClientConfig.Time.CreateTimer(...)` using the same args. Field type `Timer` → `ITimer`.
- Line 165: `var now = DateTime.UtcNow;` — replace with `var now = _config.ClientConfig.Time.GetUtcNow().UtcDateTime;` (or refactor `_lastUsed` to `DateTimeOffset` — see implementation note below).
- Line 240, 255: `_lastUsed = DateTime.UtcNow;` — same as above.

**Implementation note:** Decide between `DateTimeOffset _lastUsed` (idiomatic for `TimeProvider`) or keep `DateTime _lastUsed` and call `.UtcDateTime` on every read. Recommendation: switch the field to `DateTimeOffset _lastUsed` since it's a private internal field with localized usage. Verify no public API exposes `_lastUsed` as `DateTime` before changing.

**Tests:** Pool health-check / idle-timeout tests stay green under default; phase 13 adds a `FakeTimeProvider` test that advances 5× the idle timeout and asserts the connection is reaped.

**Commit:** `Route NexusClientPool health timer and last-used timestamps through TimeProvider`

---

## Phase 6 — `ConnectionRateLimiter`

**File:** `src/NexNet/RateLimiting/ConnectionRateLimiter.cs` (and the single call site at `NexusServer.cs:150`).

- Add `TimeProvider time` parameter to ctor (last param, no default): `public ConnectionRateLimiter(ConnectionRateLimitConfig config, TimeProvider time)`. Store as `_time` field.
- Line 43: `_cleanupTimer = new Timer(Cleanup, null, CleanupIntervalMs, CleanupIntervalMs);` — `_time.CreateTimer(Cleanup, null, TimeSpan.FromMilliseconds(CleanupIntervalMs), TimeSpan.FromMilliseconds(CleanupIntervalMs));`. Field type `Timer` → `ITimer`.
- Line 48 and 316: `var now = Environment.TickCount64;` → `var now = _time.GetTickCount64();`.
- `NexusServer.cs:150` becomes `_rateLimiter = new ConnectionRateLimiter(config.RateLimiting, config.Time);`.

**Tests:** Existing rate-limiter tests stay green. The class is `internal sealed` with a single call site, so ctor-signature change is safe.

**Commit:** `Inject TimeProvider into ConnectionRateLimiter for cleanup timer and sliding window`

---

## Phase 7 — `ServerNexusBase` auth cache + remove `TickCountOverride`

**Files:** `src/NexNet/Invocation/ServerNexusBase.cs`, plus the one test that uses the seam.

- Line 19: **delete** `internal Func<long>? TickCountOverride;`.
- Line 129: `var now = TickCountOverride?.Invoke() ?? Environment.TickCount64;` → `var now = SessionContext.Session.Config.Time.GetTickCount64();`.
- Line 159: `_authCache[methodId] = (result, (TickCountOverride?.Invoke() ?? Environment.TickCount64) + cacheDurationMs);` → `_authCache[methodId] = (result, SessionContext.Session.Config.Time.GetTickCount64() + cacheDurationMs);`.

(`SessionContext.Session.Config` returns `ConfigBase`, which now has `.Time` — verify the chain compiles. If `Session.Config` is typed weakly, may need a small cast; resolve during implementation.)

**Test migration:** `AuthCache_AttributeTtl_ExpiredEntry_ReChecks` (locate in `src/NexNet.IntegrationTests`) — find the `TickCountOverride = () => ...` assignment, replace with construction of a `FakeTimeProvider`, set it on the `ServerConfig.Time`, and call `fakeTime.Advance(TimeSpan.FromSeconds(N))` instead of bumping the override value. This is the first test in the suite that exercises the new path end-to-end.

**Tests:** The migrated test must pass deterministically. Re-run the full integration suite to confirm no other test depended on the override (grep `TickCountOverride` in `src/NexNet.IntegrationTests` first to verify singleton usage).

**Commit:** `Migrate ServerNexusBase auth cache to TimeProvider and remove TickCountOverride seam`

---

## Phase 8 — `NexusPipeReader`

**File:** `src/NexNet/Pipes/NexusPipeReader.cs`.

Site (line 109): the back-pressure retry loop using `Task.Delay(int)` with no cancellation token.

`NexusPipeReader` does not currently hold a `ConfigBase` reference — it holds `_stateManager`. Verify during implementation whether it has access to the session/config chain. Two options:
- **(a)** Add an `INexusSession` (or `TimeProvider`) parameter to `NexusPipeReader` ctor. Plumb from whatever constructs it.
- **(b)** Reach through an existing reference (e.g. `_stateManager._session._config.Time`) if such a chain already exists.

Implementation step: re-read the `NexusPipeReader` constructor and its callers (`grep "new NexusPipeReader\("`) to pick the lower-touch wiring. Default recommendation: **(a)** with a `TimeProvider time` ctor parameter, since the existing back-pressure delay is a load-bearing site for test determinism.

Replace:
```csharp
await Task.Delay(loopCount < 2 ? 1 : loopCount < 10 ? 5 : loopCount < 50 ? 10 : 100).ConfigureAwait(false);
```
with:
```csharp
var delayMs = loopCount < 2 ? 1 : loopCount < 10 ? 5 : loopCount < 50 ? 10 : 100;
await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _time).ConfigureAwait(false);
```

**Tests:** Pipe back-pressure tests stay green.

**Commit:** `Route NexusPipeReader back-pressure delay through TimeProvider`

---

## Phase 9 — `NexusListRelay` via `NexusCollectionManager`

**Files:** `src/NexNet/Collections/Lists/NexusListRelay.cs`, `src/NexNet/Invocation/NexusCollectionManager.cs`, plus the two construction sites (`NexusClient.cs:89`, `NexusServer.cs:123`).

- `NexusCollectionManager` ctor: add `TimeProvider time` param after `isServer`. Store as `_time`.
- `NexusCollectionManager.ConfigureList<T>`: pass `_time` into both `NexusListRelay<T>` (added param) and — if symmetric wiring is wanted — into `NexusListServer<T>` / `NexusListClient<T>`. **Scope check:** the issue only calls out `NexusListRelay` for `Task.Delay`. Grep `Task\.Delay\|new Timer\|Environment\.TickCount64\|DateTime\.` in `NexusListServer` / `NexusListClient` / `NexusBroadcastServer` to see whether they need wiring too. If not, plumb the `TimeProvider` only into `NexusListRelay` and document the asymmetry in a one-line code comment.
- `NexusListRelay<T>` ctor: add `TimeProvider time` param. Store as `_time`.
- Line 150: `await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);` → `await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct).ConfigureAwait(false);`.
- Call sites:
  - `NexusClient.cs:89`: `new NexusCollectionManager(_logger, false)` → `new NexusCollectionManager(_logger, false, _config.Time)`.
  - `NexusServer.cs:123`: `new NexusCollectionManager(_logger, true)` → `new NexusCollectionManager(_logger, true, _config.Time)`.

**Tests:** Relay reconnection tests stay green.

**Commit:** `Thread TimeProvider through NexusCollectionManager into NexusListRelay`

---

## Phase 10 — `SocketTransport`

**File:** `src/NexNet/Transports/SocketTransport.cs`.

Site (line 96): `await Task.Delay(clientConfig.ConnectionTimeout, timeoutCancellation.Token).ConfigureAwait(false);` — `clientConfig` is in scope, so replace with `await Task.Delay(clientConfig.ConnectionTimeout, clientConfig.Time, timeoutCancellation.Token).ConfigureAwait(false);` (verify `ConnectionTimeout` is already a `TimeSpan`; if it's an `int ms`, wrap in `TimeSpan.FromMilliseconds`).

**Tests:** Existing socket connect-timeout tests stay green.

**Commit:** `Route SocketTransport connect timeout through TimeProvider`

---

## Phase 11 — `WebSocketPipe`

**File:** `src/NexNet/Transports/WebSocket/WebSocketPipe.cs`.

Site (line 95): `await Task.WhenAny(closeTask, Task.Delay(closeTimeout)).ConfigureAwait(false);` — replace with `await Task.WhenAny(closeTask, Task.Delay(closeTimeout, _config.Time)).ConfigureAwait(false);` (verify `_config` field exists and exposes `.Time` per design decision).

**Tests:** WebSocket close-timeout tests stay green.

**Commit:** `Route WebSocketPipe close timeout through TimeProvider`

---

## Phase 12 — Delete dead `RegisteredInvocationState.Created` field

**Files:** `src/NexNet/Internals/RegisteredInvocationState.cs`, `src/NexNet/Invocation/SessionInvocationStateManager.cs`.

Grep confirmation already done: the field is only written at `SessionInvocationStateManager.cs:147` and never read.

- Delete the field declaration on `RegisteredInvocationState`.
- Delete the assignment at `SessionInvocationStateManager.cs:147`.
- If `RegisteredInvocationState.Reset()` (IResettable implementation) zeroes the field, remove that line as well.

This phase is **not** a `TimeProvider` migration — it's the dead-code cleanup decision recorded in DESIGN. Kept as a separate atomic commit.

**Tests:** Full integration suite remains green.

**Commit:** `Remove dead RegisteredInvocationState.Created field`

---

## Phase 13 — Test framework integration

**Files:** `src/NexNet.IntegrationTests/NexNet.IntegrationTests.csproj` (add PackageReference), new test files for FakeTimeProvider coverage.

- Add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="..." />` to `NexNet.IntegrationTests.csproj`. (NexNet core does **not** add this dependency.)
- New tests:
  1. **Auth cache TTL expiry under FakeTimeProvider** — variation of the migrated `AuthCache_AttributeTtl_ExpiredEntry_ReChecks` covering (a) hit before expiry, (b) miss after `fakeTime.Advance(ttl + 1)`, (c) second-call re-cache.
  2. **Reconnection backoff under FakeTimeProvider** — connect, fault the transport, assert that `NexusClient` does not retry until `fakeTime.Advance(backoffDelay)`.
  3. **Ping behavior under FakeTimeProvider** — connect, advance time by < ping interval (no ping), advance to ≥ ping interval (ping fires).
  4. **Optional:** `NexusClientPool` idle reaping — advance > MaxIdleTime, assert pooled client is disposed.

Use existing test infrastructure (test transports, test nexus factories). Each new test injects `FakeTimeProvider` via `ServerConfig.Time` / `ClientConfig.Time`.

**Tests added:** ~4–5. All must pass deterministically (no real-time waits in any new test).

**Commit:** `Add FakeTimeProvider-driven tests for auth cache, reconnect, ping, and pool reaping`

---

## Per-phase test discipline

Before committing each phase:
1. Run `dotnet test src/NexNet.IntegrationTests -c Release` — must match the 2628-passing baseline (or 2629+ once phase 13 adds tests).
2. Run `dotnet test src/NexNet.Generator.Tests -c Release` — must stay at 149.
3. `_sessions/75-adopt-timeprovider/workflow.md` `phases-complete` increments; commit message references the phase.

## Open implementation questions (resolve during phase execution)

- **Phase 5** — whether `_lastUsed` becomes `DateTimeOffset` or stays `DateTime` with `.UtcDateTime` extraction.
- **Phase 7** — whether `SessionContext.Session.Config` is strongly enough typed to expose `.Time` without a cast.
- **Phase 8** — `NexusPipeReader` constructor wiring (direct `TimeProvider` param vs. session-chain access).
- **Phase 9** — whether `NexusListServer<T>` / `NexusListClient<T>` / `NexusBroadcastServer` also need `TimeProvider` (defer until grepped during phase implementation).
