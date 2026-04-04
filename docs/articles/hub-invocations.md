# Hub Invocations

NexNet supports several method invocation patterns between server and client. The source generator handles all proxy creation — you define interfaces, and the generator emits the invocation infrastructure.

## Return Types

Nexus interface methods support three return types:

| Return Type | Behavior |
|-------------|----------|
| `void` | Fire-and-forget. The caller does not wait for completion. Use for notifications. |
| `ValueTask` | The caller awaits completion but receives no return value. |
| `ValueTask<T>` | The caller awaits and receives a return value from the remote method. |

```csharp
public interface IServerNexus
{
    void Notify(string message);                    // Fire-and-forget
    ValueTask UpdateAndWait(int id, string data);   // Await completion
    ValueTask<int> GetStatus(int userId);           // Await with result
}
```

## Method Invocation Compatibility

Some argument types have restrictions on how they can be combined. The source generator enforces these rules with compile-time diagnostics.

|                    | CancellationToken | INexusDuplexPipe | INexusChannel&lt;T&gt; | Args |
|--------------------|:-----------------:|:----------------:|:----------------------:|:----:|
| `void`             |                   |                  |                        | X    |
| `ValueTask`        | X                 |                  |                        | X    |
| `ValueTask`        |                   | X                | X                      | X    |
| `ValueTask<T>`     | X                 |                  |                        | X    |

**Rules:**
- `CancellationToken` cannot be combined with `INexusDuplexPipe` or `INexusChannel<T>` because pipes and channels have built-in cancellation/completion notifications.
- `CancellationToken` must be the last parameter, following standard .NET conventions.
- The total serialized argument size cannot exceed 65,535 bytes. For larger data, use [Duplex Pipes](duplex-pipes.md) or [Channels](channels.md).

## Cancellation

Methods that return `ValueTask` or `ValueTask<T>` can accept a `CancellationToken` as the last parameter:

```csharp
public interface IServerNexus
{
    ValueTask<byte[]> DownloadFile(string path, CancellationToken ct);
}
```

Cancelling the token on the caller side propagates the cancellation to the remote method.

## Server Broadcasting

The server can invoke methods on multiple connected clients with a single call through the session context:

```csharp
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public async ValueTask BroadcastStatus(int status)
    {
        // Send to all connected clients
        await Context.Clients.All.OnStatusChanged(status);

        // Send to a specific group
        await Context.Clients.Group("room-1").OnStatusChanged(status);
    }
}
```

## See Also

- [Sessions & Lifetimes](sessions-and-lifetimes.md) — Session groups and broadcasting targets
- [Duplex Pipes](duplex-pipes.md) — Streaming large data that exceeds the argument size limit
- [Channels](channels.md) — Type-safe data streaming
