# FakeTimeProvider test opportunities

## Summary
- Migration candidates: 9 (high-value: 4, medium: 4, low: 1)
- New test opportunities: 7
- Inherent-real-time tests (skip): 6 (network/handshake roundtrips, listed in Category C)

The codebase already has 5 migrated tests covering `Pool` MaxIdleTime, `AuthCache` TTL (3 cases), `ConnectionRateLimiter` PerIpWindow + BanDuration. The remaining sites with significant real-time waits are concentrated in `NexusClientPoolTests.cs` and `NexusClientTests.cs` (PingInterval/Timeout reconnect-on-timeout). Several config-validation tests in `SecurityTests.cs` are explicitly out of scope.

## Category A: Migration candidates

### A1. `Pool_DisposesIdleClientsAfterTimeout` (NexusClientPoolTests.cs:388)
- **What it tests:** Pool with `MaxIdleTime = 100ms` evicts idle clients after the health-check timer fires.
- **Current wait:** `await Task.Delay(TimeSpan.FromMilliseconds(500))` after `MaxIdleTime = 100`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59` health-check timer (`_config.ClientConfig.Time.CreateTimer`).
- **Migration sketch:** Mirror `Pool_FakeTimeProvider_DisposesIdleClientsDeterministically` (line 344) — set `clientConfig.Time = fakeTime`, then `fakeTime.Advance(TimeSpan.FromMilliseconds(500))` after disposing rented clients.
- **Value:** High (eliminates a flaky-by-design 500ms wait that asserts on a non-deterministic count `< 3`).
- **Risk:** None — already proven via A's sibling migrated test.

### A2. `Pool_KeepsAtLeastOneClientDespiteIdleTimeout` (NexusClientPoolTests.cs:432)
- **What it tests:** `MinIdleConnections = 1` retains one client past idle expiration.
- **Current wait:** `await Task.Delay(TimeSpan.FromMilliseconds(200))` after `MaxIdleTime = 50, MinIdleConnections = 1`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59` (health-check timer + idle-clock).
- **Migration sketch:** Set `clientConfig.Time = fakeTime`, then `fakeTime.Advance(TimeSpan.FromMilliseconds(200))`.
- **Value:** High (deterministic, tests an important boundary).
- **Risk:** None.

### A3. `Pool_RespectsMinIdleConnections` (NexusClientPoolTests.cs:469)
- **What it tests:** `MinIdleConnections = 3` keeps at least 3 idle clients during eviction.
- **Current wait:** `Task.Delay(200)` after `MaxIdleTime = 100`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59`.
- **Migration sketch:** Inject `fakeTime` then `Advance(200ms)`.
- **Value:** High.
- **Risk:** None.

### A4. `Pool_MinIdleConnectionsWithZeroValue` (NexusClientPoolTests.cs:567)
- **What it tests:** `MinIdleConnections = 0` allows all clients to be evicted.
- **Current wait:** `Task.Delay(500)` after `MaxIdleTime = 100`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59`.
- **Migration sketch:** Already a near-duplicate of the existing migrated test — fold into a parameterized variant, or just replace the `Task.Delay` with `fakeTime.Advance`.
- **Value:** Medium (very similar coverage to existing migrated test).
- **Risk:** None.

### A5. `Pool_MaxIdleTimeConfiguration_ZeroValue` (NexusClientPoolTests.cs:1207)
- **What it tests:** `MaxIdleTime = TimeSpan.Zero` immediately evicts clients on the next health-check pass.
- **Current wait:** `Task.Delay(200)`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59`.
- **Migration sketch:** Drive with `fakeTime.Advance(...)` of the health-check interval.
- **Value:** Medium (edge-case boundary).
- **Risk:** Health-check interval is derived from `MaxIdleTime / 4`; with zero, need to confirm minimum interval. Verify by reading `NexusClientPool.cs` around line 50–70 before migrating.

### A6. `Pool_HealthCheckTimer_ContinuesAfterExceptions` (NexusClientPoolTests.cs:1471)
- **What it tests:** Health-check timer continues firing across multiple cycles.
- **Current wait:** `Task.Delay(300)` after `MaxIdleTime = 50`.
- **TimeProvider site exercised:** `NexusClientPool.cs:59` (timer-callback re-arm).
- **Migration sketch:** `fakeTime.Advance(...)` in multiple steps to assert the timer keeps firing.
- **Value:** Medium.
- **Risk:** None.

### A7. `Pool_MinIdleConnectionsDoesNotCreateNewClients` (NexusClientPoolTests.cs:519)
- **What it tests:** `MinIdleConnections = 5` with only 2 existing clients does NOT pre-warm extras.
- **Current wait:** `Task.Delay(200)` (waiting for "no work to happen").
- **TimeProvider site exercised:** `NexusClientPool.cs:59` (health-check timer firing without creating clients).
- **Migration sketch:** Inject fakeTime, `Advance(200ms)` to trigger health-check cycles, assert count unchanged.
- **Value:** Medium (negative assertion — fast wins on test latency).
- **Risk:** None.

### A8. `AuthCache_AttributeTtl_SecondCallUsesCachedResult` (NexusServerTests_Authorization.cs:537)
- **What it tests:** Within TTL, second auth call returns cached result.
- **Current wait:** None — the test as written has no Task.Delay. It currently relies on "within ~ms, TTL has not expired."
- **TimeProvider site exercised:** `ServerNexusBase.cs:129,159` (auth-cache TTL via `Config.Time.GetTickCount64()`).
- **Migration sketch:** Optional — adding `fakeTime` would make it more robust against TTL = small values. Low priority but trivially safe.
- **Value:** Low (test already deterministic enough).
- **Risk:** None.

### A9. `Server_ReleasesSlotOnDisconnect` / `Server_MultipleDisconnects_ReleasesAllSlots` / `AspServer_*` siblings (ConnectionRateLimitingIntegrationTests.cs:84,294,435,543)
- **What it tests:** Connection counter decrements after disconnect; new connections then succeed.
- **Current wait:** `Task.Delay(100|200)` — comment says "wait for disconnect to propagate to server."
- **TimeProvider site exercised:** None directly — this is waiting on the server-side `OnDisconnected` callback to fire after the wire close.
- **Migration sketch:** **Not driveable by FakeTimeProvider** — the wait is for OS/network teardown to surface as an `OnDisconnected` event, not a timer.
- **Value:** N/A — actually a Category C entry. Listed here only to call out the trap. **Skip.**
- **Risk:** High — would silently still wait real time.

## Category B: New test opportunities

### B1. `Client_ReconnectDelay_DrivenByFakeTime`
- **Site exercised:** `NexusClient.cs:236` — `Task.Delay(delay.Value, _config.Time)` in the reconnect loop.
- **Sketch:** Construct a `DefaultReconnectionPolicy` with a multi-second delay (e.g. `new[] { TimeSpan.FromSeconds(10) }`). Connect, force the server to drop the client, assert reconnect attempt has NOT fired after small real wait, then `fakeTime.Advance(10s)` and assert `OnConnectedEvent` fires with `isReconnected = true`. Today this can't reasonably be tested at high delays without bloating test time.
- **Value:** High — directly validates the just-refactored reconnect-backoff site, currently zero coverage of long-delay behavior.
- **Difficulty:** Medium — needs careful sequencing of disconnect detection vs. timer advancement; the reconnect loop awaits the policy delay synchronously on the client thread.

### B2. `Client_PingTimer_FiresAtConfiguredInterval`
- **Site exercised:** `NexusClient.cs:187,302` — `_pingTimer = _config.Time.CreateTimer(PingTimer, ...)` + `PingTimer` reading `_config.Time.GetTickCount64()`.
- **Sketch:** Set `PingInterval = 1000` and `clientConfig.Time = fakeTime`. Hook `FireOnSend` (see `NexusClientTests.cs:175 ClientSendsPing` for the pattern). Connect, assert no ping yet, `fakeTime.Advance(1s)`, assert exactly one ping observed; `Advance(1s)` again, assert second ping observed.
- **Value:** High — gives precise control over the ping cadence, today only tested by setting `PingInterval = 20ms` (`ClientSendsPing` at line 175) which only verifies "fires at least once."
- **Difficulty:** Low — pattern of `FireOnSend` already exists.

### B3. `Client_TimeoutTicks_DisconnectFires_OnFakeTimeAdvance`
- **Site exercised:** `NexusClient.cs:304` (`timeoutTicks = _config.Time.GetTickCount64() - _config.Timeout`) + `NexusSession.Receiving.cs:29` (`LastReceived = Config.Time.GetTickCount64()`).
- **Sketch:** Set both `clientConfig.Time = fakeTime` AND `serverConfig.Time = fakeTime` (need both, since LastReceived is server-side too, depending on direction). Set `Timeout = 5000, PingInterval = 1000`. Connect, hold the connection idle, advance `fakeTime` past timeout, assert disconnection.
- **Value:** High — exercises a coordinated client+server time decision currently covered only by `ReconnectsOnTimeout` (NexusClientTests.cs:279) at 50ms timeout.
- **Difficulty:** Medium — must use the same `fakeTime` for both sides AND prevent real ping from succeeding; check that ping write path doesn't reset `LastReceived` if you're advancing past timeout.

### B4. `Server_ConnectionWatchdog_DisconnectsTimedOutSessions`
- **Site exercised:** `NexusServer.cs:114,402,404` — watchdog timer firing every `Timeout/4`, computing `timeoutTicks = Time.GetTickCount64() - Timeout`.
- **Sketch:** Set `serverConfig.Time = fakeTime` AND `serverConfig.Timeout = 4000` (watchdog interval = 1s). Connect a raw TCP client that ignores pings (use `RawTcpClient` helper from Security tests). Advance `fakeTime` by 4s, assert server disconnects the client.
- **Value:** High — watchdog has zero direct test coverage today; `ServerDisconnectsOnNoData` (NexusServerTests.cs:401) covers handshake timeout but not the post-handshake watchdog.
- **Difficulty:** Medium — requires either a `RawTcpClient`-style harness or a client whose `Time` is *not* faked (so it sends no pings, since `PingTimer` won't fire on a frozen fakeTime).

### B5. `Server_DisconnectDelay_DriversByFakeTime`
- **Site exercised:** `NexusSession.cs:311` — `Task.Delay(DisconnectDelay, _config.Time)`.
- **Sketch:** Set `serverConfig.Time = fakeTime` AND `serverConfig.DisconnectDelay = 5000`. Trigger a server-initiated disconnect (e.g. unauthorized auth). Assert disconnect has NOT propagated to wire yet, `fakeTime.Advance(5s)`, assert it now has.
- **Value:** Medium — `DisconnectDelay` has no targeted test coverage; only validated by config-range tests in SecurityTests.cs:106–119.
- **Difficulty:** Medium — measuring "disconnect not yet on wire" requires either a `RawTcpClient` or hooking `InternalOnReceive`.

### B6. `WebSocketPipe_CloseTimeout_DriverByFakeTime`
- **Site exercised:** `WebSocketPipe.cs:95` — `Task.WhenAny(closeTask, Task.Delay(closeTimeout, _config.Time))`.
- **Sketch:** Construct a WebSocket transport where the peer never acknowledges the close handshake (e.g. force-kill the underlying stream). Set `serverConfig.Time = fakeTime`, then `Advance(closeTimeout)`, assert local close completes.
- **Value:** Low-Medium — narrow, only matters under abusive shutdown sequences.
- **Difficulty:** High — engineering the "peer that won't ACK close" scenario is non-trivial; may need protocol-level surgery in `RawTcpClient` equivalent for WebSocket.

### B7. `Relay_ReconnectAfter500ms_DriverByFakeTime`
- **Site exercised:** `NexusListRelay.cs:153` — `Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct)` between reconnect attempts. `_time` is plumbed from `ServerConfig.Time` via `NexusCollectionManager`.
- **Sketch:** Use `CreateRelayCollectionClientServers` (NexusCollectionBaseTests.cs:29) but set `serverConfig2.Time = fakeTime` before constructing Server2. Stop Server1, then restart it. Assert relay reconnect has NOT happened yet, `fakeTime.Advance(500ms)`, assert relay reconnects.
- **Value:** Medium — `RelayReconnectsUponOtherServerDisconnection` (NexusCollectionRelayTests.cs:159) exists but waits real-time and doesn't pin down the 500ms boundary.
- **Difficulty:** Medium — requires modifying the helper `CreateRelayCollectionClientServers` to accept a `TimeProvider` override, or duplicating its setup inline.

## Category C: Explicitly skipped

- **`SecurityTests.cs:39–119`** — pure `Assert.Throws<ArgumentOutOfRangeException>` validation. No time math.
- **`ConnectionRateLimitingIntegrationTests.cs:84,294,435,543` (Server_ReleasesSlotOnDisconnect & siblings)** — `Task.Delay(100|200)` waits for OS-level disconnect propagation, not a TimeProvider timer.
- **`NexusClientTests.cs:202 ReconnectsOnDisconnect`** — `Task.Delay(100)` after `server.StopAsync()` waits for client-side socket close detection (network teardown), not a configured delay.
- **`NexusClientTests.cs:365 ReconnectsStopsAfterSpecifiedTimes`** — `Task.Delay(100)` is "let connection settle"; the actual reconnect timing is driven by `ReconnectionPolicy` which uses `_config.Time`, but the wait here is for connection establishment, not a NexNet timer.
- **`NexusServerTests.cs:401 ServerDisconnectsOnNoData`** — already deliberately uses real network for `HandshakeTimeout = 100ms`. Could in principle migrate via faking `serverConfig.Time` then `Advance(100ms)`, but the wait `Utilities.WaitForConnectionClosureAsync` polls the wire; could be added as a secondary case in B4. **Not** a pure skip — see priority section below.
- **`Security/RateLimitingTests.cs:190 GreetingRateLimit_NewConnection_CounterResets`** — `Task.Delay(100)` waits for server-side cleanup after a raw TCP `ForceDisconnect`. Not timer-driven.
- **`NexusServerTests.cs:448 ConcurrentConnections_UniqueSessionIds`** — `Task.Delay(100|500)` is network-roundtrip slack for 20 parallel connects.

## Suggested priority order

Ranked by value / difficulty:

1. **A1 `Pool_DisposesIdleClientsAfterTimeout`** — high value, trivial, kills the most flaky-prone Task.Delay(500). [first]
2. **A2 `Pool_KeepsAtLeastOneClientDespiteIdleTimeout`** — trivial migration, asserts important MinIdleConnections=1 invariant.
3. **A3 `Pool_RespectsMinIdleConnections`** — trivial, high value (asserts MinIdleConnections=3 retention).
4. **A7 `Pool_MinIdleConnectionsDoesNotCreateNewClients`** — trivial, easy guarantee.
5. **B2 `Client_PingTimer_FiresAtConfiguredInterval`** — low-difficulty new test; gives high-precision ping coverage we don't currently have.
6. **A6 `Pool_HealthCheckTimer_ContinuesAfterExceptions`** — medium effort, validates re-arm behavior.
7. **B7 `Relay_ReconnectAfter500ms_DriverByFakeTime`** — needs helper plumbing but covers the only `_time`-driven NexusListRelay site.
8. **B1 `Client_ReconnectDelay_DrivenByFakeTime`** — covers the reconnect backoff at delays we can't test today.
9. **B4 `Server_ConnectionWatchdog_DisconnectsTimedOutSessions`** — currently zero coverage of `NexusServer.cs:402`.
10. **A4 `Pool_MinIdleConnectionsWithZeroValue`** — low marginal value (overlaps the existing migrated test).
11. **A5 `Pool_MaxIdleTimeConfiguration_ZeroValue`** — verify health-check interval math first.
12. **B3 `Client_TimeoutTicks_DisconnectFires_OnFakeTimeAdvance`** — coordinated client+server fakeTime, more fragile.
13. **B5 `Server_DisconnectDelay_DriversByFakeTime`** — narrow site, requires raw-TCP harness.
14. **B6 `WebSocketPipe_CloseTimeout_DriverByFakeTime`** — last; engineering effort high vs. payoff.
15. **A8 `AuthCache_AttributeTtl_SecondCallUsesCachedResult`** — only do if the rest is done.

Quick wins (do them as one batch): A1, A2, A3, A7. Together these remove ~1.2s of real-time waits and 4 non-deterministic assertions.
