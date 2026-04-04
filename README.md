# <img src="./docs/images/logo-128.png" height="48"> NexNet

Real-time bidirectional networking for .NET. Source-generated hubs and proxies with zero reflection, multiple transports, and full AOT compatibility.

**[Documentation](https://dtronix.github.io/NexNet/)** | **[API Reference](https://dtronix.github.io/NexNet/api/)**

---

## Packages

| Name | NuGet | Description |
|------|-------|-------------|
| [`NexNet`](https://www.nuget.org/packages/NexNet) | [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) | Core library for server and client communication. |
| [`NexNet.Generator`](https://www.nuget.org/packages/NexNet.Generator) | [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator) | Source generator for hubs and proxies. |
| [`NexNet.Quic`](https://www.nuget.org/packages/NexNet.Quic) | [![NexNet.Quic](https://img.shields.io/nuget/v/NexNet.Quic.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Quic) | QUIC transport support. |
| [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) | [![NexNet.Asp](https://img.shields.io/nuget/v/NexNet.Asp.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Asp) | ASP.NET Core integration for WebSocket and HttpSocket transports. |

---

## Why NexNet

NexNet is a networking framework for .NET that handles bidirectional communication between servers and clients. A Roslyn source generator emits all hub and proxy code at compile time — there is no reflection, no runtime code generation, and the result is fully NativeAOT compatible.

Interfaces define the contract between server and client. The generator produces strongly-typed proxies, invocation dispatchers, and serialization code. At runtime, connecting and invoking methods is straightforward: start a server, connect a client, and call methods through the proxy. NexNet handles reconnection, multiplexing, and transport abstraction.

Beyond RPC, NexNet provides synchronized collections that keep data in sync across server and clients, duplex pipes for raw byte streaming, typed channels for structured data streaming, and a declarative authorization system — all source-generated with the same zero-reflection approach.

---

## Comparison

| Capability | NexNet | SignalR | gRPC |
|---|---|---|---|
| Source generated (no reflection) | Yes | No | Yes (protobuf) |
| Bidirectional RPC | Yes | Yes (hub methods) | Yes (streams) |
| Transport options | 6 (UDS, TCP, TLS, QUIC, WS, HttpSocket) | 3 (WS, SSE, Long Polling) | 1 (HTTP/2) |
| Synchronized collections | Yes | No | No |
| Duplex byte streaming | Yes (pipes + channels) | Yes (streams) | Yes (streams) |
| Auto reconnection | Yes | Yes | No |
| Built-in rate limiting | Yes | No | No |
| NativeAOT compatible | Yes | No | Partial |
| Session groups | Yes | Yes | No |
| Interface versioning | Yes (hash-locked) | No | Yes (protobuf) |
| ASP.NET integration | Yes | Native | Native |

---

## Features

- **[Source-generated hubs and proxies](https://dtronix.github.io/NexNet/articles/getting-started.html)** — all invocation code emitted at compile time; no reflection
- **[Bidirectional method invocation](https://dtronix.github.io/NexNet/articles/hub-invocations.html)** — server-to-client and client-to-server calls with void, ValueTask, and ValueTask&lt;T&gt; returns
- **[Synchronized collections](https://dtronix.github.io/NexNet/articles/synchronized-collections.html)** — INexusList with server-to-client, bidirectional, and relay modes
- **[Duplex pipes](https://dtronix.github.io/NexNet/articles/duplex-pipes.html)** — bidirectional byte streaming with built-in congestion control
- **[Typed channels](https://dtronix.github.io/NexNet/articles/channels.html)** — INexusDuplexChannel&lt;T&gt; and INexusDuplexUnmanagedChannel&lt;T&gt; with IAsyncEnumerable support
- **[Session management](https://dtronix.github.io/NexNet/articles/sessions-and-lifetimes.html)** — per-session hub instances, named groups for targeted broadcasting, automatic reconnection
- **[Authentication](https://dtronix.github.io/NexNet/articles/authentication.html)** — token-based connection authentication with IIdentity
- **[Authorization](https://dtronix.github.io/NexNet/articles/authorization.html)** — declarative `[NexusAuthorize<TPermission>]` with caching and compile-time diagnostics
- **[Interface versioning](https://dtronix.github.io/NexNet/articles/versioning.html)** — version hierarchy with HashLock validation and runtime enforcement
- **[Six transports](https://dtronix.github.io/NexNet/articles/transports.html)** — UDS, TCP, TLS, QUIC, WebSocket, HttpSocket
- **[ASP.NET Core integration](https://dtronix.github.io/NexNet/articles/asp-net-integration.html)** — middleware with DI, authentication, and reverse proxy support
- **[Rate limiting](https://dtronix.github.io/NexNet/articles/rate-limiting.html)** — per-IP limits, sliding windows, and automatic banning
- **[Benchmarks](https://dtronix.github.io/NexNet/articles/benchmarks.html)** — ~27,000 invocations/sec with minimal allocations

---

## Quick Start

```csharp
// Shared interfaces
interface IClientNexus
{
    ValueTask<string> GetUserName();
}
interface IServerNexus
{
    ValueTask<int> GetStatus(int userId);
}

// Client nexus
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    public ValueTask<string> GetUserName()
        => new("Bill");
}

// Server nexus
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public ValueTask<int> GetStatus(int userId)
        => new(1);
}

// Usage
var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());
var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

await server.StartAsync();
await client.ConnectAsync();
var status = await client.Proxy.GetStatus(42);
```

The generator emits all hub and proxy classes at compile time. See [Getting Started](https://dtronix.github.io/NexNet/articles/getting-started.html) for the full walkthrough.

---

## Benchmarks

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.7462/24H2/2024Update/HudsonValley)
Intel Core i7-10700 CPU 2.90GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 9.0.305
  [Host]     : .NET 9.0.9 (9.0.9, 9.0.925.41916), X64 RyuJIT x86-64-v3
  Job-FJSVHN : .NET 9.0.9 (9.0.9, 9.0.925.41916), X64 RyuJIT x86-64-v3

Platform=X64  Runtime=.NET 9.0
```

| Method | Mean | Error | StdDev | Op/s | Allocated |
|--------|-----:|------:|-------:|-----:|----------:|
| InvocationNoArgument | 36.7 us | 0.33 us | 0.31 us | 27,241.7 | 595 B |
| InvocationUnmanagedArgument | 37.4 us | 0.52 us | 0.48 us | 26,769.8 | 649 B |
| InvocationUnmanagedMultipleArguments | 37.3 us | 0.28 us | 0.25 us | 26,800.8 | 700 B |
| InvocationNoArgumentWithResult | 36.9 us | 0.35 us | 0.32 us | 27,095.2 | 633 B |
| InvocationWithDuplexPipe_Upload | 51.8 us | 0.60 us | 0.51 us | 19,295.4 | 14,951 B |

---

## Transport Selection Guide

| Scenario | Recommended Transport | Reason |
|----------|----------------------|--------|
| Same machine IPC | Unix Domain Sockets | Highest performance, no network overhead |
| Local network | TCP | Simple, reliable, fast |
| Internet/WAN | TLS over TCP | Secure, widely supported |
| Mobile/unstable networks | QUIC | Connection migration, better congestion control |
| Web applications | WebSockets | Browser compatibility, firewall-friendly |
| Reverse proxy setups | HttpSockets | Lower overhead than WebSockets |

---

## Dependencies

- [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization.
- Internally packages Marc Gravell's [Pipelines.Sockets.Unofficial](https://github.com/Dtronix/Pipelines.Sockets.Unofficial/tree/nexnet-v1) with additional performance modifications for pipeline socket transports.
- QUIC transport requires `libmsquic` on Linux. [Windows Support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)

---

## Samples

| Sample | Description |
|--------|-------------|
| [`NexNetDemo`](https://github.com/Dtronix/NexNet/tree/master/src/Samples/NexNetDemo) | Console app demonstrating invocations, channels, duplex pipes, collections, and API versioning |
| [`NexNetSample.Asp`](https://github.com/Dtronix/NexNet/tree/master/src/Samples) | ASP.NET Core server and client with HttpSocket transport, bearer auth, duplex pipes, and collections |
