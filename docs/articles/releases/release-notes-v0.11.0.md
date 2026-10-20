# NexNet v0.11.0

*Released 2025-08-10*

## Major Highlights
This release represents a major evolution toward production-ready versioning and security capabilities while maintaining backward compatibility for existing non-versioned implementations.

### Protocol Security & Versioning System
  - New versioning framework.
  - Type hashing system that generates hashes of interface methods and parameters for version validation
  - Method invocation verification to validate incoming invocations from clients and servers.
  - Enhanced protocol security with proper handshake validation and protocol error handling

### Source Generator Restructuring
  - Separation of NexusGenerator file into new modular architecture using MetaGenerator classes
  - Performance optimizations for hashing system.

### Collections Enhancements
  - New collection features including acknowledgment system simplification.
  - Thread safety improvements with semaphore-based locking for collection operations

### Extended Testing Suite
  - New test lines covering protocol security, version validation, and invocation verification
  - New Security test suite with ProtocolSecurityTests, ServerVersionValidationTests, and raw TCP client testing
  - Type hasher testing with comprehensive validation of hash generation

### New Diagnostics
  - NEXNET019, NEXNET020, NEXNET021, NEXNET022, NEXNET023
  - Extended diagnostics to verify versioning system usage is correct.

### NexusCollection enhancements
Adds multiple thread safety and consistency enhancements.

### Method hashing system
 - Extensive walking is performed by the TypeHasher.  Each of the method arguments and return values are hashed.  This walker ignored names of the members but instead records the structure of the MemoryPack object.  I would anticipate that this will require some additional logic in the future.
 - Hashes are deterministic of their content.  Eg. A hash of two different MemoryPack objects that product the same output would hash the same.

### Misc Changes
 - Fixed issue with List operations overlapping causing write issues as the Duplex pipes are not thread safe.
 - Made `INexusChannelWriter<T>` & `NexusChannelWriterUnmanaged<T>` thread safe. This fixes a long-standing issue with channels usage.


## Structure Changes

### Breaking Change
- Protocol handshake now has been added to verify protocol version and stream contents.  This is sent by the server and client to verify the data stream prior to any other processing.  This allows for future protocol version negotiation.
- Collections now no longer have a ACK message.  ACKs are handled by a flag on all the collection messages.

### Interface Changes

  1. IInvocationMethodHash - Added new static members:
    - VersionHashTable: Frozen dictionary mapping versions to hashes
    - VersionMethodHashSet: Set of all valid method hashes for invocation verification
  2. New NexusVersionAttribute for interface versioning
  3. ConnectionResult - Simplified with consolidated disconnect reasons

### File Structure Changes
  - Deleted: Multiple README.md files across projects (consolidated to main README)
  - Moved: MessageHeaderType.cs → MessageType.cs
  - Solution format: Migrated from .sln to .slnx format

### Configuration Changes
  - ConfigBase - New HandshakeTimeout configuration option

### Method Attribute Changes
  - NexusMethodAttribute - Now applies to both methods and properties, can be used on NexusCollections

## Impact Assessment

High Impact: The versioning system is a fundamental architectural change that affects:
  - Client-server connection negotiation
  - Method invocation validation
  - Interface compatibility checking
  - Code generation pipeline

Medium Impact: Thread safety and collection improvements enhance reliability but don't break existing APIs

Low Impact: File reorganization and test additions are largely internal improvements


## Sample

```cs
// V1 Server Interface - Initial version with one method
[NexusVersion(Version = "v1.0", HashLock = -2031775281)]
public interface IVersioningNexusServerV1
{
    [NexusMethod(1)]
    ValueTask<bool> GetStatus();
}

// V1 Server Implementation
[Nexus<IVersioningNexusServerV1, IVersioningNexusClient>(NexusType = NexusType.Server)]
public partial class NexusServerV1
{
    public ValueTask<bool> GetStatus()
    {
        return ValueTask.FromResult(true);
    }
}

// V2 Server Interface - Inherits from V1 and adds new method
[NexusVersion(Version = "v2.0", HashLock = -1210855623)]
public interface IVersioningNexusServerV2 : IVersioningNexusServerV1
{
    [NexusMethod(2)]
    ValueTask<string> GetServerInfo();
}

// V2 Server Implementation
[Nexus<IVersioningNexusServerV2, IVersioningNexusClient>(NexusType = NexusType.Server)]
public partial class VersioningNexusServerV2
{
    // Implements V1 method
    public ValueTask<bool> GetStatus()
    {
        return ValueTask.FromResult(true);
    }

    // New V2 method
    public ValueTask<string> GetServerInfo()
    {
        return ValueTask.FromResult("Server v2.0");
    }
}

// Client interface (same for both versions)
public interface IVersioningNexusClient
{
    ValueTask OnServerMessage(string message);
}

// V1 Client (can only connect to servers supporting V1)
[Nexus<IVersioningNexusClient, IVersioningNexusServerV1>(NexusType = NexusType.Client)]
public partial class VersioningNexusClientV1
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}

// V2 Client (can connect to servers supporting V2)
[Nexus<IVersioningNexusClient, IVersioningNexusServerV2>(NexusType = NexusType.Client)]
public partial class VersioningNexusClientV2
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}

public class VersioningSample : INexusSample
{
    public async Task Run()
    {
        var serverConfig = new TcpServerConfig
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
        };
        
        // Server supporting both V1 and V2 clients
        var server = VersioningNexusServerV2.CreateServer(serverConfig, () => new VersioningNexusServerV2());
        await server.StartAsync();

        // V1 Client connecting to server
        var clientConfig = new TcpClientConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
        };
        
        var clientV1 = VersioningNexusClientV1.CreateClient(clientConfig, new VersioningNexusClientV1());
        var result = await clientV1.TryConnectAsync();

        if (result.Success)
        {
            // Can call V1 methods
            Console.WriteLine(await clientV1.Proxy.GetStatus());
            // Cannot call V2 methods
        }

        // V2 Client connecting to server
        var clientV2 = VersioningNexusClientV2.CreateClient(clientConfig, new VersioningNexusClientV2());
        var result2 = await clientV2.TryConnectAsync();

        if (result2.Success)
        {
            // Can call both V1 and V2 methods
            Console.WriteLine(await clientV2.Proxy.GetStatus());
            Console.WriteLine(await clientV2.Proxy.GetServerInfo());
        }
    }
}
```
