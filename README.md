# <img src="./docs/images/logo-256.png" width="48"> NexNet [![Action Workflow](https://github.com/Dtronix/NexNet/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Dtronix/NexNet/actions)  [![NexNet](https://img.shields.io/nuget/v/NexNet.svg?maxAge=60)](https://www.nuget.org/packages/NexNet) [![NexNet.Generator](https://img.shields.io/nuget/v/NexNet.Generator.svg?maxAge=60)](https://www.nuget.org/packages/NexNet.Generator)
Modern &amp; Compact .NET Asynchronous Server and Client

Built similarly to SignalR, but slimmed down and without all the ASP.NET requirements. Depends upon [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization and [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial) for Pipeline socket transports.

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

## Lifetimes

Each session created gets its own hub instance created upon initial connection.  once the session is closed, the hub is disposed.

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

*Unix Domain Sockets* are the most efficient as they encounter the least overhead and is  a good candidate for inter process communication.

*TCP* allows for network and internet communication. Fastest option next to a UDS.

*TLS over TCP* allows for TLS encryption provided by the SslStream on both the server and client. This is still fast, but not as fast as either prior options as it creates a Socket, wrapped by a Network stream wrapped by a SslStream.

Additional transports can be added easily as long as the transports guarantees order and transmission.

## Notes
This project is in development and is subject to significant change.
