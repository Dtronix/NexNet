# Duplex Pipes

NexNet has a limitation where the total serialized argument size cannot exceed 65,535 bytes. For larger data transfers, NexNet provides built-in duplex pipe support via the `INexusDuplexPipe` argument, allowing bidirectional byte streaming between server and client.

## When to Use Duplex Pipes

Use duplex pipes when you need to:
- Transfer data larger than the 65,535 byte argument limit
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

## Congestion Control

NexNet duplex pipes include built-in congestion control. When the receiving side cannot process data fast enough, backpressure is applied to the sender automatically through the pipeline flow control mechanism.

## See Also

- [Channels](channels.md) — Type-safe streaming built on top of duplex pipes
- [Hub Invocations](hub-invocations.md) — Method compatibility table for pipe arguments
