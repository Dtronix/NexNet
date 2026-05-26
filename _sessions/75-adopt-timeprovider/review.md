# Review: TimeProvider adoption (issue #75)

Scope reviewed: `git diff 8844df2..HEAD -- src/` (23 files, +597/-71 lines, 15 commits). Plan and decisions log read in full. Three deviations on 2026-05-22 are acknowledged as documented and are **not** re-flagged below.

## Classifications

| # | Class | Rec | Sev | Section | Finding | Action Taken |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | A (C→A) | C | Medium | Plan Compliance | Phase 13 shipped 2/4 planned FakeTimeProvider tests (auth cache only; reconnect/ping/pool dropped) |  |
| 2 | D | D | Low | Plan Compliance | PooledClient kept DateTime instead of DateTimeOffset (Phase 5 open-question recommendation) |  |
| 3 | B | B | Low | Plan Compliance | NexusPipeReader TimeProvider param optional vs ConnectionRateLimiter required; not in Decisions block |  |
| 4 | A (C→A) | C | Low | Plan Compliance | Issue #75 lists MutexSlim and CoreLogger — skipped intentionally but no PR/issue note closes the loop |  |
| 5 | D | D | Low | Plan Compliance | Phase 12 Reset() check — no extra change needed (verification) |  |
| 6 | A | A | Medium | Correctness | PooledClient captures _time at construction while health-check reads Config.Time dynamically — race if Time is mutated post-ctor |  |
| 7 | A | A | Medium | Correctness | NexusClient._pingTimer constructed against ctor-time _config.Time, reconnect Task.Delay reads it dynamically — same race |  |
| 8 | D | D | Low | Correctness | _watchdogTimer deferral lifecycle verified safe |  |
| 9 | D | D | Low | Correctness | GetTickCount64 integer math verified correct for System and FakeTimeProvider |  |
| 10 | D | D | Low | Correctness | All Task.Delay(int) → TimeSpan conversions verified equivalent |  |
| 11 | D | D | Low | Correctness | ServerNexusBase SessionContext null-safety verified |  |
| 12 | A | A | Low | Correctness | Stale doc comment on INexusSession.DisconnectIfTimeout still references Environment.TickCount64 |  |
| 13 | A (C→A) | C | Low | Correctness | _watchdogTimer not disposed — pre-existing oversight, out of branch scope |  |
| 14 | A (C→A) | C | Medium | Test Quality | Only auth-cache subsystem has FakeTimeProvider coverage; reconnect/ping/pool deferred (mirrors #1) |  |
| 15 | D | D | Low | Test Quality | New FakeTimeProvider tests are Uds-only — matches pre-existing pattern |  |
| 16 | D | D | Low | Test Quality | MultipleExpiryCycles cycle-1 assertion awkward but correct |  |
| 17 | A (C→A) | C | Low | Test Quality | ConnectionRateLimiter still has no FakeTimeProvider-driven tests; the ctor change unblocks them |  |
| 18 | B | B | Low | Codebase Consistency | NexusPipeReader optional/RateLimiter required asymmetry needs an entry in the Decisions block |  |
| 19 | A | A | Low | Codebase Consistency | ConfigBase.Time XML doc doesn't mention mutation semantics from the init→set deviation |  |
| 20 | D | D | Low | Codebase Consistency | TimeProvider _time field naming consistent across migration (verification) |  |
| 21 | D | D | Low | Codebase Consistency | Timer creation idiom consistent — Time.CreateTimer + Change pattern (verification) |  |
| 22 | D | D | Low | Codebase Consistency | NexusServerTests_Authorization uses fully qualified FakeTimeProvider — cosmetic |  |
| 23 | D | D | Low | Integration | ConnectionRateLimiter ctor signature change correctly handled at all 14 sites |  |
| 24 | D | D | Low | Integration | NexusCollectionManager ctor signature change correctly threaded |  |
| 25 | D | D | Low | Integration | NexusListRelay ctor signature change correctly threaded |  |
| 26 | D | D | Low | Integration | Timer → ITimer field type changes are all private; no public-surface break |  |
| 27 | D | D | Low | Integration | New ConfigBase.Time property is purely additive with safe default |  |
| 28 | D | D | Low | Integration | Microsoft.Extensions.TimeProvider.Testing added only to NexNet.IntegrationTests; core unchanged |  |
| 29 | D | D | Low | Integration | RegisteredInvocationState.Created deletion verified fully internal |  |

## Plan Compliance

| Finding | Severity | Why It Matters |
| --- | --- | --- |
| Phase 13 plan listed 4 candidate FakeTimeProvider tests (auth cache, reconnect backoff, ping, optional pool reaping). Implementation shipped only the auth-cache flavour (one migrated + two new), 2 net new. Reconnect-backoff and ping coverage — both called out as primary value drivers in the original issue — were silently dropped. | Medium | Reduces the demonstrated payoff of the entire branch: the issue framed deterministic ping/reconnect tests as the motivating use case. No workflow.md decision records this scope cut. |
| Plan open question #5 (Phase 5) recommended switching `PooledClient._lastUsed` to `DateTimeOffset`. Implementation kept `DateTime` and uses `.UtcDateTime` on every read/write. Choice is defensible but not recorded as a decision. | Low | Minor undocumented deviation from the plan's stated recommendation. |
| Plan open question #8 (Phase 8) recommended a required `TimeProvider time` ctor parameter on `NexusPipeReader`. Implementation added an *optional* `TimeProvider? time = null` defaulting to `TimeProvider.System` "for test ergonomics" (recorded in workflow.md session-log line but not in the Decisions section). | Low | Asymmetric with `ConnectionRateLimiter` (required ctor param). The decision lives in the session log entry, not the Decisions block, so it's harder to find later. See also the Consistency table. |
| Issue #75 listed `MutexSlim` and `CoreLogger.Stopwatch` as refactor sites. Workflow.md decisions explicitly skip both for sound reasons (load-bearing primitive / cosmetic diagnostic), but no follow-up issue exists to formally close those out of #75's scope. | Low | When #75 is closed, the issue body still lists sites that won't be addressed. A short note on the PR or a tracking comment on #75 would close the loop. |
| Phase 12 — `RegisteredInvocationState.Created` was deleted as planned. Plan flagged "if `Reset()` zeroes the field, remove that line as well." Verified: `Reset()` did not reference `Created`, so no extra change was required. | Low | Verification finding — no action needed; called out for completeness. |

## Correctness

| Finding | Severity | Why It Matters |
| --- | --- | --- |
| `NexusClientPool.PooledClient` captures `_time` (readonly) at construction, while `NexusClientPool.PerformHealthAndIdleCheck` reads `_config.ClientConfig.Time` on every tick. Because `ConfigBase.Time` is now `{ get; set; }` (deviation), if a caller mutates `Config.Time` between pool construction and the first health-check tick, `now` comes from the new provider while `_lastUsed` on already-pooled clients comes from the old provider. `now - _lastUsed` (line 186 of `NexusClientPool.cs`) will then produce garbage `TimeSpan` values (e.g. multi-year deltas), causing immediate eviction of every pooled client, or vice-versa, preventing eviction indefinitely. | Medium | This is a real pitfall created specifically by switching `Time` from `init` to `set`. The deviation note in workflow.md says "timers/delays already in flight retain the original provider" but does not cover stored timestamps. Either re-read `Config.Time` consistently in `PooledClient`, or document that callers must not change `Time` after the pool is constructed. |
| `NexusClient._pingTimer` is created in the constructor using whatever `_config.Time` is at that moment, but the reconnect-loop `Task.Delay` (line 233) reads `_config.Time` dynamically on each call. If the user mutates `Config.Time` between `new NexusClient(...)` and `ConnectAsync(...)`, the ping timer fires under the *original* provider while reconnect delays use the *new* provider. Pre-existing tests do not hit this (the auth-cache test mutates `server.Server.Config.Time` after `CreateAuthServerClient` and only relies on the auth cache, which is also lazy). | Medium | Same root cause as the previous finding. Documented "in-flight retains original" semantic does not cleanly cover timers that are constructed early and reused throughout a client's lifetime. Either move `_pingTimer` construction to `TryConnectAsync` (as the original DESIGN note suggested but the implementation did not adopt) or document the invariant. |
| `_watchdogTimer` deferral to `Configure()` is correct: `StartAsync` checks `_config != null` (line 157), so `_watchdogTimer!` cannot be null there; `StopAsync` uses `_watchdogTimer?.Change(...)` defensively; `Configure` has a re-call guard. No regression from the deferral itself. | Low | Verification finding — no action needed. |
| `GetTickCount64()` extension uses integer arithmetic `(time.GetTimestamp() * 1000L) / time.TimestampFrequency`. For `TimeProvider.System` this is Stopwatch-backed (10MHz → ~29 year wraparound as documented). For `FakeTimeProvider`, `GetTimestamp()` uses `TimeSpan.Ticks` (10MHz, 100ns ticks) — math is consistent. No off-by-one. | Low | Verification finding — no concerns. |
| `Task.Delay(int, ct)` → `Task.Delay(TimeSpan.FromMilliseconds(int), TimeProvider, ct)` conversions reviewed at all sites: `NexusSession.cs:311` (DisconnectDelay int ms), `NexusSession.Receiving.cs:17` (HandshakeTimeout int ms), `NexusClient.cs:233` (delay.Value already TimeSpan), `SocketTransport.cs:96` (ConnectionTimeout int ms), `NexusClient.cs Change` calls (PingInterval int ms), `NexusServer.cs:197` (_config.Timeout/4 int ms), `WebSocketPipe.cs:95` (closeTimeout already TimeSpan), `NexusListRelay.cs:150` (literal TimeSpan), `NexusPipeReader.cs:119` (int → TimeSpan.FromMilliseconds). All semantically equivalent. | Low | Verification finding — no concerns. |
| `ServerNexusBase.CheckAuthorization` now dereferences `SessionContext.Session.Config.Time` twice (lines 129, 159). `SessionContext` throws "Session context is null" (NexusBase.cs:33) rather than returning null, and the auth path is only invoked from generated method-invocation code that always sets context first. No NRE risk introduced. | Low | Verification finding — no concerns. |
| Stale doc comment: `INexusSession.DisconnectIfTimeout(long timeoutTicks)` (`src/NexNet/Internals/INexusSession.cs:85`) still says "Normally use Environment.TickCount64 - Timeout". After the refactor, both callers (`NexusClient.PingTimer`, `NexusServer.ConnectionWatchdog`) now derive the value from `_config.Time.GetTickCount64()`. The comment is misleading. | Low | Cosmetic but easy to fix; will mislead future contributors integrating new callers. |
| `NexusServer._watchdogTimer` has never been disposed — the original code also did not dispose it (verified via `git show 8844df2`). The migration preserves this pre-existing oversight rather than fixing it. | Low | Not a regression. Worth a follow-up but explicitly out of this branch's scope. |

## Security

No concerns.

`ConfigBase.Time` deviation from `init` to `set` widens what callers can mutate post-construction, but `ConfigBase` is already the canonical mutable configuration surface (every other property is `get; set;`), and there is no security boundary crossed — the time source is local to the server/client instance and not exposed cross-process or cross-tenant. The `FakeTimeProvider` package is a test-only `PackageReference` on `NexNet.IntegrationTests`; the production `NexNet` core has no new dependencies (verified by checking the diff for `.csproj` changes — only the test project was modified).

## Test Quality

| Finding | Severity | Why It Matters |
| --- | --- | --- |
| Only the auth-cache subsystem received deterministic `FakeTimeProvider` coverage (1 migrated + 2 new). Reconnect-backoff, ping-interval, and pool-reaping tests — all listed as Phase 13 deliverables in plan.md — were not implemented. The integration test count went 2628 → 2630 (+2) when the plan said "~4-5". | Medium | Reduces confidence that the new `TimeProvider`-routed sites for ping, reconnect, rate limiter sliding window, and pool reaping actually behave deterministically with `FakeTimeProvider` injection. The two added auth-cache tests duplicate coverage of the same one site that already worked under the old `TickCountOverride` seam. |
| The three FakeTimeProvider tests (1 migrated + 2 new) all use the `Uds` transport only. `[TestCase(Type.Uds)]` — no Tcp/TcpTls/WebSocket coverage. The pre-existing pattern in `NexusServerTests_Authorization.cs` does this too for some tests, but the cross-transport variant of `AuthCache_AttributeTtl_ExpiredEntry_ReChecks` (if any other transports were previously asserted to work) silently lost coverage in the migration. | Low | Worth verifying the original test only ran Uds. If so, no regression; if not, the migration narrowed scope. |
| `AuthCache_FakeTimeProvider_MultipleExpiryCycles_ReChecksEachTime` cycles 3 times but the cycle-1 assertion (`Is.EqualTo(cycle)`) is satisfied before any `Advance` happens, because the first iteration's two calls produce `authCallCount == 1` from the initial OnAuthorize. Logic is correct but slightly awkward — the first cycle proves "cache hit within TTL" without ever exercising expiry; only cycles 2 and 3 actually validate the cross-expiry behaviour. | Low | Test is correct; just notable that the loop-counter assertion conflates "initial call" and "post-Advance call." |
| All `ConnectionRateLimiter` unit tests now pass `TimeProvider.System` rather than `FakeTimeProvider`. The sliding-window logic (`PerIpWindowSeconds`, `ConnectionsPerIpPerWindow`) remains untestable deterministically. The ctor change unblocked future tests but no new tests exploit the new injection point. | Low | Existing limitation, not a regression. Future work — likely a separate issue. |

## Codebase Consistency

| Finding | Severity | Why It Matters |
| --- | --- | --- |
| `NexusPipeReader` defaults its `TimeProvider? time` parameter to `null` (→ `TimeProvider.System`), while `ConnectionRateLimiter` requires `TimeProvider time` with no default. Both are internal-only, both are constructed by a single production path that passes `_config.Time`. The asymmetry exists purely so existing test code in `NexusPipeReader*Tests.cs` and `NexusChannelReader*Tests.cs` (30+ call sites) doesn't have to be updated. | Low | Defensible (touch reduction) but the inconsistency makes the intent harder to follow. Either both internal helpers should require the parameter (and tests updated) or both should default — `ConnectionRateLimiter` could also have defaulted given its tests already needed updates. The session log mentions this as a deviation, but the Decisions block does not. |
| The `Time` property on `ConfigBase` is the only public `set` introduced. Documenting in the property's XML doc whether mid-connection mutation is supported (per the 2026-05-22 deviation: "supported but limited") would prevent the correctness pitfalls flagged above. Current doc says only "Defaults to TimeProvider.System. Tests can substitute..." | Low | Surface-level doc gap. The XML doc-summary is the contract users see in IntelliSense. |
| Field naming is consistent across the migration: `private readonly TimeProvider _time;` in `ConnectionRateLimiter`, `NexusPipeReader`, `NexusCollectionManager`, `NexusListRelay`, `PooledClient`. Good. | Low | Verification finding — no concerns. |
| Timer creation idiom is consistent: `_config.Time.CreateTimer(callback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)` followed by `.Change(...)` to activate. Used in `NexusClient`, `NexusServer`, `NexusClientPool`, `ConnectionRateLimiter`. Idiomatic with .NET 8 `TimeProvider` guidance. | Low | Verification finding — no concerns. |
| `using Microsoft.Extensions.Time.Testing;` is not added to `NexusServerTests_Authorization.cs`; the tests use the fully qualified `Microsoft.Extensions.Time.Testing.FakeTimeProvider`. Cosmetic. | Low | Stylistic; not worth blocking on. |

## Integration / Breaking Changes

| Finding | Severity | Why It Matters |
| --- | --- | --- |
| `ConnectionRateLimiter` ctor signature change (added required `TimeProvider time` parameter) is correctly handled at all call sites: 1 production site in `NexusServer.cs:150` and 13 test sites across `ConnectionRateLimiterUnitTests.cs` + `ConnectionRateLimitingIntegrationTests.cs`. The class is `internal sealed` — no public-surface break. | Low | Verification finding — no concerns. |
| `NexusCollectionManager` ctor signature change (added required `TimeProvider time` parameter) is correctly handled at 2 production call sites (`NexusClient.cs:89`, `NexusServer.cs:123`). No test sites in the diff reference it directly. The class is `internal` — no public-surface break. | Low | Verification finding — no concerns. |
| `NexusListRelay<T>` ctor signature change is correctly threaded through `NexusCollectionManager.ConfigureList<T>`. No external callers. | Low | Verification finding — no concerns. |
| Timer field type changes (`Timer` → `ITimer` / `ITimer?` in `NexusClient`, `NexusServer`, `NexusClientPool`, `ConnectionRateLimiter`) are all `private` fields. No public-surface break. The `ITimer` returned by `TimeProvider.System.CreateTimer(...)` is documented to wrap `System.Threading.Timer`, so default behavior is byte-equivalent. | Low | Verification finding — no concerns. |
| New public `ConfigBase.Time { get; set; }` property is purely additive. Default value `TimeProvider.System` guarantees behavioural compatibility for existing callers. Deviation to `set` (from planned `init`) widens mutation surface but does not break any existing pattern. | Low | Verification finding — no concerns. |
| `Microsoft.Extensions.TimeProvider.Testing` v9.10.0 was added to `NexNet.IntegrationTests.csproj` only. Production `NexNet` core has no new dependencies (verified by inspecting the diff — no changes to `src/NexNet/*.csproj`). | Low | Confirms the stated decision. No concerns. |
| `RegisteredInvocationState.Created` field deletion is fully internal; the field was only set in `SessionInvocationStateManager.cs:147` and never read. The only consumer was the (deleted) write site. Source generator output (`NexNet.Generator`) was not inspected here, but a grep across `src/` confirms no other references. | Low | Verification finding — no concerns. |
