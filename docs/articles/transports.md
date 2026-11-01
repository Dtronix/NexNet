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

> [!IMPORTANT]
> Always set `SslClientAuthenticationOptions.TargetHost` to a DNS name (for example the name on the server certificate). Starting with libmsquic 2.5.11 / 2.6.1, the TLS handshake fails with `QUIC_STATUS_TLS_ERROR` when `TargetHost` is unset or an IP address, even if `RemoteCertificateValidationCallback` accepts the certificate.

### WebSockets (ASP.NET Core)

WebSockets enable real-time, bidirectional data exchange over persistent TCP connections. NexNet uses binary WebSocket streams, which introduce a minor overhead of 4 bytes per message header/data frame.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md).

### HttpSockets (ASP.NET Core)

HttpSockets establish a bidirectional, long-lived data stream by upgrading a standard HTTP connection. Similar to WebSockets in connection upgrade methodology, HttpSockets eliminate WebSocket-specific message header overhead. After connection establishment, the stream is directly managed by the NexNet server, minimizing transmission overhead.

Requires the [`NexNet.Asp`](https://www.nuget.org/packages/NexNet.Asp) package and an ASP.NET Core server. See [ASP.NET Integration](asp-net-integration.md).

HttpSockets upgrade with the custom token `Upgrade: nexnet-httpsockets`. A reverse proxy must forward that header and the upgraded stream; one that only passes WebSocket upgrades will reject the connection. A direct connection, or a network layer that forwards TCP (for example a VPN), needs no configuration.

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
