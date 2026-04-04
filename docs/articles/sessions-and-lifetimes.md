# Sessions & Lifetimes

NexNet creates a new hub instance for each client session. Understanding the lifecycle helps you manage state correctly and use features like session groups for targeted broadcasting.

## Hub Instance Lifecycle

A new nexus instance is created for each client that connects. The instance remains active for the duration of the session and is automatically disposed when the session ends — whether from client disconnection, error, or timeout.

Because nexus instances may be created and disposed temporarily (for example, by `NexusServer<>.ContextProvider`), avoid performing calculations or side effects in constructors.

Each session gets its own nexus instance. Data is not shared between sessions through the nexus class itself. For shared state, use external services or synchronized collections.

## Session Groups

Sessions can be added to named groups for targeted broadcasting. Group membership is managed through the `Context.Groups` API within server nexus methods:

```csharp
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public async ValueTask JoinRoom(string roomId)
    {
        // Add this session to a group
        await Context.Groups.AddAsync($"room-{roomId}");
    }

    public async ValueTask Setup()
    {
        // Add to multiple groups at once
        await Context.Groups.AddAsync(["lobby", "players"]);

        // Remove from a group
        await Context.Groups.RemoveAsync("lobby");

        // Get all group names for this session
        var groups = await Context.Groups.GetNamesAsync();
    }
}
```

### Broadcasting to Groups

```csharp
// Send to all members of a group
await Context.Clients.Group("room-123").SendMessage("Hello room!");

// Send to members of multiple groups
await Context.Clients.Groups(["lobby", "players"]).SendMessage("Hello all!");
```

Groups are automatically cleaned up when sessions disconnect. You don't need to manually remove sessions from groups on disconnection.

## Ping and Keep-Alive

NexNet includes a built-in ping system to detect connection timeouts from both the client and server side. Ping messages are sent periodically, and if a response is not received within the configured timeout, the connection is considered lost.

## Automatic Reconnection

Clients automatically reconnect when a connection is lost due to timeout or socket disconnection. This behavior is built into the client and requires no additional configuration for basic usage.

During reconnection:
- The client detects the connection loss
- A new connection is established to the server
- A new session and nexus instance are created on the server side
- The client can resume invoking methods on the proxy

## See Also

- [Hub Invocations](hub-invocations.md) — Method patterns and broadcasting
- [Synchronized Collections](synchronized-collections.md) — Data sync across sessions
- [Authentication](authentication.md) — Securing session connections
