This package is the core functionality for NexNet.

NexNet is a .NET real-time asynchronous networking library, providing developers with the capability to seamlessly incorporate server and client bidirectional event-driven functionality into their applications. This framework streamlines the transmission of updates bidirectionally between server-side code and connected clients with resilient communication channels.

## Features
- Automatic reconnection upon timeout or socket losing connection.
- High performance Socket and Pipeline usage.
- Multiple transports and easy extensibility.
- Server <-> Client communication
  - Cancellable Invocations
  - Streaming byte data via `INexusDuplexPipe`
  - Streaming classes/structs data via `NexusChannel<T>`
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
- Minimal external package requirements. [MemoryPack](https://github.com/Cysharp/MemoryPack)

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