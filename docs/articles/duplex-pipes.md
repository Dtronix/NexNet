# Duplex Pipes

Method arguments and results are limited to `ConfigBase.MaxMessageSize` (4 MiB by default) and are held in memory whole. For larger or continuous data transfers, NexNet provides built-in duplex pipe support via the `INexusDuplexPipe` argument, allowing bidirectional byte streaming between server and client.

## When to Use Duplex Pipes

Use duplex pipes when you need to:
- Transfer data larger than `MaxMessageSize`, or that should not be held in memory at once
- Stream data continuously (e.g., file transfers)
- Send and receive byte data simultaneously

For streaming typed data (classes, structs), consider using [Channels](channels.md) instead, which build on top of duplex pipes with type safety.

## Defining a Pipe Method

Add an `INexusDuplexPipe` parameter to a nexus interface method:

```csharp
public interface IServerNexus
{
    ValueTask UploadFile(string fileName, INexusDuplexPipe pipe);
}
```

Methods with `INexusDuplexPipe` must return `ValueTask`. They cannot return `void` or `ValueTask<T>`, and cannot use `CancellationToken` (pipes have built-in cancellation/completion notifications).

## Reading and Writing

The `INexusDuplexPipe` provides `Input` and `Output` properties based on `System.IO.Pipelines`:

```csharp
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public async ValueTask UploadFile(string fileName, INexusDuplexPipe pipe)
    {
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            var buffer = result.Buffer;

            // Process the received bytes
            foreach (var segment in buffer)
            {
                // Write to file, etc.
            }

            pipe.Input.AdvanceTo(buffer.End);

            if (result.IsCompleted)
                break;
        }
    }
}
```

## Thread Safety

As with `System.IO.Pipelines`, the `INexusDuplexPipe` is **not thread safe**. You are responsible for ensuring that member calls do not overlap. Do not read and write from multiple threads simultaneously without synchronization.

## Flow Control

Each direction of a pipe has a receive window (`ConfigBase.PipeWindow`, 256 KiB by default, 1 MiB with trusted serializer options). A writer only sends what the reader has granted, and waits in its own `FlushAsync` for more; the reader grants credit back as it consumes data. A slow reader therefore slows only its own pipe: other pipes, invocations and pings on the session are unaffected. Pass a `CancellationToken` to `WriteAsync`/`FlushAsync` to stop waiting.

Each session holds at most `ConfigBase.MaxPipes` pipes (32 by default), and every open pipe's window is reserved from the session budget (`ConfigBase.SessionBudget`). Opening a pipe beyond either limit fails with `NexusBusyException` (from `CreatePipe`, or from `ReadyTask` when the remote side refuses it); the session stays connected.

If you frame your own messages on a raw pipe, a message must fit in the window. Channels handle larger items themselves.

A pipe has at most one window in flight, so over a link with a long round-trip time its throughput is limited to the window divided by the round-trip time. See [Limits and Flow Control](limits-and-flow-control.md) for sizing the window and the session budget.

## See Also

- [Channels](channels.md) — Type-safe streaming built on top of duplex pipes
- [Limits and Flow Control](limits-and-flow-control.md) — Windows, session budget and timeouts
- [Hub Invocations](hub-invocations.md) — Method compatibility table for pipe arguments
