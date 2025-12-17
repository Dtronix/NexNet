# NexNet LLM Reference

High-performance .NET async networking with bidirectional server-client communication. All code is source-generated (no reflection, AOT-friendly).

## NuGet Packages
```xml
<PackageReference Include="NexNet" Version="0.11.0" />
<PackageReference Include="NexNet.Generator" Version="0.11.0">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
<!-- Optional: NexNet.Quic for QUIC, NexNet.Asp for ASP.NET -->
```

## Core Pattern

```csharp
// Shared interfaces (in shared project)
public interface IClientNexus { ValueTask ReceiveMessage(string msg); }
public interface IServerNexus { ValueTask SendMessage(string msg); }

// Client: implements IClientNexus, calls IServerNexus via Proxy
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus {
    public ValueTask ReceiveMessage(string msg) => ValueTask.CompletedTask;
}

// Server: implements IServerNexus, calls IClientNexus via Proxy
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

**Rules:** Nexus classes must be `partial`. One hub instance per connection. No work in constructors (hubs can be temporarily created/disposed via `ContextProvider`).

## Method Signatures

| Return Type | Behavior |
|-------------|----------|
| `void` | Fire-and-forget |
| `ValueTask` | Wait for completion |
| `ValueTask<T>` | Wait and return value |

### Argument Rules
- `void`: args only (no CT, pipes, or channels)
- `ValueTask`: args + CancellationToken, OR args + pipes/channels (not both)
- `ValueTask<T>`: args + CancellationToken only (no pipes/channels)
- CancellationToken must be last parameter
- Max serialized args: 65,535 bytes (use pipes for larger)

## Lifecycle Methods

```csharp
// Both client and server
protected override ValueTask OnConnected(bool isReconnected) => default;
protected override ValueTask OnDisconnected(Exception? ex) => default;

// Client only
protected override ValueTask OnReconnecting() => default;

// Server only
protected override ValueTask<bool> OnAuthenticate(ReadOnlyMemory<byte>? token) => new(true);
```

## Transport Configurations

| Scenario | Transport | Config Classes |
|----------|-----------|----------------|
| Same machine IPC | Unix Domain Sockets | `UdsServerConfig`, `UdsClientConfig` |
| Local network | TCP | `TcpServerConfig`, `TcpClientConfig` |
| Secure internet | TLS/TCP | `TcpTlsServerConfig`, `TcpTlsClientConfig` |
| Mobile/unstable | QUIC | `QuicServerConfig`, `QuicClientConfig` |
| Web browsers | WebSocket | `WebSocketClientConfig` (ASP.NET server) |
| Reverse proxy | HttpSocket | `HttpSocketClientConfig` (ASP.NET server) |

### TCP
```csharp
var serverConfig = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 1234) };
var clientConfig = new TcpClientConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 1234) };
```

### Unix Domain Sockets
```csharp
var serverConfig = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint("/tmp/app.sock") };
var clientConfig = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint("/tmp/app.sock") };
```

### TLS/TCP
```csharp
var serverConfig = new TcpTlsServerConfig {
    EndPoint = new IPEndPoint(IPAddress.Any, 1234),
    SslServerAuthenticationOptions = new() {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "pass"),
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    }
};
var clientConfig = new TcpTlsClientConfig {
    EndPoint = new IPEndPoint(IPAddress.Loopback, 1234),
    SslClientAuthenticationOptions = new() {
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        RemoteCertificateValidationCallback = (_, _, _, _) => true // dev only
    }
};
```

### QUIC (requires NexNet.Quic + libmsquic on Linux)
```csharp
var serverConfig = new QuicServerConfig {
    EndPoint = new IPEndPoint(IPAddress.Any, 1234),
    SslServerAuthenticationOptions = new() {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "pass"),
        EnabledSslProtocols = SslProtocols.Tls13
    }
};
```

### WebSocket / HttpSocket (ASP.NET clients)
```csharp
var wsConfig = new WebSocketClientConfig {
    Url = new Uri("ws://localhost:5000/nexus"),
    AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "token")
};
var httpConfig = new HttpSocketClientConfig {
    Url = new Uri("http://localhost:5000/nexus"),
    AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "token")
};
```

## Client Options

```csharp
var config = new TcpClientConfig {
    EndPoint = endpoint,
    ConnectionTimeout = 30000,        // default: 50000ms
    PingInterval = 5000,              // default: 10000ms
    ReconnectionPolicy = new DefaultReconnectionPolicy(),
    Authenticate = () => Encoding.UTF8.GetBytes("token")
};

var client = ClientNexus.CreateClient(config, new ClientNexus());
await client.ConnectAsync();                    // throws on failure
var result = await client.TryConnectAsync();    // returns Success bool
client.StateChanged += (s, state) => { };       // Connected, Disconnected, Reconnecting
await client.DisconnectedTask;                  // wait for disconnect
```

## Client Connection Pooling

```csharp
var poolConfig = new NexusClientPoolConfig(clientConfig);
var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

// Rent and auto-return
using var rental = await pool.RentClientAsync();
await rental.Client.Proxy.DoSomething();
```

## Server Broadcasting

```csharp
// Inside server nexus methods
await Context.Clients.Caller.Method();              // calling client only
await Context.Clients.All.Method();                 // all clients
await Context.Clients.Others.Method();              // all except caller
await Context.Clients.Client(id).Method();          // specific client
await Context.Clients.Clients([id1, id2]).Method(); // multiple clients
await Context.Clients.Group("room").Method();       // group members
await Context.Clients.Groups(["a", "b"]).Method();  // multiple groups

// Group management
Context.Groups.Add("room");
Context.Groups.Remove("room");
```

## Duplex Pipes (Byte Streaming)

NOT thread-safe. For large data or continuous streams.

```csharp
// Interface
ValueTask Upload(INexusDuplexPipe pipe);

// Client: create and send
var pipe = client.CreatePipe();
await client.Proxy.Upload(pipe);
await pipe.ReadyTask;
await stream.CopyToAsync(pipe.Output);
await pipe.CompleteAsync();

// Server: receive
public async ValueTask Upload(INexusDuplexPipe pipe) {
    await pipe.Input.CopyToAsync(destStream);
}
```

## Channels (Typed Object Streaming)

Thread-safe for writing. Uses MemoryPack serialization.

| Type | Use Case |
|------|----------|
| `INexusDuplexChannel<T>` | MemoryPack-serializable types |
| `INexusDuplexUnmanagedChannel<T>` | Unmanaged types (int, struct) - faster |

```csharp
// Interface
ValueTask StreamData(INexusDuplexUnmanagedChannel<int> channel);

// Client
await using var channel = client.CreateUnmanagedChannel<int>();
await client.Proxy.StreamData(channel);
var reader = await channel.GetReaderAsync();
await foreach (var item in reader) { }  // IAsyncEnumerable

// Server
public async ValueTask StreamData(INexusDuplexUnmanagedChannel<int> channel) {
    var writer = await channel.GetWriterAsync();
    await writer.WriteAsync(42);
    await writer.CompleteAsync();
}
```

### Channel Extensions
```csharp
await channel.WriteAndComplete(enumerable, batchSize: 100);  // bulk write
var list = await reader.ReadUntilComplete(initialCapacity: 1000);  // bulk read
```

### Different Types via Pipe
```csharp
var pipe = client.CreatePipe();
var writer = await pipe.GetChannelWriter<long>();
var reader = await pipe.GetChannelReader<string>();
```

## Synchronized Collections

Server-side only. Auto-synced to clients.

| Mode | Description |
|------|-------------|
| `ServerToClient` | Read-only on client |
| `BiDirectional` | Client can mutate |
| `Relay` | Broadcasts from parent to clients |

```csharp
// Interface (server only)
public interface IServerNexus {
    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<int> Items { get; }
}

// Client usage
var list = client.Proxy.Items;
list.Changed.Subscribe(args => { });     // reactive updates
await list.EnableAsync();                 // connect to collection
await list.ReadyTask;                     // wait for initial sync
// OR: await list.ConnectAsync();         // combines both above

await list.AddAsync(1);
await list.InsertAsync(0, 2);
await list.RemoveAsync(1);
await list.RemoveAtAsync(0);
await list.ReplaceAsync(0, 99);
await list.MoveAsync(0, 1);
await list.ClearAsync();

foreach (var item in list) { }           // read local copy
Console.WriteLine(list.State);           // Disconnected, Connecting, Connected
await list.DisabledTask;                  // fires on disconnect
await list.DisableAsync();
```

### Collection Relay Mode

For hierarchical distribution (master → relay → clients).

```csharp
// Master server interface
public interface IMasterServerNexus {
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<Data> Items { get; }
}

// Relay server interface
public interface IRelayServerNexus {
    [NexusCollection(NexusCollectionMode.Relay)]
    INexusList<Data> Items { get; }
}

// Relay server setup
var masterPool = new NexusClientPool<MasterClient, MasterClient.ServerProxy>(poolConfig);
var relayServer = RelayServerNexus.CreateServer(config, () => new RelayServerNexus(),
    cfg => {
        var connector = masterPool.GetCollectionConnector(p => p.Items);
        cfg.Context.Collections.Items.ConfigureRelay(connector);
    });
```

Relay is read-only, auto-reconnects to parent, maintains full state sync.

## ASP.NET Integration

Requires `NexNet.Asp`.

```csharp
// Server setup
builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

app.UseAuthentication();
app.UseAuthorization();

await app.UseHttpSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c => {
    c.Path = "/nexus";
    c.AspEnableAuthentication = true;
    c.AspAuthenticationScheme = "BearerToken";
}).StartAsync(app.Lifetime.ApplicationStopped);
// OR: UseWebSocketNexusServerAsync for WebSocket
```

## Versioning

Server-only. For backward-compatible API evolution.

```csharp
[NexusVersion(Version = "v1.0", HashLock = -2031775281)]
public interface IServerV1 {
    [NexusMethod(1)]
    ValueTask<bool> GetStatus();
}

[NexusVersion(Version = "v2.0", HashLock = -1210855623)]
public interface IServerV2 : IServerV1 {
    [NexusMethod(2)]
    ValueTask<string> GetInfo();
}

// V2 server supports V1 and V2 clients
[Nexus<IServerV2, IClient>(NexusType = NexusType.Server)]
public partial class ServerV2 { ... }
```

Rules: All methods need `[NexusMethod(id)]` with unique ID. `HashLock` prevents accidental API changes. V1 client on V2 server can only call V1 methods.

## CancellationToken

```csharp
// Must be last parameter
ValueTask Operation(int data, CancellationToken ct);

// Usage
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await client.Proxy.Operation(42, cts.Token);
```
