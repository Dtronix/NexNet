# NexNet v0.10.0

*Released 2025-06-17*

This release introduces a foundational set of features for working with server-synchronized collections in Nexus. The update includes a new abstraction for remotely managed collections, a concrete implementation for list-type collections, and supporting infrastructure for future collection types like dictionaries and sets.

### New Public API
```cs
public interface INexusList<T> : INexusCollection, IEnumerable<T>{
    int Count { get; }
    bool IsReadOnly { get; }
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    int IndexOf(T item);
    Task<bool> ClearAsync();
    Task<bool> InsertAsync(int index, T item);
    Task<bool> RemoveAtAsync(int index);
    Task<bool> RemoveAsync(T item);
    Task<bool> AddAsync(T item);
    Task<bool> MoveAsync(int fromIndex, int toIndex);
    Task<bool> ReplaceAsync(int index, T value);
    T this[int index] { get; }
}

public interface INexusCollection : IEnumerable
{    
    public NexusCollectionState State { get; }
    public Task DisconnectedTask { get; }
    public Task<bool> ConnectAsync(CancellationToken token = default);
    public Task  DisconnectAsync();
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed { get; }
}

public class NexusCollectionAttribute : Attribute
{
    public ushort Id { get; init; } = 0;
    public NexusCollectionMode Mode { get; init; } = NexusCollectionMode.ServerToClient;
    public bool Ignore { get; init; }
}

public enum NexusCollectionMode
{
    Unset,
    ServerToClient,
    BiDrirectional
}
```
The *Async methods all execute the changes on the server and wait for the server confirmation of the change operation.  If the Task returns false, the value was not updated on the server.  This may indicate that the list was cleared or the operation was rendered a noop.

### Synchronized List Usage
The list must be decorated with the NexusCollectionAttribute, otherwise the Server nexus it will throw.  Synchronized collections are only allowed on the server nexus.  Collections are to be configured as either `BiDrirectional` or `ServerToClient`. 
``` cs
public partial interface IClientNexus { }
public partial interface IServerNexus
{
    [NexusCollection(NexusCollectionMode.BiDrirectional)]
    INexusList<int> IntList { get; }
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus { }

await ServerNexus.CreateServer(..., ...).StartAsync();

var client = ClientNexus.CreateClient(..., ...);
await client.ConnectAsync();

// Connect to the list (internally starts a duplex pipe for communication).
await client.Proxy.IntList.ConnectAsync();

// Add the value to the synchronized collection. This will return upon acknowledgement by the server of addition.
await client.Proxy.IntList.AddAsync(12345);
```

### IAsyncEnumerable Usage

The preferred method of reading channels is now using the IAsyncEnumerable on the provided INexusChannelReader.  This allows for the most efficient buffering of data while reading and simplifies channel closure whether graceful or not.
```cs
var writer = await pipe.GetUnmanagedChannelWriter<int>();
int counter = 0;
await foreach (var msg in await pipe.GetChannelReader<ComplexMessage>())
{
    // Do something with the message.
}
```
### INexusCollection
- Core abstraction for server-backed collections.
- Manages the connection lifecycle and provides change notifications through a Changed event.

### INexusList<T>
- Implements a list-like interface backed by a remote server.
- Mutation methods such as AddAsync, RemoveAsync, ClearAsync, and others return a Task<bool> to indicate server-side success.
- Read-only operations (Contains, IndexOf, Count, etc.) remain synchronous and reflect the client-side view.

### ISubscriptionEvent<T>
- New event subscription pattern for change tracking.
- Supports subscribing via a handler and unsubscribing by disposing the returned token.

### Async Channel Support
- Adds support for IAsyncEnumerable to simplify channel reading.
- This is now the recommended method for consuming channel data.

### Extensibility for Other Collections
- The underlying architecture is built to support additional collection types like sets and dictionaries.
- These types will be added in future updates.

### Updated Code Generation and Diagnostics
- The NexusGenerator has been updated to support collection integration and to catch common setup errors.
- Includes extensive generator and integration tests.

### Internal Collection Implementations
- Several custom collection implementations have been added for internal use, laying the groundwork for broader support.

This release is a foundational step toward enabling real-time, synchronized collections across distributed clients. Feedback and contributions are welcome.

### Breaking Changes
- ICollectionConfigurer is now a required interface of Nexuses.
- Properties are no longer allowed on the Nexus classes other than for synchronized collections.

## What's Changed
* Additional Integration Tests by @DJGosnell in https://github.com/Dtronix/NexNet/pull/42
* Distributed Collections by @DJGosnell in https://github.com/Dtronix/NexNet/pull/43


**Full Changelog**: https://github.com/Dtronix/NexNet/compare/v0.9.1...v0.10.0
