# Channels

Building on the [Duplex Pipes](duplex-pipes.md) infrastructure, NexNet provides typed channel interfaces for streaming structured data between server and client. Channels handle serialization and deserialization automatically.

## Channel Types

NexNet offers two channel interfaces, each optimized for different data types:

| Interface | Best For | Thread Safety |
|-----------|----------|---------------|
| `INexusDuplexChannel<T>` | Any type serializable by [MemoryPack](https://github.com/Cysharp/MemoryPack#built-in-supported-types) | Writing is thread safe; reading should be single-threaded |
| `INexusDuplexUnmanagedChannel<T>` | [Unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types) (primitives, simple structs) | Writing is thread safe; reading should be single-threaded |

Always prefer `INexusDuplexUnmanagedChannel<T>` when working with unmanaged types — it is fine-tuned for performance of simple types.

## Defining a Channel Method

Add a channel parameter to a nexus interface method:

```csharp
public interface IServerNexus
{
    ValueTask StreamData(INexusChannel<ComplexMessage> channel);
    ValueTask StreamIntegers(INexusChannel<int> channel);
}
```

Like duplex pipes, channel methods must return `ValueTask` and cannot use `CancellationToken` (channels have built-in cancellation/completion notifications).

## Creating Channels

Channels are acquired through the client or session context:

```csharp
// On the client
var channel = client.CreateChannel<MyMessage>();
var unmanagedChannel = client.CreateUnmanagedChannel<int>();

// On the server (inside a nexus method)
var channel = Context.CreateChannel<MyMessage>();
var unmanagedChannel = Context.CreateUnmanagedChannel<int>();
```

If a channel instance is created, it should be disposed to release held resources.

## Reading with IAsyncEnumerable

The preferred method of reading channels is using `IAsyncEnumerable` on the `INexusChannelReader`. This provides efficient buffering and simplifies handling of channel closure, whether graceful or not:

```csharp
var writer = await pipe.GetUnmanagedChannelWriter<int>();
await foreach (var msg in await pipe.GetChannelReader<ComplexMessage>())
{
    // Process each message
}
```

## Extension Methods

Several extension methods simplify reading and writing entire collections:

### WriteAndComplete

Writes a collection to a channel and signals completion:

```csharp
// Write to INexusDuplexChannel<T> or INexusChannelWriter<T>
await channel.WriteAndComplete(items, batchSize: 100);
```

The optional `batchSize` parameter controls how many items are sent per batch for optimized transmission.

### ReadUntilComplete

Reads all items from a channel until the sender signals completion:

```csharp
// Read from INexusDuplexChannel<T> or INexusChannelReader<T>
var items = await channel.ReadUntilComplete<MyMessage>(initialSize: 1000);
```

The optional `initialSize` parameter pre-allocates collection capacity to reduce resizing during reads.

## See Also

- [Duplex Pipes](duplex-pipes.md) — Low-level byte streaming
- [Hub Invocations](hub-invocations.md) — Method compatibility table for channel arguments
