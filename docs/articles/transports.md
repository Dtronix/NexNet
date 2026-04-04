# Transports

NexNet supports six transport types. All transports guarantee ordered, reliable delivery. The choice of transport affects performance, security, and deployment flexibility.

## Transport Types

### Unix Domain Sockets (UDS)

Unix Domain Sockets offer the highest efficiency for inter-process communication due to minimal overhead. UDS are suitable when processes communicate on the same host, providing optimal performance without network stack overhead.

```csharp
// Server
var config = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint("/tmp/nexnet.sock") };

// Client
var config = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint("/tmp/nexnet.sock") };
```

### TCP

TCP supports reliable network and internet communication. It is the fastest transport following UDS, offering reliable, ordered packet delivery over IP networks.

```csharp
// Server
var config = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 5000) };

// Client
var config = new TcpClientConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 5000) };
```

### TLS over TCP

TLS over TCP enables secure, encrypted communication using `SslStream` on both server and client. It introduces additional overhead due to the Socket → NetworkStream → SslStream encapsulation, making it less performant compared to UDS and plain TCP.

### QUIC (UDP)

QUIC is a reliable UDP-based protocol guaranteeing packet transmission, order integrity, and resilience against IP and port changes (such as transitions from Wi-Fi to cellular). Implementation requires:

- The [`NexNet.Quic`](https://www.nuget.org/packages/NexNet.Quic) NuGet package
- `libmsquic` library on Linux (`sudo apt install libmsquic` on Ubuntu)
- [Windows QUIC support](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview) on Windows

### WebSockets (ASP.NET Core)

WebSockets enable real-time, bidirectional data exchange over persistent TCP connections. NexNet uses binary WebSocket streams, which introduce a minor overhead of 4 bytes per message header/data frame.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md).

### HttpSockets (ASP.NET Core)

HttpSockets establish a bidirectional, long-lived data stream by upgrading a standard HTTP connection. Similar to WebSockets in connection upgrade methodology, HttpSockets eliminate WebSocket-specific message header overhead. After connection establishment, the stream is directly managed by the NexNet server, minimizing transmission overhead.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md).

## Transport Selection Guide

| Scenario | Recommended Transport | Reason |
|----------|----------------------|--------|
| Same machine IPC | Unix Domain Sockets | Highest performance, no network overhead |
| Local network | TCP | Simple, reliable, fast |
| Internet/WAN | TLS over TCP | Secure, widely supported |
| Mobile/unstable networks | QUIC | Connection migration, better congestion control |
| Web applications | WebSockets | Browser compatibility, firewall-friendly |
| Reverse proxy setups | HttpSockets | Lower overhead than WebSockets |

## Extensibility

Additional transports can be added with relative ease as long as the new transport guarantees order and transmission.

## See Also

- [ASP.NET Integration](asp-net-integration.md) — WebSocket and HttpSocket setup with middleware
- [Rate Limiting](rate-limiting.md) — Rate limiting works across all transport types
- [Protocol Specification](../internals/protocol-specification.md) — Wire protocol details
