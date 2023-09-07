# <img src="./docs/images/logo-256.png" width="48"> NexNet [![Action Workflow](https://github.com/Dtronix/NexNet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Dtronix/NexNet/actions)  [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator) [![NexNet.Quic](https://img.shields.io/nuget/v/NexNet.Quic.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Quic)

NexNet is a .NET real-time asynchronous networking library that provides bidirectional communication between a server and multiple clients.


 Depends upon [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization. Internally packages Marc Gravell's [Pipelines.Sockets.Unofficial](https://github.com/Dtronix/Pipelines.Sockets.Unofficial/tree/nexnet-v1)  with additional performance modifications for Pipeline socket transports.

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
    private long _counter = 0;
    public void UpdateInfo(int userId, int status, string? customStatus)
    {
        // Do something with the data.
    }

    public ValueTask UpdateInfoAndWait(int userId, int status, string? customStatus)
    {
        // Do something with the data.
        if(_counter++ % 10000 == 0)
            Console.WriteLine($"Counter: {_counter}");

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

## Method Invocation Table
Some methods are handled differently based upon the arguments passed and there are limitations placed upon the types of arguments which can be used together.  Most of these incompatibilities handled with Diagnostic Errors provided by the `NexNet.Generator`.  Below is a table which shows valid combinations of arguments and return values.

|                    | CancellationToken | NexusDuplexPipe | Args |
|--------------------|-------------------|-----------------|------|
| void               |                   |                 | X    |
| ValueTask          | X                 |                 | X    |
| ValueTask          |                   | X               | X    |
| ValueTask&lt;T&gt; | X                 |                 | X    |

Notes:
- `CancellationToken`s can't be combined with `NexusDuplexPipe` due to pipes having built-in cancellation/completion notifications.
- `CancellationToken` must be at the end of the argument list like standard conventions.

## Duplex Pipe Usage
NexNet has a limitation where the total arguments passed can't exceed 65,535 bytes. To address this, NexNet comes with built-in handling for duplex pipes via the `NexusDuplexPipe` argument, allowing you to both send and receive byte arrays. This is especially handy for continuous data streaming or when dealing with large data, like files.  If you need to send larger data, you should use the `NexusDuplexPipe` arguments to handle the transmission.

## Channels
Building upon the Duplex Pipes infrastructure, NexNet prvoides two channel structures to allow transmission/streaming of data structures via the `INexusDuplexChannel<T>` and `INexusDuplexUnmanagedChannel<T>` interfaces.

#### INexusDuplexChannel<T>
The `INexusDuplexChannel<T>` interface provides data transmission for all types which can be seralized by [MemoryPack](https://github.com/Cysharp/MemoryPack#built-in-supported-types).  This is the interface tuned for general usage and varying sized payloads.  If you have an [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types) to send, make sure to use the  `INexusDuplexUnmanagedChannel<T>` interface instead as it is fine tuned for performance of those simple types

Acquisition is handled through the `INexusClient.CreateChannel<T>` or `SessionContext<T>.CreateChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

#### INexusDuplexUnmanagedChannel<T> (Unmanaged Types)
The `INexusDuplexUnmanagedChannel<T>` interface provides data transmission for [unmanaged types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types).  This is good for mainly simple types and structs.  This interface should always be used over the `INexusDuplexChannel<T>` if the type is an unmanaged type as it is fine tuned for performance.

Acquisition is handled through the `INexusClient.CreateUnmanagedChannel<T>` or `SessionContext<T>.CreateUnmanagedChannel<T>` methods.  If an instance is created, it should be disposed to release held resources.

## Lifetimes
New hub instances are created for each session that connects to the hub. The hub manages the communication between the client and the server and remains active for the duration of the session. Once the session ends, either due to client disconnection, error or session timeout, the hub instance is automatically disposed of by NexNet.

Each session is assigned a unique hub instance, ensuring that data is not shared between different sessions. This design guarantees that each session is independently handled, providing a secure and efficient communication mechanism between the client and server.

## Features
- Automatic reconnection upon timeout or socket losing connection.
- High performance Socket and Pipeline usage.
- Multiple transports and easy extensibility.
- Server <-> Client communication
  - Cancellable Invocations
  - Proxies can return:
    - void for "fire and forget" invocation situations such as notifications.
    - ValueTask whcih waiting for invocation completion.
    - ValueTask<T> which will return a value from the remote invocation method.
- Server can message multiple connected clients with a single invocation.
- Automatic reconnection of clients upon timeout or loss of connection.
- Thorough use of ValueTasks in hot paths for reduced invocation overhead.
- Ping system to detect timeouts from cline tand server side.
- No reflection. All hubs and proxies are created by the NexNet.Generator project.  This allows for fast execution and easier tracing of bugs.
- Full asynchronous TPL useage throughout socket reading/writing, processing and execution of invocations and their return values.
- Minimal package requirements. [MemoryPack](https://github.com/Cysharp/MemoryPack)

## Transports Supported
- Unix Domain Sockets (UDS)
- TCP
- TLS over TCP
- QUIC (UDP)

**Unix Domain Sockets** are the most efficient as they encounter the least overhead and is  a good candidate for inter process communication.

**TCP** allows for network and internet communication. Fastest option next to a UDS.

**TLS over TCP** allows for TLS encryption provided by the SslStream on both the server and client. This is still fast, but not as fast as either prior options as it creates a Socket, wrapped by a Network stream wrapped by a SslStream.

**QUIC (UDP)** s a  UDP protocol which guarantees packet transmission, order and survives a connection IP and port change such as a connection switching from WiFi to celular.  It requires the `libmsquic` library which can be installed on linux/unix based systems via the local app pacakge manager.  Ubuntu: `sudo apt install libmsquic`.  Must install the `NexNet.Quic` Nuget package to add the Quic transport.

Additional transports can be added easily as long as the transports guarantees order and transmission.

## Notes
This project is in development and is subject to significant change.
