# Channels

Building on the [Duplex Pipes](duplex-pipes.md) infrastructure, NexNet provides a typed channel interface for streaming structured data between server and client. Channels handle serialization and deserialization automatically.

## Channel Type

| Interface | Item types | Thread Safety |
|-----------|------------|---------------|
| `INexusDuplexChannel<T>` | Any type NexNet can serialize: [built-in types](serialization.md#built-in-types), `[NexusObject]` types and types with a registered formatter | Writing is thread safe; reading should be single-threaded |

Each item is written as one MessagePack value, and items are sent back to back on the channel's pipe. An item can be larger than a single pipe frame; the reader waits until the whole item has arrived. A single incomplete item may buffer at most `NexusSerializerOptions.MaxBufferedItemSize` bytes (16 MiB by default, see [Serialization](serialization.md#options-and-limits)).

For bulk numeric data, send primitive arrays (`int[]`, `float[]`, `double[]`, ...) as items. They are encoded as one compact block of raw little-endian values instead of one MessagePack value per element.

## Defining a Channel Method

Add a channel parameter to a nexus interface method:

```csharp
public interface IServerNexus
{
    ValueTask StreamData(INexusDuplexChannel<ComplexMessage> channel);
    ValueTask StreamIntegers(INexusDuplexChannel<int> channel);
}
```

Like duplex pipes, channel methods must return `ValueTask` and cannot use `CancellationToken` (channels have built-in cancellation/completion notifications).

## Creating Channels

Channels are acquired through the client or session context:

```csharp
// On the client
var channel = client.CreateChannel<MyMessage>();

// On the server (inside a nexus method)
var channel = Context.CreateChannel<MyMessage>();
```

If a channel instance is created, it should be disposed to release held resources.

## Item Types and NEXNET038

The generator creates formatters for every type that appears in a nexus method signature, so `INexusDuplexChannel<ComplexMessage>` parameters need no extra work as long as `ComplexMessage` is a `[NexusObject]` type.

When a type is only used at runtime, through `CreateChannel<T>()`, `GetChannel<T>()`, `GetChannelReader<T>()` or `GetChannelWriter<T>()`, the generator may not see it. The analyzer reports **NEXNET038** at the call when `T` has no formatter. Fix it by marking the type with `[NexusObject]` or by declaring it at assembly level:

```csharp
[assembly: NexusSerializable<List<MyMessage>>]
```

Built-in types and non-generic `[NexusObject]` types are always registered.

## Reading with IAsyncEnumerable

The preferred method of reading channels is using `IAsyncEnumerable` on the `INexusChannelReader`. This provides efficient buffering and simplifies handling of channel closure, whether graceful or not:

```csharp
// Given an INexusDuplexPipe from a method argument
var writer = await duplexPipe.GetChannelWriter<int>();
await foreach (var msg in await duplexPipe.GetChannelReader<ComplexMessage>())
{
    // Process each message
}
```

A pipe can carry different item types in each direction, as in this example.

## Extension Methods

Several extension methods simplify reading and writing entire collections:

### WriteAndComplete

Writes a collection to a channel and signals completion:

```csharp
// Write to INexusDuplexChannel<T> or INexusChannelWriter<T>
await channel.WriteAndComplete(items, chunkSize: 100);
```

The optional `chunkSize` parameter controls how many items are written per batch.

### ReadUntilComplete

Reads all items from a channel until the sender signals completion:

```csharp
// Read from INexusDuplexChannel<T> or INexusChannelReader<T>
var items = await channel.ReadUntilComplete(estimatedSize: 1000);
```

The optional `estimatedSize` parameter pre-allocates collection capacity to reduce resizing during reads.

## See Also

- [Duplex Pipes](duplex-pipes.md) — Low-level byte streaming
- [Serialization](serialization.md) — Attributes, built-in types and limits
- [Hub Invocations](hub-invocations.md) — Method compatibility table for channel arguments
