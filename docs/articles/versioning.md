# Versioning

NexNet supports interface versioning for server nexus types, allowing clients with older interface versions to connect to servers running newer versions. This enables backward-compatible API evolution without breaking existing clients.

## Overview

Versioning works by building a hierarchy of interfaces using inheritance. Each version is decorated with `[NexusVersion]`, and each method gets a stable ID via `[NexusMethod]`. A server implementing the latest version can accept clients targeting any prior version in the hierarchy.

Versioning is a server-only feature — client nexus interfaces cannot be versioned.

## Defining Versioned Interfaces

### 1. Decorate with NexusVersion

```csharp
[NexusVersion(Version = "v1.0", HashLock = 1408991834)]
public interface IServerNexusV1
{
    [NexusMethod(1)]
    ValueTask<bool> GetStatus();
}

[NexusVersion(Version = "v2.0", HashLock = 325983114)]
public interface IServerNexusV2 : IServerNexusV1
{
    [NexusMethod(2)]
    ValueTask<string> GetServerInfo();
}
```

### 2. Assign Stable Method IDs

Every method and `[NexusCollection]` property must have a `[NexusMethod]` attribute with a unique ID. Once set, these IDs must not change.

### 3. Use HashLock for Stability

The `HashLock` property ensures an interface cannot be changed unintentionally after release. If any arguments, return values, or method types are modified, or the structure of a serialized type used by a parameter, a return value or a collection changes, the source generator emits a compile error.

During development, omit `HashLock` so you can iterate freely. Set it when the API is ready for release.

### How Serialized Types Are Hashed

The hash describes what goes on the wire, and only that. Everything that travels is hashed structurally:

- the parameter types of each method;
- the return kind (`void`, `ValueTask` or `ValueTask<T>`) and, for `ValueTask<T>`, the type `T`;
- the kind of each `[NexusCollection]` and its item type.

For every [`[NexusObject]`](serialization.md) type reachable from one of these, the hash covers:

- each member's `[NexusKey]` value and member type, in key order (members with `[NexusIgnore]` are excluded);
- whether the type is a class or a struct;
- for unions, every `[NexusUnion<T>(tag)]` tag and case type, in tag order.

Enums are hashed by their underlying type and their values. `Nullable<T>` (`int?`) is hashed, because it changes what a reader accepts. Built-in, CLR and custom-formatter types (`int`, `string`, `Guid`, `List<T>`, a type with an `[assembly: NexusFormatter]`) are hashed by their .NET identity: full type name plus type arguments, so changing `int` to `long` or `List<T>` to `T[]` changes the hash.

Names never travel on the wire, so they are not hashed: not the names of `[NexusObject]` types, unions, members or enum members. Reference-type nullability (`string?`, `Customer?`) is not hashed either, because it does not change the bytes.

As a consequence, **renaming a DTO, a member or an enum member keeps the `HashLock`**, and so does adding or removing `?` on a reference type. **Changing keys, member types, enum values or the structure of a returned type changes it**: adding, removing or renumbering a key, or changing a keyed member's type, on a type used by a released version produces a compile error until the `HashLock` is updated. Because clients present the hash of their version when they connect, updating it also means clients built against the old contract can no longer connect to that version.

Adding a member with a new, previously unused key is the compatible way to extend a type at the serialization level: older readers skip the extra element, and newer readers leave the member at its default when an older peer omits it. Never reuse or renumber a key. To extend data used by an already released version, add the new member to a new type, or to a type only used by the new version, and expose it through methods in a new version interface. See [Serialization](serialization.md#evolving-types).

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
4. `[NexusObject]` types used by parameters, return values or collections of a released version must keep their keys and member types
5. `HashLock` is strongly recommended for released interfaces but optional during development

## See Also

- [Hub Invocations](hub-invocations.md) — Method patterns and return types
- [Getting Started](getting-started.md) — Basic nexus setup without versioning
- [Serialization](serialization.md) — `[NexusObject]` keys and evolving types
