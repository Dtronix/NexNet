# <img src="./docs/images/logo-256.png" width="48"> NexNet [![Action Workflow](https://github.com/Dtronix/NexNet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Dtronix/NexNet/actions)  [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator)

NexNet is a .NET real-time asynchronous networking library that provides bidirectional communication between the server hub and multiple clients. The library manages connections and communication, allowing for seamless integration with new technologies.


 Depends upon [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization and [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial) for Pipeline socket transports.

## Usage
```csharp
partial interface IClientHub
{
    ValueTask<int> GetValue();
}

partial interface IServerHub
{
    ValueTask<int> GetValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
}

[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub
{
    private int i = 0;

    public ValueTask<int> GetValue()
    {
        //Console.WriteLine(i++);
        return ValueTask.FromResult(i);
    }
    
    protected override async ValueTask OnConnected(bool isReconnected)
    {
        var reaultValue =  Context.Proxy.GetValueWithValueAndCancellation(321, CancellationToken.None);
    }

}

[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
    private int i2 = 0;

    public async ValueTask<int> GetValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        var i2 = Interlocked.Increment(ref i);
        try
        {
            await Task.Delay(10, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        return i2;
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        return ValueTask.CompletedTask;
    }
}

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
NexNet has built in handling of duplex pipes for sending and receiving of byte arrays.  This is useful when you want to stream data for long periods of time or have a large amount of data you want to transmit such as a file.  NexNet has a limitation of combined arguments passed at 65,535 bytes and when you want to send larger data, the NexusDuplexPipe is the method to facilitate this transmission.

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
- Minimal package requirements. [MemoryPack](https://github.com/Cysharp/MemoryPack) and [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial)

## Transports Supported
- Unix Domain Sockets (UDS)
- TCP
- TLS over TCP

**Unix Domain Sockets** are the most efficient as they encounter the least overhead and is  a good candidate for inter process communication.

**TCP** allows for network and internet communication. Fastest option next to a UDS.

**TLS over TCP** allows for TLS encryption provided by the SslStream on both the server and client. This is still fast, but not as fast as either prior options as it creates a Socket, wrapped by a Network stream wrapped by a SslStream.

Additional transports can be added easily as long as the transports guarantees order and transmission.

## Notes
This project is in development and is subject to significant change.
