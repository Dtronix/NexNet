# P01 shared rules — porting bucket-A tests to IClientUnderTest

These rules apply to every P01a–P01h card. Read this file together with your P01x card.

## When to start
A P01x card can start once **H01 and H04** are merged. Port against the `csharp` row first. Rows for `node`, `browser-*` and `java` turn on automatically once J12 and K12 merge.

- **Before J12/K12 merge:** the PR only needs the C# row green.
- **After they merge:** the coordinator re-runs the PR with `NEXNET_TEST_LANGS=node,browser,java`. Any failure gets fixed in the runtime (as a follow-up card for the J*/K* owner) or is marked `[Flaky("issue")]`. **A test is never silently skipped.**

## How to port a test
1. **Replace the client setup.** `CreateServerClient(...)`/`CreateClient(...)` become:
   - `[ClientMatrix(Transports = …)] ClientTarget target` as a parameter;
   - `await using var c = await Harness.CreateClientAsync(target, spec)`.

   The server side keeps using the C# `NexusServer` via the H07 shared host, or a dedicated server when the test restarts or stops it.
2. **Proxies.** `client.Proxy.X(...)` becomes `c.Proxy<IServerNexus>().X(...)`.
3. **Client callbacks.** `ClientNexus.<Event> = lambda` becomes `c.Callbacks<ClientNexusScript>().<Method>.Returns(...)`, or `.Forward(lambda)` when the logic is complex.
   - Prefer in-host steps for anything timing-sensitive: delays, cancellation, slow readers.
4. **Pipes.** Pipes become `IRemotePipe`. Bulk data uses `WritePattern(seed, len)` plus `Drain()`, which returns `(len, sha256)`, never large byte arrays.
5. **Synchronous state reads become async:** `GetStateAsync`, `WaitForStateAsync`, `List.SnapshotAsync`.
6. **Capabilities.** If a test needs a capability (Authorization header, client certificate, pinning, fake time, UDS, …), declare it with `Requires = Capability.X`. Unsupported rows show as skipped, with a reason.
7. **Exception assertions.** Keep the exact-type checks (`ThrowsExactly`). The remote driver maps error kinds to the same C# exceptions.
8. **Timeouts.** Use `T(x)`, and assert durations measured in the host where timing matters.
9. **Server-side assertions** stay in C#: observe through the server nexus or the server-side taps (H01).
10. **Leak audit.** It runs automatically after each test. Never suppress it.

## Acceptance (every P01x card)
- The test count for the area is unchanged on the C# row. List any intentionally dropped test, with its reason (it must be C#-runtime-specific).
- Green on the `csharp` row, run 3 times.
- Green on the remote rows once available, or follow-up cards are filed.
