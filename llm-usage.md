# NexNet Usage Reference

High-performance .NET 10 async networking. Bidirectional server-client communication. Source-generated, Native AOT compatible. Built-in MessagePack serializer (no external serializer dependency).

## Packages
```xml
<PackageReference Include="NexNet" Version="0.17.0" />
<PackageReference Include="NexNet.Generator" Version="0.17.0">
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

CancellationToken must be last. Max serialized args: 65,526 bytes per invocation (use pipes or channels for larger).

## Serialization

NexNet ships its own MessagePack serializer (namespace `NexNet.Serialization`); there is no external serializer package.
The generator emits a formatter for every `[NexusObject]` type reachable from a nexus (method parameters, return values,
channel and collection item types) into one `NexNet.Formatters.g.cs` per assembly, registered by a module initializer.
No reflection; Native AOT and trimming safe.

### Attributes

| Attribute | Target | Meaning |
|---|---|---|
| `[NexusObject]` | class, struct, record, abstract class, interface | Generate a formatter for this type |
| `[NexusKey(n)]` | property, field | Array position `n` (>= 0) on the wire |
| `[NexusIgnore]` | property, field | Not serialized |
| `[NexusConstructor]` | constructor | Constructor used to deserialize (parameters matched to members by name, case-insensitive) |
| `[NexusUnion<T>(tag)]` | abstract class, interface | Union case `T` with a `ushort` tag; one attribute per case |

```csharp
using NexNet.Serialization;

[NexusObject]
public class Order
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Customer { get; set; }
    [NexusKey(2)] public List<OrderLine> Lines { get; set; } = new();
    [NexusIgnore] public decimal CachedTotal { get; set; }
}

[NexusObject]
public readonly record struct OrderLine([property: NexusKey(0)] string Sku, [property: NexusKey(1)] int Quantity);

// Polymorphism: abstract base or interface with tagged cases
[NexusObject]
[NexusUnion<Circle>(0)]
[NexusUnion<Square>(1)]
public abstract class Shape { }
[NexusObject] public class Circle : Shape { [NexusKey(0)] public double Radius { get; set; } }
[NexusObject] public class Square : Shape { [NexusKey(0)] public double Side { get; set; } }
```

Rules:
- Every public instance field/property of a `[NexusObject]` type needs `[NexusKey]` or `[NexusIgnore]`.
- Non-public members may carry `[NexusKey]` (set through generated accessors), except on generic types.
- Construction: `[NexusConstructor]` if present; else an accessible parameterless constructor; else the single
  accessible constructor. `init` and `required` members are supported.
- Wire shape: array of length `maxKey + 1`; element `n` is the member with key `n`; unused keys are nil. Keep keys dense.

### Evolving types

- Add a member with a **new** key. Old readers skip the extra element; new readers leave it at its default when an old
  peer omits it.
- Never reuse or renumber a key, and never change a keyed member's type. Retire a member by removing it and leaving its
  key unused.
- The keys, member types and union tags of `[NexusObject]` types reachable from method parameters are part of the nexus
  hash (and `HashLock`). Peers compare these hashes when connecting, so such a change still requires both sides to be
  rebuilt; for released versions add new types and methods in a new version interface (see Versioning).

### Types used only at runtime / third-party types

```csharp
// A type that never appears in a nexus signature, e.g. used only with CreateChannel<T>() / GetChannel<T>()
[assembly: NexusSerializable<List<Order>>]

// A type you cannot annotate: register a formatter (public parameterless constructor required)
[assembly: NexusFormatter<PointFormatter, ThirdParty.Point>]

public sealed class PointFormatter : NexusFormatter<ThirdParty.Point>
{
    public override void Serialize(ref MsgPackWriter writer, ThirdParty.Point? value)
    {
        if (value is null) { writer.WriteNil(); return; }
        writer.WriteArrayHeader(2);
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ThirdParty.Point? value)
    {
        if (reader.TryReadNil()) { value = null; return; }
        reader.ReadArrayHeader(2);   // requires exactly 2 elements
        value = new ThirdParty.Point(reader.ReadInt32(), reader.ReadInt32());
    }
}
```

A formatter writes and reads exactly one MessagePack value. Standalone use: `NexusSerializer.Serialize<T>(value)` and
`NexusSerializer.Deserialize<T>(bytes, options)`.

### Built-in types

| .NET type | Encoding |
|---|---|
| `bool`, integers, `char`, `float`, `double`, `Half` | MessagePack bool/int/float (smallest form) |
| `string`, `Uri`, `Version` | str (nil for null) |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>` | bin |
| Arrays, `List<T>`, `Memory<T>`, `ReadOnlyMemory<T>` of `sbyte, short, ushort, int, uint, long, ulong, float, double, char, Half` | ext 78 (bulk little-endian) |
| Other `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`, `HashSet<T>`, `Queue<T>`, `Stack<T>` | array |
| `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` | map |
| `Nullable<T>` | nil or `T` |
| enums | underlying integer |
| `DateTime` (keeps `Kind`), `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly` | int, or `[utcTicks, offsetMinutes]` for `DateTimeOffset` |
| `Guid`, `decimal`, `BigInteger` | bin |
| `ValueTuple<...>` and `Tuple<...>` (1 to 7 elements), `KeyValuePair<K,V>` | array |

Anything else needs `[NexusObject]` or `[assembly: NexusFormatter<TFormatter, T>]` (NEXNET028).

### Options and limits

`ConfigBase.SerializerOptions` (every client and server config) controls how data from the peer is deserialized:

| Value | Behavior |
|---|---|
| `NexusSerializerOptions.Untrusted` (default) | Max nesting depth 64, invalid UTF-8 rejected, randomized hashing for dictionaries/sets with non-string keys, a channel item may buffer at most 16 MiB |
| `NexusSerializerOptions.Trusted` | No depth limit, invalid UTF-8 replaced, default comparers |

Customize with `with`: `config.SerializerOptions = NexusSerializerOptions.Untrusted with { MaxBufferedItemSize = 64 * 1024 * 1024 };`
(properties: `Security`, `MaxDepth`, `StrictUtf8`, `MaxBufferedItemSize`). Length checks before allocation and exact
message shapes are always enforced.

### Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| NEXNET028 | Error | A type used by a nexus has no formatter (not `[NexusObject]`, no `NexusFormatter` registration, not built in) |
| NEXNET029 | Error | Duplicate or negative `[NexusKey]` |
| NEXNET030 | Error | Public member of a `[NexusObject]` type has neither `[NexusKey]` nor `[NexusIgnore]` |
| NEXNET031 | Error | Keyed member cannot be assigned during deserialization (no setter, init accessor, matching constructor parameter or backing field; or a non-public member on a generic type) |
| NEXNET032 | Error | No usable constructor (need an accessible parameterless one, a single accessible one, or one marked `[NexusConstructor]`) |
| NEXNET033 | Error | Union has a duplicate tag or a case type that does not derive from it |
| NEXNET034 | Error | Abstract or interface `[NexusObject]` type without `[NexusUnion<T>(tag)]` cases |
| NEXNET036 | Error | `[NexusObject]` type is not accessible to generated code (make it public, or internal with InternalsVisibleTo across assemblies) |
| NEXNET037 | Warning | More than 16 unused key positions, each written as nil |
| NEXNET038 | Error | `CreateChannel<T>`/`GetChannel<T>`/`GetChannelReader<T>`/`GetChannelWriter<T>` with a `T` that has no formatter; add `[NexusObject]` or `[assembly: NexusSerializable<T>]` |

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
| `AuthorizationCacheDuration` | null | Default auth cache TTL; null = disabled |

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

## Authorization

Declarative method/collection authorization via `[NexusAuthorize<TPermission>]`. Server-only. Permission enum must be backed by `int` (default).

```csharp
// 1. Define permission enum
public enum Permission { Read, Write, Admin }

// 2. Decorate methods on the server nexus class
[NexusAuthorize<Permission>(Permission.Admin)]
public ValueTask AdminMethod() { ... }

[NexusAuthorize<Permission>(Permission.Read, Permission.Write)]
public ValueTask MultiPermMethod() { ... }

[NexusAuthorize<Permission>()]  // marker-only: requires auth, no specific permission
public ValueTask AnyAuthMethod() { ... }

// 3. Decorate collections on the interface
public partial interface IServerNexus {
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<Permission>(Permission.Read)]
    INexusList<string> SecureItems { get; }
}

// 4. Override OnAuthorize on the server nexus
protected override ValueTask<AuthorizeResult> OnAuthorize(
    ServerSessionContext<ClientProxy> context, int methodId,
    string methodName, ReadOnlyMemory<int> requiredPermissions)
{
    // requiredPermissions contains int-cast enum values
    // Return: Allowed, Unauthorized (error to client), Disconnect (kill session)
    var user = context.Identity;
    return new(HasPermissions(user, requiredPermissions)
        ? AuthorizeResult.Allowed : AuthorizeResult.Unauthorized);
}

// 5. Client-side: catch ProxyUnauthorizedException
try { await client.Proxy.AdminMethod(); }
catch (ProxyUnauthorizedException) { /* denied */ }
```

Auth guard runs before deserialization. If `OnAuthorize` throws, session disconnects (fail-safe). Collections use `Disconnect` for unauthorized access since they lack a return channel.

### Authorization Caching

Opt-in TTL-based caching per session. Only `Allowed` and `Unauthorized` results are cached; `Disconnect` and exceptions are never cached.

```csharp
// Server-wide default (null = disabled)
serverConfig.AuthorizationCacheDuration = TimeSpan.FromSeconds(30);

// Per-method override via attribute (-1 = use server config, 0 = never cache, >0 = seconds)
[NexusAuthorize<Permission>(Permission.Read, CacheDurationSeconds = 60)]   // 60s override
[NexusAuthorize<Permission>(Permission.Admin, CacheDurationSeconds = 0)]   // never cache
[NexusAuthorize<Permission>(Permission.Write)]                              // use server default

// Explicit invalidation (inside nexus methods)
InvalidateAuthorizationCache();           // clear all cached results
InvalidateAuthorizationCache(methodId);   // clear single method
```

### Diagnostics

| ID | Description |
|---|---|
| NEXNET024 | `[NexusAuthorize]` on client nexus (server-only) |
| NEXNET025 | `[NexusAuthorize]` without `OnAuthorize` override |
| NEXNET026 | Mixed permission enum types across attributes |
| NEXNET027 | Permission enum not backed by `int` |

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

Thread-safe writing. `INexusDuplexChannel<T>`: each item is one MessagePack value (any serializable `T`). For bulk
numeric data use a primitive array item type (`int[]`, `float[]`, ...), encoded as ext 78, a raw little-endian block.
A `T` used only through `CreateChannel<T>()`/`GetChannel<T>()` needs a formatter (NEXNET038).

```csharp
// Interface
ValueTask StreamData(INexusDuplexChannel<int> channel);
// Client
await using var channel = client.CreateChannel<int>();
await client.Proxy.StreamData(channel);
var reader = await channel.GetReaderAsync();
await foreach (var item in reader) { }
// Server
public async ValueTask StreamData(INexusDuplexChannel<int> channel) {
    var writer = await channel.GetWriterAsync();
    await writer.WriteAsync(42);
    await writer.CompleteAsync();
}
// Extensions
await channel.WriteAndComplete(enumerable, chunkSize: 100);
var list = await reader.ReadUntilComplete(estimatedSize: 1000);
```

### Different types via pipe

```csharp
var pipe = client.CreatePipe();
await client.Proxy.StreamData(pipe);
await pipe.ReadyTask;
var writer = await pipe.GetChannelWriter<long>();
var reader = await pipe.GetChannelReader<string>();
// Also: GetChannel<T>
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

Server-only. All methods need `[NexusMethod(id)]` with unique IDs. `HashLock` prevents accidental changes. The hash covers
method signatures and the `[NexusObject]` types reachable from parameters (keys, member types, nullability, union tags; not
member names). Adding, removing or renumbering a key changes it.

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
