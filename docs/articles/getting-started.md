# Getting Started

NexNet is a .NET real-time asynchronous networking library for bidirectional server-client communication. All hubs and proxies are source-generated at compile time — no reflection, fully AOT compatible.

## Installation

Install via NuGet. You need two packages: the core library and the source generator.

```xml
<PackageReference Include="NexNet" Version="*" />
<PackageReference Include="NexNet.Generator" Version="*-*">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

Adding packages via the CLI:

```bash
dotnet add package NexNet
dotnet add package NexNet.Generator
```

### Optional Packages

| Package | Purpose |
|---------|---------|
| [`NexNet.Quic`](https://www.nuget.org/packages/NexNet.Quic) | QUIC transport (requires libmsquic on Linux) |
| [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) | ASP.NET Core integration for WebSocket and HttpSocket transports |

## Project Structure

NexNet projects follow a three-project pattern:

- **Shared** — Contains the nexus and proxy interfaces. Referenced by both client and server.
- **Server** — Implements the server nexus. References NexNet and the Shared project.
- **Client** — Implements the client nexus. References NexNet and the Shared project.

Both the Server and Client projects need the `NexNet.Generator` package. The Shared project only needs the core `NexNet` package.

## Define Interfaces

Start by defining the interfaces in the Shared project. Each side gets an interface: one for the server nexus and one for the client nexus.

```csharp
public interface IClientNexus
{
    ValueTask<string> GetUserName();
}

public interface IServerNexus
{
    void UpdateInfo(int userId, int status, string? customStatus);
    ValueTask UpdateInfoAndWait(int userId, int status, string? customStatus);
    ValueTask<int> GetStatus(int userId);
}
```

## Implement Nexus Classes

In the client and server projects, create partial classes decorated with the `[Nexus]` attribute. The source generator will produce the rest.

```csharp
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    public ValueTask<string> GetUserName()
    {
        return new ValueTask<string>("Bill");
    }
}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void UpdateInfo(int userId, int status, string? customStatus)
    {
        // Handle the data
    }

    public ValueTask UpdateInfoAndWait(int userId, int status, string? customStatus)
    {
        // Handle the data
        return default;
    }

    public ValueTask<int> GetStatus(int userId)
    {
        return new ValueTask<int>(1);
    }
}
```

The `[Nexus]` attribute takes two type parameters: the interface this nexus implements, and the interface of the remote proxy it communicates with.

## Create Server and Client

The source generator adds static `CreateServer` and `CreateClient` factory methods to each nexus class.

```csharp
var serverConfig = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
};

var clientConfig = new TcpClientConfig
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
};

var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());
var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

await server.StartAsync();
await client.ConnectAsync();
```

## Invoke Methods

Once connected, the client can invoke server methods through the generated proxy:

```csharp
await client.Proxy.UpdateInfoAndWait(1, 2, "Custom Status");
var status = await client.Proxy.GetStatus(42);
```

## Next Steps

- [Hub Invocations](hub-invocations.md) — Method patterns, return types, and broadcasting
- [Sessions & Lifetimes](sessions-and-lifetimes.md) — Hub lifecycle and session groups
- [Synchronized Collections](synchronized-collections.md) — Real-time data sync between server and clients
- [Duplex Pipes](duplex-pipes.md) — Streaming raw bytes
- [Channels](channels.md) — Type-safe data streaming
- [Authentication](authentication.md) — Token-based auth setup
- [Transports](transports.md) — Choosing the right transport
