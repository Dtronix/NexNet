# Rate Limiting

NexNet provides application-level connection rate limiting to protect servers against denial-of-service attacks. Rate limiting is configured per server and works across all transport types.

## Configuration

```csharp
var serverConfig = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    RateLimiting = new ConnectionRateLimitConfig
    {
        MaxConcurrentConnections = 1000,    // Total connections
        MaxConnectionsPerIp = 10,           // Per-IP limit
        ConnectionsPerIpPerWindow = 20,     // Rate limit per IP
        PerIpWindowSeconds = 60,            // Sliding window
        BanDurationSeconds = 300,           // 5-min ban for offenders
        BanThreshold = 5                    // Violations before ban
    }
};
```

## Configuration Properties

| Property | Description |
|----------|-------------|
| `MaxConcurrentConnections` | Maximum total concurrent connections the server will accept |
| `MaxConnectionsPerIp` | Maximum concurrent connections from a single IP address |
| `ConnectionsPerIpPerWindow` | Maximum new connections from a single IP within the sliding window |
| `PerIpWindowSeconds` | Duration of the sliding window in seconds |
| `BanDurationSeconds` | How long an IP is banned after exceeding the ban threshold |
| `BanThreshold` | Number of rate limit violations before an IP is temporarily banned |

## Capabilities

- **Global concurrent connection limits** — Caps the total number of active connections
- **Per-IP connection limits** — Prevents a single source from consuming all connections
- **Sliding window rate limiting** — Controls the rate of new connections per IP
- **Automatic temporary banning** — Repeat offenders are banned for a configurable duration
- **IP whitelisting** — Trusted infrastructure can be exempted from rate limits

## Transport Compatibility

Rate limiting works across all transport types (TCP, TLS, UDS, WebSocket, HttpSocket, QUIC). Per-IP limits are automatically skipped for Unix Domain Sockets, where IP addresses are not applicable.

## See Also

- [Transports](transports.md) — Transport types and selection guide
- [Authentication](authentication.md) — Combine rate limiting with authentication for layered security
