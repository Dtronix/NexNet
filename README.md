﻿# <img src="./docs/images/logo-256.png" width="48"> NexNet [![Action Workflow](https://github.com/Dtronix/NexNet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Dtronix/NexNet/actions)  [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator) [![NexNet.Quic](https://img.shields.io/nuget/v/NexNet.Quic.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Quic)

NexNet is a .NET real-time asynchronous networking library, providing developers with the capability to seamlessly incorporate server and client bidirectional, multiplexing, and event-driven functionality into applications. This framework streamlines the transmission of data bidirectionally between servers and clients with resilient connections.

## Features
- Automatic reconnection upon timeout or socket losing connection.
- High performance Socket and Pipeline usage.
- Multiple transports and easy extensibility.
- Strong Typed Hubs & Clients.
- Server <-> Client communication
  - Cancellable Invocations
  - Streaming byte data via `INexusDuplexPipe` with built-in congestion control.
  - Streaming classes/structs data via `NexusChannel<T>`
  - Multiplexing method invocations
  - Proxies can return:
    - void for "fire and forget" invocation situations such as notifications.
    - ValueTask for waiting for invocation completion.
    - ValueTask<T> which will return a value from the remote invocation method.
- Server can message multiple connected clients with a single invocation.
- Automatic reconnection of clients upon timeout or loss of connection.
- Thorough use of ValueTasks in hot paths for reduced invocation overhead.
- Ping system to detect timeouts from client and server side.
- No reflection. All hubs and proxies are Source Generated by the NexNet.Generator project.  This allows for fast execution and easier tracing of bugs.
- Full asynchronous TPL usage throughout socket reading/writing, processing and execution of invocations and their return values.
- Minimal external package requirements.

## Installation
Installation through NuGet is the most common method of installation.  See below for the NuGet packages.

| Name                                                                  | NuGet                                                                                                                                 | Install                               |
|-----------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------|
| [`NexNet`](https://www.nuget.org/packages/NexNet)                     | [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet)                               | `dotnet add package NexNet`           |
| [`NexNet.Generator`](https://www.nuget.org/packages/NexNet.Generator) | [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator) | `dotnet add package NexNet.Generator` |
| [`NexNet.Quic`](https://www.nuget.org/packages/NexNet.Quic)           | [![NexNet.Quic](https://img.shields.io/nuget/v/NexNet.Quic.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Quic)                | `dotnet add package NexNet.Quic`      |
| [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp)             | [![NexNet.Asp](https://img.shields.io/nuget/v/NexNet.Asp.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Asp)                   | `dotnet add package NexNet.Asp`       |

Add the `NexNex.Generator` package to the `Client` and `Server` projects and the `NexNex` package to the `Shared` project.  Once complete, reference the `Shared` project in the `Client` and `Server` project.

The `NexNet.Generator` is a Source Code Generator which will take the provided interfaces and create the required invocation system.  This is the system that allows for the elimination of reflection in the `Client` and `Server` projects and also allows for the final generated classes to be AOT friendly.
The code below will need to be used to reference the `NexNet.Generator` in your `.csproj`.  It will normally be created automatically when you add the NuGet package.
```xml
<PackageReference Include="NexNet.Generator" Version="*-*">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

## Setup
Common client and server interfaces should reside in a separate `Shared` project, referenceable by other projects.

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
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26100.3476)
Intel Core i7-10700 CPU 2.90GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 9.0.201
[Host]     : .NET 9.0.3 (9.0.325.11113), X64 RyuJIT AVX2
Job-FWSUJI : .NET 9.0.3 (9.0.325.11113), X64 RyuJIT AVX2

Platform=X64  Runtime=.NET 9.0  MaxIterationCount=5
MaxWarmupIterationCount=3  MinIterationCount=3  MinWarmupIterationCount=1
```

| Method                               |    Mean |   Error |  StdDev |     Op/s | Allocated |
|--------------------------------------|--------:|--------:|--------:|---------:|----------:|
| InvocationNoArgument                 | 24.4 us | 0.68 us | 0.10 us | 40,973.3 |     569 B |
| InvocationUnmanagedArgument          | 24.7 us | 0.67 us | 0.10 us | 40,394.6 |     624 B |
| InvocationUnmanagedMultipleArguments | 24.7 us | 0.22 us | 0.03 us | 40,393.9 |     673 B |
| InvocationNoArgumentWithResult       | 24.4 us | 0.26 us | 0.04 us | 40,959.4 |     609 B |
| InvocationWithDuplexPipe_Upload      | 39.5 us | 1.93 us | 0.30 us | 25,278.3 |   13967 B |
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
Building upon the Duplex Pipes infrastructure, NexNet provides two channel structures to allow transmission/streaming of data structures via the `INexusDuplexChannel<T>` and `INexusDuplexUnmanagedChannel<T>` interfaces.

Several extension methods have been provided to allow for ease of reading and writing of entire collections (e.g. selected table rows).
- `NexusChannelExtensions.WriteAndComplete<T>(...)`: Writing a collection to either a `INexusDuplexChannel<T>` or `INexusChannelWriter<T>` with optional batch sizes for optimized sending.
- `NexusChannelExtensions.ReadUntilComplete<T>(...)`: Reads from either a `INexusDuplexChannel<T>` or a `INexusChannelReader<T>` with an optional initial collection size to reduce collection resizing.

#### INexusDuplexChannel<T>
The `INexusDuplexChannel<T>` interface provides data transmission for all types which can be serialized by [MemoryPack](https://github.com/Cysharp/MemoryPack#built-in-supported-types).  This is the interface tuned for general usage and varying sized payloads.  If you have an [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types) to send, make sure to use the  `INexusDuplexUnmanagedChannel<T>` interface instead as it is fine-tuned for performance of those simple types

Acquisition is handled through the `INexusClient.CreateChannel<T>` or `SessionContext<T>.CreateChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

#### INexusDuplexUnmanagedChannel<T> (Unmanaged Types)
The `INexusDuplexUnmanagedChannel<T>` interface provides data transmission for [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).  This is good for mainly simple types and structs.  This interface should always be used over the `INexusDuplexChannel<T>` if the type is an unmanaged type as it is fine-tuned for performance.

Acquisition is handled through the `INexusClient.CreateUnmanagedChannel<T>` or `SessionContext<T>.CreateUnmanagedChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

## Lifetimes
New hub instances are created for each session that connects to the hub. The hub manages the communication between the client and the server and remains active for the duration of the session. Once the session ends, either due to client disconnection, error or session timeout, the hub instance is automatically disposed of by NexNet.

Each session is assigned a unique hub instance, ensuring that data is not shared between different sessions. This design guarantees that each session is independently handled, providing a secure and efficient communication mechanism between the client and server.

## Transports Supported
- Unix Domain Sockets (UDS)
- TCP
- TLS over TCP
- QUIC (UDP)
- WebSockets
- HttpSockets (Custom HTTP Negotiation)

Unix Domain Sockets (UDS)
Unix Domain Sockets offer the highest efficiency for inter-process communication due to minimal overhead. UDS are suitable when processes communicate on the same host, providing optimal performance without network stack overhead.

#### TCP
TCP supports reliable network and internet communication. It is the fastest transport method following Unix Domain Sockets, offering reliable, ordered packet delivery over IP networks.

#### TLS over TCP
TLS over TCP enables secure, encrypted communication using SslStream on both server and client ends. While it maintains good performance, it introduces additional overhead due to encapsulation—using a Socket wrapped by a NetworkStream, further wrapped by an SslStream—making it less performant compared to UDS and plain TCP.

#### QUIC (UDP)
QUIC is a reliable UDP-based protocol guaranteeing packet transmission, order integrity, and resilience against IP and port changes, such as transitions from Wi-Fi to cellular connections. Implementation requires installing the libmsquic library (sudo apt install libmsquic on Ubuntu) and including the NexNet.Quic NuGet package.

#### WebSockets (ASP.NET Core)
WebSockets enable real-time, bidirectional data exchange between client and server over persistent TCP connections. NexNet utilizes Binary WebSocket streams, which introduce a minor overhead—specifically, 4 bytes per message header/data frame transmitted.

#### HttpSockets (ASP.NET Core - Custom HTTP Negotiation)
HttpSockets establish a bidirectional, long-lived data stream by upgrading a standard HTTP connection. Similar to WebSockets in connection upgrade methodology, HttpSockets differ by eliminating WebSocket-specific message header overhead. After connection establishment, the stream is directly managed by the NexNet server, minimizing transmission overhead.  The server requires an ASP.NET Core server.

Additional transports can be added wit relative ease as long as the new transport guarantees order and transmission.

## ASP.NET Server Integration

The NexNet.Transports.Asp package allows direct integration of NexNet servers into ASP.NET Core applications. It integrates into middleware pipelines, simplifying configuration, routing, and dependency injection.

The package supports integration of NexNet server using WebSocket and HttpSocket connections, enabling easy management and proxying via common reverse proxies such as Nginx. This allows for potential improved connection handling, load balancing, and security.

Abstracting the server away from direct connections can have some advantages such as th following:
- Proxying HTTP connections through reverse proxies provides SSL/TLS termination, reducing cryptographic overhead on application servers.
- Enables centralized traffic management, simplifying enforcement of security policies (e.g., rate-limiting, IP allowlisting, header validation).
- Facilitates consistent logging, monitoring, and metrics collection at proxy level, aiding operational visibility and troubleshooting.
- Provides an additional layer for DDoS mitigation and protection against common web vulnerabilities.

### ASP Proxying Configurations

#### Nginx
Below is a simple configuration that will allow for proxy integration with an ASP.NET Core server
```
server {
    server_name example.com;
    location / {
        proxy_pass         http://backend;
        proxy_http_version 1.1;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
```

## Dependencies
- [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization. 
- Internally packages Marc Gravell's [Pipelines.Sockets.Unofficial](https://github.com/Dtronix/Pipelines.Sockets.Unofficial/tree/nexnet-v1) with additional performance modifications for Pipeline socket transports.
- Quic protocol requires `libmsquic` on *nix based systems. [Windows Support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)

