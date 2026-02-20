# NexNet Usage Reference

High-performance .NET 10 async networking. Bidirectional server-client communication. Source-generated, AOT-friendly. MemoryPack serialization.

## Packages
```xml
<PackageReference Include="NexNet" Version="0.14.1" />
<PackageReference Include="NexNet.Generator" Version="0.14.1">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
<!-- Optional: NexNet.Quic, NexNet.Asp -->
```

## Core Pattern

```csharp
// Shared interfaces
public interface IClientNexus { ValueTask ReceiveMessage(string msg); }
public interface IServerNexus { ValueTask SendMessage(string msg); }

// Client implements IClientNexus, calls IServerNexus via Proxy
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus {
    public ValueTask ReceiveMessage(string msg) => ValueTask.CompletedTask;
}

// Server implements IServerNexus, calls IClientNexus via Proxy
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus {
    public ValueTask SendMessage(string msg) => ValueTask.CompletedTask;
}

// Usage
var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());
await server.StartAsync();
var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
await client.ConnectAsync();
await client.Proxy.SendMessage("Hello");
```

Nexus classes must be `partial`, not abstract/nested/generic. One instance per connection. No constructor work (pooled via `ContextProvider`).

## Method Signatures

| Return | Behavior | Allowed Params |
|--------|----------|----------------|
| `void` | Fire-and-forget | args only |
| `ValueTask` | Await completion | args + CT, OR args + pipes/channels |
| `ValueTask<T>` | Await + return | args + CT only |

CancellationToken must be last. Max serialized args: 65,535 bytes (use pipes for larger).

## Lifecycle

```csharp
// Both
protected override ValueTask OnConnected(bool isReconnected) => default;
protected override ValueTask OnDisconnected(Exception? ex) => default;
// Client only
protected override ValueTask OnReconnecting() => default;
// Server only (null = reject auth)
protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? token) => ...;
// Server only (after auth, before OnConnected)
protected override ValueTask OnNexusInitialize() => default;
```

## Transports

| Scenario | Server Config | Client Config |
|----------|---------------|---------------|
| Unix IPC | `UdsServerConfig` | `UdsClientConfig` |
| TCP | `TcpServerConfig` | `TcpClientConfig` |
| TLS/TCP | `TcpTlsServerConfig` | `TcpTlsClientConfig` |
| QUIC | `QuicServerConfig` | `QuicClientConfig` |
| WebSocket | ASP.NET server | `WebSocketClientConfig` |
| HttpSocket | ASP.NET server | `HttpSocketClientConfig` |

```csharp
// TCP
new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 1234) };
new TcpClientConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 1234) };

// UDS
new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint("/tmp/app.sock") };

// TLS - set SslServerAuthenticationOptions / SslClientAuthenticationOptions
new TcpTlsServerConfig {
    EndPoint = new IPEndPoint(IPAddress.Any, 1234),
    SslServerAuthenticationOptions = new() {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "pass"),
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    }
};

// QUIC (requires NexNet.Quic; libmsquic on Linux)
new QuicServerConfig {
    EndPoint = new IPEndPoint(IPAddress.Any, 1234),
    SslServerAuthenticationOptions = new() { ... }
};

// WebSocket/HttpSocket clients
new WebSocketClientConfig { Url = new Uri("ws://localhost:5000/nexus") };
new HttpSocketClientConfig { Url = new Uri("http://localhost:5000/nexus") };
// Both support: AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "token")
```

## Configuration

### Base (all transports)

| Property | Default | Description |
|----------|---------|-------------|
| `Logger` | null | `INexusLogger` instance |
| `MaxConcurrentConnectionInvocations` | 2 | 1-1000 |
| `DisconnectDelay` | 200ms | 0-10000ms |
| `Timeout` | 30000ms | 50-300000ms, idle timeout |
| `HandshakeTimeout` | 15000ms | 50-60000ms |
| `NexusPipeFlushChunkSize` | 8KB | 1KB-1MB |
| `NexusPipeHighWaterMark` | 192KB | Pause writer threshold |
| `NexusPipeLowWaterMark` | 16KB | Resume threshold |
| `NexusPipeHighWaterCutoff` | 256KB | Hard stop threshold |

### Client-specific

| Property | Default | Description |
|----------|---------|-------------|
| `ConnectionTimeout` | 50000ms | Connect timeout |
| `PingInterval` | 10000ms | Keepalive interval |
| `ReconnectionPolicy` | null | `IReconnectionPolicy`; null = disabled |
| `Authenticate` | null | `Func<Memory<byte>>` auth token provider |

### Server-specific

| Property | Default | Description |
|----------|---------|-------------|
| `AcceptorBacklog` | 20 | Listen backlog |
| `Authenticate` | false | Require client auth |
| `RateLimiting` | null | `ConnectionRateLimitConfig`; null = disabled |

### TCP options (TcpServerConfig/TcpClientConfig)

`DualMode`, `KeepAlive`, `TcpKeepAliveTime` (-1=OS), `TcpKeepAliveInterval`, `TcpKeepAliveRetryCount`, `TcpNoDelay` (default: true). Server-only: `ReuseAddress`, `ExclusiveAddressUse`.

## Client API

```csharp
var client = ClientNexus.CreateClient(config, new ClientNexus());
await client.ConnectAsync();                   // throws on failure
var result = await client.TryConnectAsync();   // ConnectionResult with .Success, .State, .DisconnectReason
client.StateChanged += (s, state) => { };      // ConnectionState enum
await client.DisconnectedTask;                 // wait for disconnect
await client.DisconnectAsync();
// Create pipes/channels
var pipe = client.CreatePipe();                // IRentedNexusDuplexPipe
var ch = client.CreateChannel<T>();            // INexusDuplexChannel<T>
var uch = client.CreateUnmanagedChannel<T>();  // INexusDuplexUnmanagedChannel<T>
```

`ConnectionState`: Unset, Connecting, Connected, Reconnecting, Disconnecting, Disconnected.

### Reconnection

```csharp
// DefaultReconnectionPolicy: retries at 0s, 2s, 10s, 30s then repeats last
config.ReconnectionPolicy = new DefaultReconnectionPolicy();
// Custom intervals
config.ReconnectionPolicy = new DefaultReconnectionPolicy(
    [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)], continuousRetry: true);
```

### Connection Pooling

```csharp
var poolConfig = new NexusClientPoolConfig(clientConfig) {
    MaxConnections = 10, MaxIdleTime = TimeSpan.FromMinutes(2), MinIdleConnections = 1
};
var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);
using var rental = await pool.RentClientAsync();
await rental.Proxy.DoSomething();
await rental.EnsureConnectedAsync();   // reconnect if needed
// Also: pool.GetCollectionConnector(p => p.Items) for relay collections
```

## Server API

```csharp
var server = ServerNexus.CreateServer(config, () => new ServerNexus());
await server.StartAsync();
// server.State: Stopped, Running, Disposed
await server.StopAsync();
```

### Server Context (inside nexus methods)

```csharp
// Broadcasting
await Context.Clients.Caller.Method();               // calling client
await Context.Clients.All.Method();                   // all clients
await Context.Clients.Others.Method();                // all except caller
await Context.Clients.Client(id).Method();            // by session ID
await Context.Clients.Clients([id1, id2]).Method();   // multiple IDs
await Context.Clients.Group("room").Method();         // group members
await Context.Clients.Groups(["a", "b"]).Method();
await Context.Clients.GroupExceptCaller("room").Method();
await Context.Clients.GroupsExceptCaller(["a", "b"]).Method();
var ids = Context.Clients.GetIds();                   // all session IDs

// Groups
await Context.Groups.AddAsync("room");
await Context.Groups.AddAsync(["room1", "room2"]);
await Context.Groups.RemoveAsync("room");
var names = await Context.Groups.GetNamesAsync();

// Session info
long id = Context.Id;
string? user = Context.Identity?.DisplayName;

// Per-session key-value store (lifetime = connection)
Context.Store["key"] = value;
Context.Store.TryGet("key", out var val);

// Disconnect
await Context.DisconnectAsync();
```

### ContextProvider (external invocation)

```csharp
// Invoke clients from outside nexus methods (background services, timers)
using var owner = server.ContextProvider.Rent();
await owner.Context.Clients.All.Notify();
await owner.Context.Clients.Client(sessionId).Notify();
await owner.Context.Clients.Group("room").Notify();
```

## Rate Limiting

```csharp
var serverConfig = new TcpServerConfig {
    EndPoint = endpoint,
    RateLimiting = new ConnectionRateLimitConfig {
        MaxConcurrentConnections = 1000,   // total (default: 1000)
        GlobalConnectionsPerSecond = 100,  // new conn/sec (default: 100)
        MaxConnectionsPerIp = 0,           // per-IP concurrent (0=unlimited)
        ConnectionsPerIpPerWindow = 0,     // per-IP per window (0=unlimited)
        PerIpWindowSeconds = 60,           // window size (default: 60)
        BanDurationSeconds = 300,          // ban duration (default: 300)
        BanThreshold = 5,                  // violations before ban (default: 5)
        WhitelistedIps = ["127.0.0.1"]     // skip rate limiting
    }
};
```

## Authentication

Disabled by default. Enable with `ServerConfig.Authenticate = true`.

```csharp
// Server config
var serverConfig = new TcpServerConfig { EndPoint = ep, Authenticate = true };
// Server nexus: override OnAuthenticate (return null = reject)
protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? token) {
    var str = Encoding.UTF8.GetString(token!.Value.Span);
    return str == "valid" ? new(new DefaultIdentity { DisplayName = "User" }) : new((IIdentity?)null);
}
// Client config
var clientConfig = new TcpClientConfig { EndPoint = ep, Authenticate = () => Encoding.UTF8.GetBytes("valid") };
```

## Duplex Pipes (Byte Streaming)

NOT thread-safe. For large data or continuous streams.

```csharp
// Interface method
ValueTask Upload(INexusDuplexPipe pipe);
// Client
var pipe = client.CreatePipe();
await client.Proxy.Upload(pipe);
await pipe.ReadyTask;
await stream.CopyToAsync(pipe.Output);
await pipe.CompleteAsync();
// Server
public async ValueTask Upload(INexusDuplexPipe pipe) {
    await pipe.Input.CopyToAsync(destStream);
}
```

## Channels (Typed Streaming)

Thread-safe writing. `INexusDuplexChannel<T>` (MemoryPack) or `INexusDuplexUnmanagedChannel<T>` (unmanaged, faster).

```csharp
// Interface
ValueTask StreamData(INexusDuplexUnmanagedChannel<int> channel);
// Client
await using var channel = client.CreateUnmanagedChannel<int>();
await client.Proxy.StreamData(channel);
var reader = await channel.GetReaderAsync();
await foreach (var item in reader) { }
// Server
public async ValueTask StreamData(INexusDuplexUnmanagedChannel<int> channel) {
    var writer = await channel.GetWriterAsync();
    await writer.WriteAsync(42);
    await writer.CompleteAsync();
}
// Extensions
await channel.WriteAndComplete(enumerable, batchSize: 100);
var list = await reader.ReadUntilComplete(initialCapacity: 1000);
```

### Different types via pipe

```csharp
var pipe = client.CreatePipe();
await client.Proxy.StreamData(pipe);
await pipe.ReadyTask;
var writer = await pipe.GetChannelWriter<long>();
var reader = await pipe.GetChannelReader<string>();
// Also: GetUnmanagedChannelWriter/Reader<T>, GetUnmanagedChannel<T>, GetChannel<T>
```

## Synchronized Collections

Auto-synced server-to-client. Modes: `ServerToClient` (read-only client), `BiDirectional` (client can mutate), `Relay` (hierarchical).

```csharp
// Interface
public interface IServerNexus {
    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<int> Items { get; }
}
// Client usage
var list = client.Proxy.Items;
await list.ConnectAsync();   // EnableAsync() + ReadyTask
list.Changed.Subscribe(args => { /* Action: Add, Remove, Replace, Move, Reset, Ready */ });
await list.AddAsync(1);
await list.InsertAsync(0, 2);
await list.RemoveAsync(1);
await list.RemoveAtAsync(0);
await list.ReplaceAsync(0, 99);
await list.MoveAsync(0, 1);
await list.ClearAsync();
foreach (var item in list) { }  // read local copy
await list.DisableAsync();
```

### Relay mode

```csharp
// Master interface: [NexusCollection(NexusCollectionMode.ServerToClient)]
// Relay interface:  [NexusCollection(NexusCollectionMode.Relay)]
var masterPool = new NexusClientPool<MasterClient, MasterClient.ServerProxy>(poolConfig);
var relayServer = RelayNexus.CreateServer(config, () => new RelayNexus(),
    cfg => cfg.Context.Collections.Items.ConfigureRelay(
        masterPool.GetCollectionConnector(p => p.Items)));
```

## Versioning

Server-only. All methods need `[NexusMethod(id)]` with unique IDs. `HashLock` prevents accidental changes.

```csharp
[NexusVersion(Version = "v1.0", HashLock = -2031775281)]
public interface IServerV1 {
    [NexusMethod(1)] ValueTask<bool> GetStatus();
}
[NexusVersion(Version = "v2.0", HashLock = -1210855623)]
public interface IServerV2 : IServerV1 {
    [NexusMethod(2)] ValueTask<string> GetInfo();
}
// V2 server supports V1+V2 clients
[Nexus<IServerV2, IClient>(NexusType = NexusType.Server)]
public partial class ServerV2 { ... }
```

Attribute options: `[NexusMethod(Ignore = true)]`, `[NexusCollection(Id = 1)]`, `[NexusCollection(Ignore = true)]`.

## ASP.NET Integration

Requires `NexNet.Asp`.

```csharp
builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
app.UseAuthentication();
app.UseAuthorization();
// HttpSocket
await app.UseHttpSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c => {
    c.NexusConfig.Path = "/nexus";
    c.NexusConfig.AspEnableAuthentication = true;
    c.NexusConfig.AspAuthenticationScheme = "BearerToken";
    c.NexusConfig.TrustProxyHeaders = false; // X-Forwarded-For (default: false)
}).StartAsync(app.Lifetime.ApplicationStopped);
// WebSocket: UseWebSocketNexusServerAsync instead
```

## Logging

```csharp
config.Logger = new ConsoleLogger();                // stdout
config.Logger = new RollingLogger(maxLines: 200);   // circular buffer, Flush(TextWriter)
// NexusLogLevel: Trace, Debug, Information, Warning, Error, Critical, None
// NexusLogBehaviors flags: Default, ProxyInvocationsLogAsInfo, LocalInvocationsLogAsInfo, LogTransportData
```

## CancellationToken

```csharp
// Must be last parameter in interface
ValueTask Operation(int data, CancellationToken ct);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await client.Proxy.Operation(42, cts.Token);
```
