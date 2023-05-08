# NexNet
Modern &amp; Compact .NET Asynchronous Server and Client

Built similarly to SignalR, but slimmed down and without all the ASP.NET requirements. Depends upon [MemoryPack](https://github.com/Cysharp/MemoryPack) for message serialization and [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial] for Pipeline socket transports.

## Features
- Automatic reconnection upon timeout or socket losing connection.
- High performance Socket and Pipeline usage.
- Multiple transports and easy extensibility.
- Server <-> Client communication
  - Cancellable Invocations
  


## Transports Supported
- Unix Domain Sockets (UDS)
- TCP
- TLS over TCP

*Unix Domain Sockets* are the most efficient as they encounter the least overhead and is  a good candidate for inter process communication.

*TCP* allows for network and internet communication. Fastest option next to a UDS.

*TLS over TCP* allows for TLS encryption provided by the SslStream on both the server and client. This is still fast, but not as fast as either prior options as it creates a Socket, wrapped by a Network stream wrapped by a SslStream.

Additional transports can be added easily as long as the transports guarantees order and transmission.

## Notes
This project is in development and is subject to drastic change.
