---
_layout: landing
---

<div style="text-align: center; padding: 0.75rem 0 0.5rem;">
  <img src="images/logo-128.png" alt="NexNet" style="height: 64px; margin-bottom: 0.5rem;" />
  <h1 style="margin-bottom: 0.25rem; font-size: 2rem;">NexNet</h1>
  <p style="font-size: 1.1rem; color: #666; max-width: 640px; margin: 0 auto;">
    Real-time bidirectional networking for .NET. Source-generated hubs, multiple transports, zero reflection. AOT compatible.
  </p>
</div>

---

## Explore the Documentation

<div class="row" style="margin-top: 0.5rem;">
<div class="col-md-6">

### [Getting Started](articles/getting-started.md)
Install NexNet and build your first server-client hub in minutes.

### [Hub Invocations](articles/hub-invocations.md)
Method patterns, return types, fire-and-forget, and broadcasting.

### [Sessions & Lifetimes](articles/sessions-and-lifetimes.md)
Hub lifecycle, session groups, ping, and automatic reconnection.

### [Synchronized Collections](articles/synchronized-collections.md)
Real-time INexusList with server-to-client, bidirectional, and relay modes.

### [Duplex Pipes](articles/duplex-pipes.md)
Stream raw bytes bidirectionally with built-in congestion control.

### [Channels](articles/channels.md)
Type-safe streaming with INexusDuplexChannel&lt;T&gt; and IAsyncEnumerable.

</div>
<div class="col-md-6">

### [Authentication](articles/authentication.md)
Token-based authentication with server-side validation.

### [Authorization](articles/authorization.md)
Declarative method and collection authorization with caching.

### [Versioning](articles/versioning.md)
Interface versioning with hash-lock validation and backward compatibility.

### [Transports](articles/transports.md)
Six transport types: UDS, TCP, TLS, QUIC, WebSocket, and HttpSocket.

### [ASP.NET Integration](articles/asp-net-integration.md)
Middleware integration with DI, authentication, and reverse proxy support.

### [Rate Limiting](articles/rate-limiting.md)
Connection rate limiting and DoS protection.

</div>
</div>

---

## See It in Action

Define shared interfaces and let the source generator handle the rest:

```csharp
public interface IClientNexus
{
    ValueTask<string> GetUserName();
}
public interface IServerNexus
{
    ValueTask<int> GetStatus(int userId);
}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public ValueTask<int> GetStatus(int userId)
        => new(1);
}

// Connect and invoke
var server = ServerNexus.CreateServer(config, () => new ServerNexus());
await server.StartAsync();
await client.ConnectAsync();
var status = await client.Proxy.GetStatus(42);
```

The generator emits all hub and proxy classes at compile time — no reflection, no runtime code generation.

---

## Quick Install

```xml
<PackageReference Include="NexNet" Version="*" />
<PackageReference Include="NexNet.Generator" Version="*-*">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

---

## Why NexNet?

<div class="row" style="margin-top: 1rem;">
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Source Generated</h3>
<p>All hubs and proxies are emitted by a Roslyn source generator at compile time. No reflection, fully NativeAOT compatible.</p>
</div>
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Multiple Transports</h3>
<p>Unix Domain Sockets, TCP, TLS, QUIC, WebSockets, and HttpSockets. Pick the right transport for each deployment.</p>
</div>
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Synchronized Collections</h3>
<p>INexusList keeps data in sync across server and clients with server-to-client, bidirectional, and relay modes.</p>
</div>
</div>

<div class="row">
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Duplex Streaming</h3>
<p>Stream bytes via INexusDuplexPipe or typed data via INexusDuplexChannel&lt;T&gt; with built-in congestion control.</p>
</div>
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Auth & Authorization</h3>
<p>Token-based authentication and declarative method-level authorization with <code>[NexusAuthorize]</code>. Caching included.</p>
</div>
<div class="col-md-4" style="margin-bottom: 1.5rem;">
<h3>Auto Reconnection</h3>
<p>Clients automatically reconnect on timeout or connection loss with no additional configuration required.</p>
</div>
</div>
