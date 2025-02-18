# <img src="./docs/images/logo-256.png" width="48"> NexNet [![Action Workflow](https://github.com/Dtronix/NexNet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Dtronix/NexNet/actions)  [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator) [![NexNet.Quic](https://img.shields.io/nuget/v/NexNet.Quic.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Quic)

NexNet is a .NET real-time asynchronous networking library, providing developers with the capability to seamlessly incorporate server and client bidirectional event-driven functionality into their applications. This framework streamlines the transmission of updates bidirectionally between server-side code and connected clients with resilient communication channels.

## Features
- Automatic reconnection upon timeout or socket losing connection.
- High performance Socket and Pipeline usage.
- Multiple transports and easy extensibility.
- Server <-> Client communication
  - Cancellable Invocations
  - Streaming byte data via `INexusDuplexPipe` with built-in congestion control.
  - Streaming classes/structs data via `NexusChannel<T>`
  - Proxies can return:
    - void for "fire and forget" invocation situations such as notifications.
    - ValueTask for waiting for invocation completion.
    - ValueTask<T> which will return a value from the remote invocation method.
- Server can message multiple connected clients with a single invocation.
- Automatic reconnection of clients upon timeout or loss of connection.
- Thorough use of ValueTasks in hot paths for reduced invocation overhead.
- Ping system to detect timeouts from cline tand server side.
- No reflection. All hubs and proxies are created by the NexNet.Generator project.  This allows for fast execution and easier tracing of bugs.
- Full asynchronous TPL useage throughout socket reading/writing, processing and execution of invocations and their return values.
- Minimal external package requirements.

## Usage

#### Base classes
```csharp
interface IInvocationSampleClientNexus
{
    ValueTask<string> GetUserName();
}

interface IInvocationSampleServerNexus
{
    void UpdateInfo(int userId, int status, string? customStatus);
    ValueTask UpdateInfoAndWait(int userId, int status, string? customStatus);
    ValueTask<int> GetStatus(int userId);
}


[Nexus<IInvocationSampleClientNexus, IInvocationSampleServerNexus>(NexusType = NexusType.Client)]
partial class InvocationSampleClientNexus
{
    public ValueTask<string> GetUserName()
    {
        return new ValueTask<string>("Bill");
    }
}

[Nexus<IInvocationSampleServerNexus, IInvocationSampleClientNexus>(NexusType = NexusType.Server)]
partial class InvocationSampleServerNexus
{
    public void UpdateInfo(int userId, int status, string? customStatus)
    {
        // Do something with the data.
    }

    public ValueTask UpdateInfoAndWait(int userId, int status, string? customStatus)
    {
        // Do something with the data.
        return default;
    }

    public ValueTask<int> GetStatus(int userId)
    {
        return new ValueTask<int>(1);
    }
}
```
#### Usage
```csharp
var client = InvocationSampleClientNexus.CreateClient(ClientConfig, new InvocationSampleClientNexus());
var server = InvocationSampleServerNexus.CreateServer(ServerConfig, () => new InvocationSampleServerNexus());

await server.StartAsync();
await client.ConnectAsync();

await client.Proxy.UpdateInfoAndWait(1, 2, "Custom Status");
```
## Benchmarks
```
BenchmarkDotNet v0.13.10, Windows 11 (10.0.26100.3194)
Intel Core i7-10700 CPU 2.90GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 8.0.400
  [Host]     : .NET 8.0.13 (8.0.1325.6609), X64 RyuJIT AVX2
  Job-XDUVQL : .NET 8.0.13 (8.0.1325.6609), X64 RyuJIT AVX2

Platform=X64  Runtime=.NET 8.0  MaxIterationCount=5
MaxWarmupIterationCount=3  MinIterationCount=3  MinWarmupIterationCount=1
```

| Method                               |    Mean |     Error |   StdDev | Op/s     | Gen0   | Allocated |
|------------------------------------- |--------:|----------:|---------:|---------:|-------:|----------:|
| InvocationNoArgument                 | 27.1 us |   0.35 us |  0.05 us | 36,900.5 | 0.0610 |     632 B |
| InvocationUnmanagedArgument          | 27.7 us |   2.37 us |  0.62 us | 36,091.7 | 0.0610 |     690 B |
| InvocationUnmanagedMultipleArguments | 27.6 us |   0.86 us |  0.22 us | 36,250.8 | 0.0610 |     737 B |
| InvocationNoArgumentWithResult       | 27.7 us |   1.42 us |  0.37 us | 36,164.3 | 0.0610 |     675 B |
| InvocationWithDuplexPipe_Upload      | 45.8 us |  32.00 us |  4.95 us | 21,847.0 | 2.0752 |   13998 B |

## Method Invocation Table
Some methods are handled differently based upon the arguments passed and there are limitations placed upon the types of arguments which can be used together.  Most of these incompatibilities handled with Diagnostic Errors provided by the `NexNet.Generator`.  Below is a table which shows valid combinations of arguments and return values.

|                    | CancellationToken | INexusDuplexPipe | INexusChannel<T> | Args |
|--------------------|-------------------|------------------|------------------|------|
| void               |                   |                  |                  | X    |
| ValueTask          | X                 |                  |                  | X    |
| ValueTask          |                   | X                | X                | X    |
| ValueTask&lt;T&gt; | X                 |                  |                  | X    |

Notes:
- `CancellationToken`s can't be combined with `NexusDuplexPipe` nor `INexusChannel<T>` due to pipes/channels having built-in cancellation/completion notifications.
- `CancellationToken` must be at the end of the argument list like standard conventions.

## Duplex Pipe Usage
NexNet has a limitation where the total arguments passed can't exceed 65,535 bytes. To address this, NexNet comes with built-in handling for duplex pipes via the `NexusDuplexPipe` argument, allowing you to both send and receive byte arrays. This is especially handy for continuous data streaming or when dealing with large data, like files.  If you need to send larger data, you should use the `NexusDuplexPipe` arguments to handle the transmission.

## Channels
Building upon the Duplex Pipes infrastructure, NexNet prvoides two channel structures to allow transmission/streaming of data structures via the `INexusDuplexChannel<T>` and `INexusDuplexUnmanagedChannel<T>` interfaces.

Several extension methods have been provided to allow for ease of reading and writing of entire collections (eg. selected table rows).
- `NexusChannelExtensions.WriteAndComplete<T>(...)`: Writing a collection to either a `INexusDuplexChannel<T>` or `INexusChannelWriter<T>` with optional batch sizes for optimized sending.
- `NexusChannelExtensions.ReadUntilComplete<T>(...)`: Reads from either a `INexusDuplexChannel<T>` or a `INexusChannelReader<T>` with an optional initial collection size to reduce collection resizing.

#### INexusDuplexChannel<T>
The `INexusDuplexChannel<T>` interface provides data transmission for all types which can be seralized by [MemoryPack](https://github.com/Cysharp/MemoryPack#built-in-supported-types).  This is the interface tuned for general usage and varying sized payloads.  If you have an [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types) to send, make sure to use the  `INexusDuplexUnmanagedChannel<T>` interface instead as it is fine tuned for performance of those simple types

Acquisition is handled through the `INexusClient.CreateChannel<T>` or `SessionContext<T>.CreateChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

#### INexusDuplexUnmanagedChannel<T> (Unmanaged Types)
The `INexusDuplexUnmanagedChannel<T>` interface provides data transmission for [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).  This is good for mainly simple types and structs.  This interface should always be used over the `INexusDuplexChannel<T>` if the type is an unmanaged type as it is fine tuned for performance.

Acquisition is handled through the `INexusClient.CreateUnmanagedChannel<T>` or `SessionContext<T>.CreateUnmanagedChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

## Lifetimes
New hub instances are created for each session that connects to the hub. The hub manages the communication between the client and the server and remains active for the duration of the session. Once the session ends, either due to client disconnection, error or session timeout, the hub instance is automatically disposed of by NexNet.

Each session is assigned a unique hub instance, ensuring that data is not shared between different sessions. This design guarantees that each session is independently handled, providing a secure and efficient communication mechanism between the client and server.

## Transports Supported
- Unix Domain Sockets (UDS)
- TCP
- TLS over TCP
- QUIC (UDP)

**Unix Domain Sockets** are the most efficient as they encounter the least overhead and is  a good candidate for inter process communication.

**TCP** allows for network and internet communication. Fastest option next to a UDS.

**TLS over TCP** allows for TLS encryption provided by the SslStream on both the server and client. This is still fast, but not as fast as either prior options as it creates a Socket, wrapped by a Network stream wrapped by a SslStream.

**QUIC (UDP)** s a  UDP protocol which guarantees packet transmission, order and survives a connection IP and port change such as a connection switching from WiFi to celular.  It requires the `libmsquic` library which can be installed on linux/unix based systems via the local app pacakge manager.  Ubuntu: `sudo apt install libmsquic`.  Must install the `NexNet.Quic` Nuget package to add the Quic transport.

## Dependencies
- [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization. 
- Internally packages Marc Gravell's [Pipelines.Sockets.Unofficial](https://github.com/Dtronix/Pipelines.Sockets.Unofficial/tree/nexnet-v1) with additional performance modifications for Pipeline socket transports.
- Quic protocol requires `libmsquic` on *nix based systems. [Windows Support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)

Additional transports can be added easily as long as the new transport guarantees order and transmission.
