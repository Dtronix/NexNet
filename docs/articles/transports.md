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

#### Socket file permissions and stale files

```csharp
var config = new UdsServerConfig
{
    EndPoint = new UnixDomainSocketEndPoint("/run/myapp/nexnet.sock"),
    SocketFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, // 0600
    RemoveStaleSocketFile = true,
};
```

- `SocketFileMode` sets the socket file's permissions right after it is created, before the server listens, so no client can connect while the file has the umask's mode. Null (the default) keeps the umask's mode. It is Unix only: on Windows a non-null value makes `StartAsync` throw `PlatformNotSupportedException`; protect the socket's folder with ACLs there.
- `RemoveStaleSocketFile` (default false) deletes a socket file left behind by a server that is no longer running, for example after a crash. Without it, such a file makes `StartAsync` fail with `TransportException` and `TransportError.AddressInUse`. The file is deleted only when a connection to it is refused and the path is confirmed to be a socket file:
  - If a server is listening on the path, `StartAsync` fails with `TransportError.AddressInUse` and the file is left alone.
  - A regular file, directory, link, or any file whose type cannot be determined is never deleted; `StartAsync` fails as it does without the option.
  - Another process binding the same path between the check and the delete is not detected. Use one path per server.
- A graceful `StopAsync` deletes the socket file only if it is still the file the server created. If the file was replaced while the server ran, the replacement is left alone.

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

> [!IMPORTANT]
> Always set `SslClientAuthenticationOptions.TargetHost` to a DNS name (for example the name on the server certificate). Starting with libmsquic 2.5.11 / 2.6.1, the TLS handshake fails with `QUIC_STATUS_TLS_ERROR` when `TargetHost` is unset or an IP address, even if `RemoteCertificateValidationCallback` accepts the certificate.

### WebSockets (ASP.NET Core)

WebSockets enable real-time, bidirectional data exchange over persistent TCP connections. NexNet uses binary WebSocket streams, which introduce a minor overhead of 4 bytes per message header/data frame.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md). The handshake, message and close rules are in [WebSocket binding](../internals/protocol-specification.md#137-websocket).

### HttpSockets (ASP.NET Core)

HttpSockets establish a bidirectional, long-lived data stream by upgrading a standard HTTP connection. Similar to WebSockets in connection upgrade methodology, HttpSockets eliminate WebSocket-specific message header overhead. After connection establishment, the stream is directly managed by the NexNet server, minimizing transmission overhead.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md).

HttpSockets upgrade with the custom token `Upgrade: nexnet-httpsockets`. A reverse proxy must forward that header and the upgraded stream; one that only passes WebSocket upgrades will reject the connection. A direct connection, or a network layer that forwards TCP (for example a VPN), needs no configuration. The exact upgrade request and response are in [HttpSocket binding](../internals/protocol-specification.md#136-httpsocket).

## TLS and Certificates

### Client options for HTTPS

`WebSocketClientConfig` and `HttpSocketClientConfig` take `SslClientAuthenticationOptions` for `https://` and `wss://` URLs, the same type as `TcpTlsClientConfig` and `QuicClientConfig`. Use it to present a client certificate or to judge the server's certificate, for example by pinning its fingerprint:

```csharp
var config = new HttpSocketClientConfig
{
    Url = new Uri("https://100.64.0.10:9001/nexus"),
    SslClientAuthenticationOptions = new SslClientAuthenticationOptions
    {
        ClientCertificates = new X509CertificateCollection { clientCertificate },
        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 cert
            && cert.GetCertHashString(HashAlgorithmName.SHA256) == pinnedSha256Hex,
    },
};
```

- Null options use the platform defaults: no client certificate and system chain validation.
- The config's options instance is copied for each connection and never modified, so one config can be shared.
- For these two transports the validation callback's `sender` is the `HttpRequestMessage`, not an `SslStream`.

A TLS handshake failure (the server certificate was rejected, or the server refused the client certificate) fails the connect with `TransportException` and `TransportError.AuthenticationError`, on every TLS transport. HTTP 401 and 403 responses map to the same error.

### The peer's certificate in a nexus

`Context.RemoteCertificate` is the certificate the other side presented during the TLS handshake:

| Nexus | Value |
|-------|-------|
| Server | The client certificate, or null if the client presented none |
| Client | The server certificate |

It is set for TLS over TCP, QUIC, and WebSockets and HttpSockets over HTTPS, and is null for UDS, TCP and plain HTTP. It is available from `OnAuthenticate` onward, so a server can check it together with the authentication token:

```csharp
protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? token)
{
    var fingerprint = Context.RemoteCertificate?.GetCertHashString(HashAlgorithmName.SHA256);
    return ValueTask.FromResult(IsEnrolled(fingerprint, token) ? CreateIdentity(fingerprint) : null);
}
```

The certificate belongs to the connection and is valid while the session is connected; copy it (`X509CertificateLoader.LoadCertificate(cert.RawData)`) to keep it longer. For ASP.NET Core servers see [Client certificates](asp-net-integration.md#client-certificates).

## Multiple Endpoints

One server can accept sessions on several transports at once. The configuration passed to the constructor is the **primary** one; add more endpoints before starting:

```csharp
var server = ServerNexus.CreateServer(new TcpTlsServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 9001), ... },
    () => new ServerNexus());
server.AddListener(new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint("/run/app.sock") }, "uds");
server.AddListener(new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 9002) }, "tcp-local");
await server.StartAsync();
```

- `AddListener(config, name)` takes a listener configuration (TCP, TLS, UDS, QUIC). `AddExternalEndpoint(config, name)` returns an `IAcceptsExternalTransport` that the application (or NexNet.Asp, see [ASP.NET Integration](asp-net-integration.md#multiple-endpoints)) feeds connected transports to.
- Both are allowed only after the server is configured and while it is stopped. Endpoints can not be removed or added while running.
- Names are unique within the server. The primary endpoint is named after its transport (`tcp`, `tls`, `uds`, `quic`, `websocket`, `httpsocket`). A second endpoint on the same TCP port (an any-address bind included), UDS path, ASP.NET path or configuration object is rejected.
- Start binds every listener; if one fails, the ones already bound are closed and the server stays `Stopped`. A listener that faults while running stops only that endpoint; the server stops when no listener or external endpoint is left.
- `server.Endpoints` lists `NexusEndpointInfo(Name, Transport, IsSecure, Path)`, the primary first. While the server runs, `BoundEndPoint` on a TCP, TLS or UDS listener is the end point it actually bound, so a listener configured with port 0 reports the port the OS picked (`((IPEndPoint)server.Endpoints[0].BoundEndPoint!).Port`); it is null while stopped and for ASP.NET and QUIC endpoints. `server.GetSnapshot().Endpoints` adds each endpoint's state, address (the bound end point while running) and session count.

**Which settings come from where.** Each endpoint's configuration supplies its sessions' settings: `Timeout`, `HandshakeTimeout`, `MaxMessageSize`, flow control (`SessionBudget`, `PipeWindow`, `InvocationCredits`, ...), `Authenticate`, `AuthorizationCacheDuration`, TLS and socket options, and the ASP.NET path, authentication and proxy headers. Server-wide settings come from the primary configuration only: `Logger`, `Time`, `MeterFactory`, `RateLimiting` (its limits count connections on every endpoint together), `MaxPendingHandshakes` and collections. An added endpoint must keep the primary's `Time` and leave `RateLimiting` null.

**Authentication guard.** When the primary configuration sets `Authenticate = true`, `StartAsync` throws unless every endpoint does, so an added endpoint can not silently skip `OnAuthenticate`.

**In the nexus.** `Context.Endpoint` tells a session which endpoint accepted it, from `OnAuthenticate` onward:

```csharp
protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? token)
{
    // Allow the local UDS endpoint without a token.
    if (Context.Endpoint?.Name == "uds")
        return new(new DefaultIdentity { DisplayName = "local" });
    ...
}
```

`IsSecure` is true for TLS and QUIC. For WebSocket and HttpSocket it is the `HttpRequest.IsHttps` of the request that opened the connection.

**Metrics and logs.** Server sessions and rejections carry the `nexnet.endpoint` metric tag with the endpoint name, next to `nexnet.role` and `nexnet.transport`. Sessions of an added endpoint log under `SV{id}/{name}`; those of the primary endpoint under `SV{id}`, as before.

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
- [Wire Protocol](../internals/protocol-specification.md) — Wire protocol details
- [Transport bindings](../internals/protocol-specification.md#13-transport-bindings) — How each transport carries the NexNet stream: TLS, the HttpSocket upgrade request, WebSocket messages and close codes
