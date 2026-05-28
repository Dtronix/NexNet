## Summary
- Ships a new `NexNet.Testing` package: in-process transport, test host, recorders, server-side and per-client assertion APIs, streaming-helper extensions, and a quiescence primitive.
- Adds three minimal optional internal hooks to `NexNet` core (`IInvocationInterceptor`, `IPipeFactory`, `ServerConfig.OnAuthenticateOverride`) so the harness can instrument dispatch and auth without touching production hot paths.
- Demo + showcase nexus rewritten in Phase 14 from the original `JoinGroup`/`BroadcastToGroup` passthrough shape to a realistic `EditorServerNexus` document-editor domain that drives every harness feature through natural business verbs.
- Driven by 14 plan phases + 12 remediation phases (R1–R12) addressing 40 findings from a structured review, plus a second full-branch review pass addressing 5 more findings.

## Reason for Change

End users building applications on NexNet need a way to test their nexus implementations — server methods, client callbacks, authorization rules, broadcasts, group routing, pipes, and channels — without standing up sockets, ports, TLS, or fake auth providers. Existing options either require real network plumbing or fork into custom test harnesses per project.

## Impact

New consumers of the package can write tests like:

```csharp
await using var host = await NexusTestHost.CreateAsync<
    EditorServerNexus, EditorServerNexus.ClientProxy,
    EditorClientNexus, EditorClientNexus.ServerProxy>();

var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
var bob   = await host.ConnectAsAsync(TestIdentity.Of("bob",   "Write"));
var carol = await host.ConnectAsAsync(TestIdentity.Of("carol", "Read"));

await alice.Server.OpenDocument("design.md");
await bob.Server.OpenDocument("design.md");
await carol.Server.OpenDocument("recipe.txt");

await alice.Server.SaveDraft("design.md", "v1");
await host.QuiesceAsync();

bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
carol.AssertNotReceived<IEditorClientNexus>(
    n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()));
Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(2));
```

The sample drives real business methods (`OpenDocument`, `SaveDraft`) instead of synthetic passthroughs — `SaveDraft` is `[NexusAuthorize<DocPermission>(Write)]`-gated and broadcasts `DraftSaved` to `Context.Clients.Group($"doc-{docId}")` internally. Tests observe the resulting client callbacks plus the recorded server-side invocation.

Existing NexNet consumers are unaffected: every new hook is `internal` (with InternalsVisibleTo for `NexNet.Testing`) and defaults to `null` so production sessions keep the unchanged dispatch path.

## Plan items implemented as specified

- **Phase 1** — `PendingInvocationCount` accessor on `ISessionInvocationStateManager`.
- **Phase 2** — `IInvocationInterceptor` interface + ConfigBase / NexusSessionConfigurations plumbing + Receiving wire-up.
- **Phase 3** — `IPipeFactory` interface + WrapLocal / WrapRemote hook points in `NexusPipeManager`.
- **Phase 4** — `ServerConfig.OnAuthenticateOverride` + `ServerNexusBase.Authenticate` consult-then-fallback.
- **Phase 5** — `NexNet.Testing` project with `InProcessTransport` (paired Pipes cross-wired), listener with Channel-based accept queue, `InProcessServerConfig`/`ClientConfig`, and `InProcessRendezvous`.
- **Phase 6** — `Type.InProcess` enum value + integration-test config branches (further expanded in R10).
- **Phase 7** — Recorder primitives (`InvocationRecorder`, `Arg.Any<T>()`/`Arg.Is<T>(predicate)` sentinels, `ArgMatcher`, `ExpressionParser`, `NexusAssertionException`).
- **Phase 8** — `TestInvocationInterceptor`, `QuiescenceCounters`, `QuiescenceTracker` with observe-zero/yield/re-observe pattern.
- **Phase 9** — `PipeRecording`, `TappingPipeReader`/`TappingPipeWriter`, `TappedNexusDuplexPipe`, `TestPipeFactory`.
- **Phase 10** — `NexusTestHost.CreateAsync` static entry point, `NexusTestHost<...>`, `NexusTestClient<...>`, `TestIdentity.Of`, `TestAuthenticationStore`.
- **Phase 12** — Server-side `AssertReceived`/`AssertNotReceived`/`WaitFor` on the host with `MethodIdMap` + `ArgumentDeserializer`.
- **Phase 14** — Demo + showcase rewrite (added after first review pass). The old `DemoServerNexus` had passthrough methods (`JoinGroup`/`BroadcastToGroup`) that showed harness ergonomics in the worst light by forcing users to write passthrough plumbing just to test broadcasts. Replaced with a realistic `EditorServerNexus` document-editor domain (`OpenDocument`, `LeaveDocument`, `SaveDraft` [Write], `Whisper`, `BroadcastSystemAnnouncement` [Admin], `ListActiveEditors`, `UploadAttachment`, `StreamEdits`) that exercises every harness feature through natural business verbs. `OnAuthorize` override matches `TestIdentity.IsInRole` case-sensitive ordinal against `DocPermission` enum names. `EditorAppShowcaseTests.cs` adds 18 focused tests; existing dependent tests (`AssertionTests`, `ClientAssertionTests`, `NexusTestHostTests`, `StreamingExtensionsTests`, `MethodIdMapTests`) updated to the new domain; `HarnessSampleNexus.cs` + `HarnessShowcaseTests.cs` removed.

## Deviations from plan implemented

- **Hook reduction.** Plan called for three core factories (invocation interceptor + pipe factory + channel factory). Channels do not need a core factory in v1 — harness convenience helpers (`ChannelPublishAsync`/`ChannelCollectAsync`) own channel creation directly. The pipe-layer byte tap still observes any user-instantiated channels (Decisions §"Revisions after source verification").
- **Hook location on `ConfigBase`.** Plan considered direct fields on `NexusSessionConfigurations`. Implementation stores them on `ConfigBase` (authoritative install point) and copies into the per-session struct at construction. Users install hooks once on the config and every session inherits them.
- **Transport home.** Plan started with `MemoryTransport` in NexNet core; revised to `InProcessTransport` shipped in `NexNet.Testing` so production code never has to take a test-package dependency. The integration-test-validation argument is preserved by `NexNet.IntegrationTests` taking a project reference to `NexNet.Testing` and exercising the InProcess transport through the existing `[TestCase]` matrix.
- **TimeProvider integration deferred** to follow-up issue #75. Verification revealed the actual scope (14 time references + 5 timers + 9 `Task.Delay` calls + an existing `TickCountOverride` test seam) was large enough to warrant its own focused workflow. The harness ships without time control as a known limitation; tests of time-sensitive logic (auth cache TTL, reconnect, ping) remain real-time-bound until #75 lands.

## Gaps in original plan implemented

These were uncovered during REVIEW after the 13-phase IMPLEMENT pass and addressed in the R1–R12 remediation:

- **Quiescence counters were dead code.** Two of four counters (`bytesInTransit`, `pendingResults`) had no production wire-up at all — the unit tests called the mutators directly but real session activity never did. R1 fixed this with `CountingPipeWriter`/`CountingPipeReader` in `InProcessTransport` and per-session `InternalOnSessionSetup` callbacks on both server and client configs.
- **Multi-client `ConnectAsAsync` hang.** Phase 13 was reduced-scope because a second connect against the same host hung. R2 traced this to user-supplied factories returning a shared nexus instance corrupting `SessionContext`; the harness now detects this and throws a clear error instead of hanging. Multi-client tests (3 clients + group broadcast) added in R3.
- **Group introspection** (`host.Groups[name].Members`) added in R3 — previously deferred because of the multi-client hang.
- **Streaming-helper extensions** (`PipeUploadAsync`, `PipeDownloadAsync`, `ChannelPublishAsync<T>`, `ChannelCollectAsync<T>`) added in R4 (plan §11).
- **Per-client assertion API** on `NexusTestClient` added in R5 (plan §12).
- **`MethodIdMap` generator parity** — runtime build now uses `BindingFlags.DeclaredOnly` + alphabetically-sorted inherited interfaces (no `.Distinct()`), matching the generator's `OrderBy(i => i.ToDisplayString())` ordering. Pinned by new `MethodIdMapTests`. (R6)
- **Generator-aligned arg deserializer filter** — replaced namespace-prefix heuristic with exact-FullName matches against the generator's exclusion list. (R7)
- **Mismatch diagnostics include arg values** — `AssertReceived` failures now render `Notify("alpha"); Notify("beta")` instead of `#1, #1`. (R7)
- **Multi-arg + string-arg AssertionTests** added (R7), and the synthetic ExpressionParser test replaced with a real `n => n.IntValue` property-access case (R10).
- **Tap continuation deduplication** in `TappedNexusDuplexPipe` (R8); `WaitFor` switched to `Environment.TickCount64` monotonic clock (R8).
- **PredicateMatcher exception surfacing** — user-thrown predicate exceptions are now reported as `NexusAssertionException` with inner exception preserved, not silently masked. (R9)
- **Integration matrix expansion** — `[TestCase(Type.InProcess)]` added to all three hook test classes plus pipes, channels, collections, groups, cancellation, and invalid-invocations (+83 InProcess test cases, R10).
- **Polish**: `TestAuthenticationStore.OverrideDelegate` cached as a field; `NexusTestHost.DisposeAsync` logs teardown errors instead of swallowing; `InProcessRendezvous.Unregister` rewritten with `TryGetValue`+`ReferenceEquals`+`TryRemove`. (R11)
- **API ergonomics**: public `NexusTestHost.RecordedServerInvocationCount`; per-typeparam XML docs on `CreateAsync`; AppDomain/ALC scope documented on `InProcessRendezvous`. (R12)

See `_sessions/add-nexnet-testing/review.md` for the full 40-finding classification table and per-finding remediation notes.

## Second review pass (full branch re-review)

A second structured review over the full branch diff (after the first finalize gate) surfaced 8 findings (1 Med, 7 Low). The 5 actionable items were fixed in-branch; 3 are documented/tracked (no action). See `review.md` §"Full branch re-analysis (Session 8)" for the classification table.

- **MethodIdMap ignored-method parity (Med).** The runtime map now filters `[NexusMethod(Ignore=true)]` methods before assigning ids, matching the generator's pre-`AssignMethodIds` filter — previously an ignored method would shift the ids of methods declared after it, silently misaligning `AssertReceived`/`AssertNotReceived`. Regression test added.
- **MethodIdMap generic-interface ordering.** Inherited interfaces are now ordered by a C#-style display name matching the generator's `ToDisplayString()` ordinal, rather than `Type.FullName` (which diverges for generic/nested interfaces). Regression test added (`IGen<Guid>` vs `IGen<bool>`).
- **Orphaned public surface.** `ChannelRecording<T>` was demoted from public to internal: no harness producer (`TapChannel<T>`) ships in v1, so it had no way to be populated. Internalizing keeps a non-breaking public promotion open for later.
- **Dead code.** Deleted the unused `TappedRentedNexusDuplexPipe` (local pipes pass through unwrapped since R4) and corrected the `TestPipeFactory` doc.
- **Package README.** `NexNet.Testing` now ships a harness-specific NuGet README instead of inheriting the core-NexNet README.

Documented/tracked (no action): the aggregate (host-global) quiescence counter model; the `Task.Run` fire-and-forget quiescence residual; wall-clock-bound timing tests (deterministic time control tracked by #75).

## Migration Steps

None for existing consumers. To adopt the harness:

1. Add a project reference to `NexNet.Testing` from the test project.
2. Replace handcrafted socket setup with `await using var host = await NexusTestHost.CreateAsync<...>();`.
3. Use `client.Server`/`client.Nexus`/`client.AssertReceived(...)` and `host.AssertReceived(...)` for assertions.

## Performance Considerations

- Production hot paths take three nullability checks (`_invocationInterceptor`, `_pipeFactory`, `OnAuthenticateOverride`). All default to `null` in non-harness code; the JIT specializes the branch.
- `TappingPipeWriter.GetSpan` re-routes through `GetMemory` so the tap can read the source; cost is only paid on tapped (test-only) pipes.
- Quiescence counter increments are `Interlocked.*` ops on the shared host counter; one per byte movement / dispatch / pipe-open. Test-only overhead.

## Security Considerations

- All three new core hooks are `internal` with `[InternalsVisibleTo("NexNet.Testing")]`; promotion to public is a v1.x decision when external demand exists.
- The `NexNet.csproj` grant to `NexNet.Testing.Tests` was removed in R9 — the test project now reaches NexNet internals only through `NexNet.Testing`'s intended surface.
- `OnAuthenticateOverride` is server-side only; the harness installs it via the public-readable `InProcessServerConfig.OnAuthenticateOverride` property. Production code that doesn't set this property keeps the existing `OnAuthenticate`-only path.
- `TestAuthenticationStore` accumulates tokens for the host's lifetime by design (the store can't tell which tokens are still in use). Documented in XML; `Clear()` added for long-lived hosts.
- `ArgumentDeserializer` only ever runs on bytes the session has already validated — it doesn't ingest untrusted input.
- `ArgMatcher.PredicateMatcher` no longer swallows predicate-thrown exceptions silently; they surface as `NexusAssertionException` with inner-exception preserved.

## Breaking Changes

### Consumer-facing
- None for application code.
- **New public surface in `NexNet.Testing` is v1** — `NexusTestHost`, `NexusTestClient`, `NexusAssertionException`, `Arg.Any<T>()`, `Arg.Is<T>(...)`, `TestIdentity.Of(...)`, `PipeRecording`, `GroupView`, `GroupIntrospector`, `StreamingExtensions`. Future iteration may add overloads non-breakingly. (`ChannelRecording<T>` was demoted to internal in the second review pass — see below — until a public `TapChannel<T>` producer ships.)

### Internal
- `ConfigBase` gains `internal IInvocationInterceptor?` and `internal IPipeFactory?` properties.
- `ServerConfig` gains `internal Func<ReadOnlyMemory<byte>?, ValueTask<IIdentity?>>? OnAuthenticateOverride`.
- `ISessionInvocationStateManager` gains `int PendingInvocationCount { get; }`.
- `NexusServer<TServerNexus, TClientProxy>` gains `internal IServerSessionManager? SessionManagerInternal`.
- `NexNet.csproj` `InternalsVisibleTo` grants reshuffled — `NexNet.Testing` grant moved to source attribute; `NexNet.Testing.Tests` grant removed.

## Test plan
- [ ] CI matrix passes (Generator + IntegrationTests + Testing tests).
- [ ] All hook tests run on both `Type.Tcp` and `Type.InProcess`.
- [ ] All pipe/channel/collection/group tests now exercise `Type.InProcess`.
- [ ] `NexNet.Testing.Tests` (84 tests) covers transport, recorder, MethodIdMap parity (declaration order, explicit ids, ignored-method filtering, and generic-inherited-interface ordering), quiescence under real load, host construction with multi-client connect and shared-instance detection, group introspection + broadcast, streaming helpers (upload/download/publish/collect), per-client assertions, and the 18-test `EditorAppShowcaseTests` covering identity flow, authorization (Write/Admin gates), `GroupExceptCaller`/`Client(id)`/`All` routing, pipe + channel streaming, mixed-traffic quiescence, and `Arg.Any`/`Arg.Is` matchers.
