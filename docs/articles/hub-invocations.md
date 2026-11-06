# Hub Invocations

NexNet supports several method invocation patterns between server and client. The source generator handles all proxy creation — you define interfaces, and the generator emits the invocation infrastructure.

## Return Types

Nexus interface methods support three return types:

| Return Type | Behavior |
|-------------|----------|
| `void` | Fire-and-forget. The caller does not wait for completion. Use for notifications. |
| `ValueTask` | The caller awaits completion but receives no return value. |
| `ValueTask<T>` | The caller awaits and receives a return value from the remote method. |

The `ValueTask` a call returns follows the usual `ValueTask` rules: consume it once, by awaiting it, blocking on it, or converting it with `AsTask()`. It is backed by pooled state that is reused once the result has been read, so consuming it a second time throws `InvalidOperationException`. Call `AsTask()` first if the result is needed more than once.

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

|                    | CancellationToken | INexusDuplexPipe | INexusDuplexChannel&lt;T&gt; | Args |
|--------------------|:-----------------:|:----------------:|:----------------------:|:----:|
| `void`             |                   |                  |                        | X    |
| `ValueTask`        | X                 |                  |                        | X    |
| `ValueTask`        |                   | X                | X                      | X    |
| `ValueTask<T>`     | X                 |                  |                        | X    |

**Rules:**
- `CancellationToken` cannot be combined with `INexusDuplexPipe` or `INexusDuplexChannel<T>` because pipes and channels have built-in cancellation/completion notifications.
- `CancellationToken` must be the last parameter, following standard .NET conventions.
- Arguments and results may each be up to `ConfigBase.MaxMessageSize` (4 MiB by default, the smaller of both sides' settings). Values larger than one 64 KiB frame are streamed automatically. A larger value fails the call with `NexusMessageTooLargeException`, and a value the receiver has no memory for right now fails it with `NexusBusyException`; the session stays connected in both cases. For larger or continuous data, use [Duplex Pipes](duplex-pipes.md) or [Channels](channels.md).
- The receiving side queues a limited number of invocations per session (`ConfigBase.InvocationCredits`) and runs `MaxConcurrentConnectionInvocations` of them at a time. When the queue is full, a call waits until a slot frees up. See [Limits and Flow Control](limits-and-flow-control.md).
- Argument and return types must be serializable: built-in types, `[NexusObject]` types, or types with a registered formatter. See [Serialization](serialization.md).

## Cancellation

Methods that return `ValueTask` or `ValueTask<T>` can accept a `CancellationToken` as the last parameter:

```csharp
public interface IServerNexus
{
    ValueTask<byte[]> DownloadFile(string path, CancellationToken ct);
}
```

Cancelling the token on the caller side propagates the cancellation to the remote method. Each call is canceled individually, also when several calls to the same method are running. A remote method that throws `OperationCanceledException` on its own, with or without a token, fails the call with `TaskCanceledException`.

When a method is invoked without waiting for its result (a broadcast, or a `ValueTask` method invoked on several clients), the caller can't cancel it. Its token is canceled when the session ends.

## Errors

When a `ValueTask` or `ValueTask<T>` method throws on the side that runs it, the caller's call fails with `ProxyRemoteInvocationException`. The executing side logs the exception; by default nothing about it is sent, and the exception's message is `Exception occurred while executing method on proxy.`

What is sent is decided by the side that runs the method: a server's configuration controls what its clients see, a client's what the server sees when it calls the client.

- `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's full type name and its message.
- Overriding `OnInvocationException` on the nexus sends an application error instead, whatever the setting says. Return null to fall back to the setting. An exception thrown by the hook is logged and treated as null.

```csharp
partial class ServerNexus
{
    protected override NexusRemoteError? OnInvocationException(Exception exception, int methodId)
        => exception switch
        {
            KeyNotFoundException => new NexusRemoteError(404, "Not found."),
            _ => null
        };
}
```

On the caller, the exception exposes what was sent:

| Property | Value |
|----------|-------|
| `RemoteErrorCode` | The hook's code, or null |
| `RemoteExceptionType` | The exception's full type name with `TypeAndMessage`, else null |
| `RemoteMessage` | The exception's message with `TypeAndMessage`, the hook's message, or null |

The type is truncated to 256 characters and the message to 2048. A detail that is malformed or longer than that is ignored, and the caller gets the plain exception. Fire-and-forget (`void`) methods, broadcasts, and pipe and channel methods send nothing. Canceled and unauthorized calls are unchanged (`TaskCanceledException`, `ProxyUnauthorizedException`). A peer without this feature ignores the detail.

> [!WARNING]
> `TypeAndMessage` can reveal internal details (file paths, connection strings, user data) to the peer. With untrusted peers, leave it at `None` and return application error codes from `OnInvocationException`.

## Execution Context

Invoked methods run on the thread pool, in the execution context of the code that started the session: on a client the code that called `ConnectAsync`, on a server the code that called `StartAsync` or, with the ASP.NET Core transports, the upgrade request. Methods therefore see its `AsyncLocal` values, such as `IHttpContextAccessor.HttpContext` (when registered with `AddHttpContextAccessor`), `Activity.Current`, logging scopes and `CultureInfo.CurrentCulture`. Each invocation runs in its own copy, so values one method sets are not seen by other invocations.

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

A broadcast is written straight away to every client that can take it; clients that are behind are waited for together, for at most `ConfigBase.ProgressTimeout`, so a slow client does not delay the others. See [Limits and Flow Control](limits-and-flow-control.md#invocations).

## See Also

- [Sessions & Lifetimes](sessions-and-lifetimes.md) — Session groups and broadcasting targets
- [Duplex Pipes](duplex-pipes.md) — Streaming large data that exceeds the argument size limit
- [Channels](channels.md) — Type-safe data streaming
- [Serialization](serialization.md) — Making argument and return types serializable
