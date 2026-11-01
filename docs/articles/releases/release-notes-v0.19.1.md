# NexNet v0.19.1

*Released 2026-10-31*

## Highlights

- **TLS options for WebSocket and HttpSocket clients.** `WebSocketClientConfig` and `HttpSocketClientConfig` take `SslClientAuthenticationOptions` for `https://` and `wss://` URLs. A client can present a certificate and judge the server's certificate, for example by pinning its fingerprint ([#92](https://github.com/Dtronix/NexNet/pull/92)).
- **The peer's certificate in a nexus.** `Context.RemoteCertificate` is the certificate the other side presented during the TLS handshake: the client certificate in a server nexus, the server certificate in a client nexus.
- **Fix: unobserved fault when a WebSocket peer went away.** A receive that finished after the connection was closed faulted the receive loop with `ArgumentOutOfRangeException` in `Pipe.Advance`.
- **HttpSocket client fixes.** Each connection's HTTP handler and upgrade response are now released when the connection closes, and the `Host` header includes the port.

---

## Breaking Changes

- **`ITransport` has a new member, `RemoteCertificate`.** Custom transports must implement it; return null when the transport has no TLS.
- **`ISessionContext` has a new member, `RemoteCertificate`.** Only affects code that implements the interface itself.
- **TLS handshake failures report `TransportError.AuthenticationError`.** This applies to TLS over TCP, WebSocket and HttpSocket, and matches HTTP 401/403. Before, it was `ConnectionRefused` (TLS over TCP), `ProtocolError` (HttpSocket), or `ProtocolError`/`ConnectionRefused` (WebSocket).

## New Features

### Client TLS options for HTTP transports

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

- `HttpClientConfig.SslClientAuthenticationOptions` is the same type as on `TcpTlsClientConfig` and `QuicClientConfig`.
- Null keeps the platform defaults: no client certificate and system chain validation.
- The options are copied for each connection; the config's instance is never modified.
- For these transports the validation callback's `sender` is the `HttpRequestMessage`.
- WebSocket clients now connect through the same handler (`ClientWebSocket.ConnectAsync(uri, invoker)`) and still use HTTP/1.1.

### `Context.RemoteCertificate`

- Set for TLS over TCP, QUIC, and WebSocket and HttpSocket over HTTPS. Null for UDS, TCP and plain HTTP, and when the peer presented no certificate.
- Available from `OnAuthenticate` onward, so a server can check the certificate together with the token.
- On ASP.NET Core servers it is `HttpContext.Connection.ClientCertificate` when the connection is accepted. Configure Kestrel with `ClientCertificateMode.AllowCertificate` or `RequireCertificate`. A certificate requested later (`DelayCertificate`) is not seen, and it is null when a reverse proxy terminates TLS.
- The certificate belongs to the connection and is valid while the session is connected. Copy it to keep it longer.

See [TLS and Certificates](../transports.md#tls-and-certificates) and [Client Certificates](../asp-net-integration.md#client-certificates).

## Bug Fixes

- **WebSocket receive loop fault.** The transport could be closed while a receive was in flight. When that receive then returned data, the loop advanced the completed input pipe and faulted with `ArgumentOutOfRangeException`. The task was never observed, so the fault surfaced only as an unobserved task exception. Bytes that arrive after the close are now dropped.
- **HttpSocket client resources.** Every connect created an `HttpClient`, a handler and an upgrade response that were never disposed. The transport now owns them and releases them when it closes.
- **HttpSocket `Host` header.** It was sent without the port (`Host: 127.0.0.1` for `http://127.0.0.1:9001/`). It now comes from the URL.
- **HttpSocket connect timeout.** The TCP connect was limited to 5 seconds whatever `ConnectionTimeout` was set to. It now follows `ConnectionTimeout`.
- **Unknown transport errors.** An `HttpRequestError` or `WebSocketError` value NexNet did not list threw `ArgumentOutOfRangeException` from inside the connect error handling. It now reports `TransportError.InternalError`.

## Upgrade Notes

- Clients and servers on 0.19.0 and 0.19.1 interoperate; the wire protocol is unchanged.
- The HttpSocket upgrade still uses `Upgrade: nexnet-httpsockets`. A reverse proxy in front of an HttpSocket server must forward that header.
