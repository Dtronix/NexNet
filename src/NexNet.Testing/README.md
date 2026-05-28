# NexNet.Testing

Test harness and in-process transport for [NexNet](https://github.com/Dtronix/NexNet). Exercise your
nexus implementations — server methods, client callbacks, authorization rules, broadcasts, group
routing, pipes, and channels — **without** standing up sockets, ports, TLS, or fake auth providers.

`NexNet.Testing` is test-framework agnostic: assertions throw `NexusAssertionException`, which
NUnit, xUnit, and MSTest all surface as a normal test failure. No framework integration package is
required.

## Install

```
dotnet add package NexNet.Testing
```

Add the reference from your test project (alongside the project that defines your nexuses).

## Quickstart

```csharp
await using var host = await NexusTestHost.CreateAsync<
    EditorServerNexus, EditorServerNexus.ClientProxy,
    EditorClientNexus, EditorClientNexus.ServerProxy>();

// Connect clients with fake identities (name + roles). Roles drive [NexusAuthorize] checks
// through your nexus's OnAuthorize override — you test your real authorization logic.
var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
var bob   = await host.ConnectAsAsync(TestIdentity.Of("bob",   "Write"));
var carol = await host.ConnectAsAsync(TestIdentity.Of("carol", "Read"));

await alice.Server.OpenDocument("design.md");
await bob.Server.OpenDocument("design.md");
await carol.Server.OpenDocument("recipe.txt");

// SaveDraft is [NexusAuthorize<DocPermission>(Write)]-gated and broadcasts DraftSaved to
// Context.Clients.Group($"doc-{docId}") internally — a real business method, not a passthrough.
await alice.Server.SaveDraft("design.md", "v1");

// Quiesce: wait until all in-flight bytes, dispatches, pending results, and pipes settle. This is
// what makes negative assertions (AssertNotReceived) deterministic.
await host.QuiesceAsync();

bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
carol.AssertNotReceived<IEditorClientNexus>(
    n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()));

Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(2));
```

## What you get

| Surface | Purpose |
|---------|---------|
| `NexusTestHost.CreateAsync<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(…)` | Spins up an in-process server. Returns a host you `await using`. |
| `host.ConnectAsAsync(TestIdentity.Of(name, roles))` | Connects a client under a fake identity; returns a `NexusTestClient`. |
| `client.Server` / `client.Nexus` / `client.SessionId` | The strongly-typed proxy to call server methods, the client nexus instance, and the resolved session id. |
| `host.QuiesceAsync()` | Awaits until all transit bytes, dispatches, pending results, and open pipes settle. |
| `client.AssertReceived<I>(expr, times)` / `AssertNotReceived<I>(expr)` / `WaitFor<I>(expr)` | Per-client callback assertions using LINQ expressions. |
| `host.AssertReceived<I>(expr, times)` | Server-side: assert what the server received. |
| `Arg.Any<T>()` / `Arg.Is<T>(predicate)` | Argument matchers inside assertion expressions. |
| `host.Groups[name].Members` / `.Count` | Introspect live group membership. |
| `TestIdentity.Of(name, roles)` | Build a fake principal; roles feed your `OnAuthorize`. |
| `PipeRecording` | Observe byte traffic on a tapped pipe (`WaitForBytesAsync`, `ConsumedBytes`, …). |
| `StreamingExtensions` (`PipeUploadAsync` / `PipeDownloadAsync` / `ChannelPublishAsync<T>` / `ChannelCollectAsync<T>`) | Convenience helpers for pipe/channel-based methods. |
| `InProcessServerConfig` / `InProcessClientConfig` | The in-process transport, usable directly as just another NexNet transport. |

## Notes

- **Quiescence** is the load-bearing primitive. After driving traffic, `await host.QuiesceAsync()`
  before asserting; negative assertions are only meaningful once the system is known to be idle.
- **Testing your own auth.** Connect with `TestIdentity.Of(name, roles)` and your real
  `OnAuthorize` override runs against those roles. To test your own `OnAuthenticate` code instead,
  leave the harness override unset and connect with a real token.
- **Time-sensitive logic.** This release does not yet provide deterministic time control, so tests
  of TTL / reconnect / ping timing remain wall-clock-bound. Fake-time support is tracked upstream.

See the [NexNet repository](https://github.com/Dtronix/NexNet) for full documentation.
