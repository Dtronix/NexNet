# Versioning

NexNet supports interface versioning for server nexus types, allowing clients with older interface versions to connect to servers running newer versions. This enables backward-compatible API evolution without breaking existing clients.

## Overview

Versioning works by building a hierarchy of interfaces using inheritance. Each version is decorated with `[NexusVersion]`, and each method gets a stable ID via `[NexusMethod]`. A server implementing the latest version can accept clients targeting any prior version in the hierarchy.

Versioning is a server-only feature — client nexus interfaces cannot be versioned.

## Defining Versioned Interfaces

### 1. Decorate with NexusVersion

```csharp
[NexusVersion(Version = "v1.0", HashLock = -2031775281)]
public interface IServerNexusV1
{
    [NexusMethod(1)]
    ValueTask<bool> GetStatus();
}

[NexusVersion(Version = "v2.0", HashLock = -1210855623)]
public interface IServerNexusV2 : IServerNexusV1
{
    [NexusMethod(2)]
    ValueTask<string> GetServerInfo();
}
```

### 2. Assign Stable Method IDs

Every method and `[NexusCollection]` property must have a `[NexusMethod]` attribute with a unique ID. Once set, these IDs must not change.

### 3. Use HashLock for Stability

The `HashLock` property ensures an interface cannot be changed unintentionally after release. If any arguments, MemoryPack members (including union changes), return values, or method types are modified, the source generator emits a compile error.

During development, omit `HashLock` so you can iterate freely. Set it when the API is ready for release.

## Server Implementation

Implement the latest version interface. The server automatically accepts clients targeting any version in the hierarchy:

```csharp
[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus
{
    public ValueTask<bool> GetStatus()
        => ValueTask.FromResult(true);

    public ValueTask<string> GetServerInfo()
        => ValueTask.FromResult("Server v2.0");
}
```

## Client Versions

Clients target a specific version of the server interface:

```csharp
// V1 client — can only call V1 methods
[Nexus<IClientNexus, IServerNexusV1>(NexusType = NexusType.Client)]
public partial class ClientV1
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}

// V2 client — can call V1 and V2 methods
[Nexus<IClientNexus, IServerNexusV2>(NexusType = NexusType.Client)]
public partial class ClientV2
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}
```

## Usage

```csharp
var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());
await server.StartAsync();

// V1 client connects — can call GetStatus() only
var clientV1 = ClientV1.CreateClient(clientConfig, new ClientV1());
var result = await clientV1.TryConnectAsync();
if (result.Success)
    Console.WriteLine(await clientV1.Proxy.GetStatus());

// V2 client connects — can call both methods
var clientV2 = ClientV2.CreateClient(clientConfig, new ClientV2());
var result2 = await clientV2.TryConnectAsync();
if (result2.Success)
{
    Console.WriteLine(await clientV2.Proxy.GetStatus());
    Console.WriteLine(await clientV2.Proxy.GetServerInfo());
}
```

## Security Features

Versioning includes runtime enforcement to prevent unauthorized method access:

- All invoked methods are validated against the client's declared version capabilities
- Servers maintain a `VersionMethodHashSet` for valid method+version combinations
- If a client tries to invoke a method outside its declared version, it is immediately disconnected with a `ProtocolError`
- Connection establishment includes invocation hash verification for compatibility
- Method IDs combined with version hashes create unique identifiers for each version+method combination
- The source generator creates optimized lookup tables with minimal performance overhead

## Rules and Caveats

1. All versioned interfaces must have a version string, which is used during connection
2. All methods and `[NexusCollection]` properties must have `[NexusMethod]` with a unique ID
3. Methods and collections must not be changed in an interface after setting a version
4. `HashLock` is strongly recommended for released interfaces but optional during development

## See Also

- [Hub Invocations](hub-invocations.md) — Method patterns and return types
- [Getting Started](getting-started.md) — Basic nexus setup without versioning
