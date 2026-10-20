# NexNet v0.14.0

*Released 2025-12-30*

## Highlights

This release introduces connection rate limiting for DoS protection, remote endpoint access, critical bug fixes, and internal code cleanup.

### Connection Rate Limiting ([#60](https://github.com/Dtronix/NexNet/pull/60))

New opt-in protection against DoS attacks through rapid connection establishment:

- **Global Limits**: Cap total concurrent connections and new connections per second
- **Per-IP Limits**: Restrict connections per IP with sliding window rate limiting
- **Automatic Banning**: Temporarily ban IPs that repeatedly violate rate limits
- **IP Whitelisting**: Allow trusted infrastructure to bypass rate limits

```csharp
var serverConfig = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    RateLimiting = new ConnectionRateLimitConfig
    {
        MaxConcurrentConnections = 1000,
        GlobalConnectionsPerSecond = 100,
        MaxConnectionsPerIp = 10,
        BanDurationSeconds = 300,
        BanThreshold = 5
    }
};
```

Works across all transports: TCP, TLS, UDS, QUIC, WebSocket, and HttpSocket.

### Remote Endpoint Support ([#57](https://github.com/Dtronix/NexNet/pull/57))

Access client IP addresses and ports through `SessionContext`:

```csharp
protected override ValueTask OnConnected()
{
    Console.WriteLine($"Client connected from {Context.RemoteAddress}:{Context.RemotePort}");
    return ValueTask.CompletedTask;
}
```

- Supports all transport types
- Proxy header parsing for reverse proxy deployments (`X-Forwarded-For`, `CF-Connecting-IP`, etc.)
- Secure by default: `TrustProxyHeaders` must be explicitly enabled

### Bug Fixes

**Fixed Use-After-Free Race Condition ([#59](https://github.com/Dtronix/NexNet/pull/59))**

Resolved a critical race condition in the operation object pooling mechanism that caused data corruption during concurrent collection modifications. Operations were being returned to the pool while still referenced by version history, leading to corrupted transformation results in relay scenarios.

### Internal Improvements

**Code Cleanup ([#62](https://github.com/Dtronix/NexNet/pull/62))**

- Removed unused arena memory management subsystem
- Removed legacy pipeline infrastructure (`ArrayPoolStream`, `DedicatedThreadPoolPipeScheduler`, `MemoryMappedPipeReader`, `SocketServer`, `StreamConnection`)
- Integrated and simplified code from Pipelines.Sockets.Unofficial
- Added comprehensive test coverage for remaining socket functionality

## Breaking Changes

None. All changes are backward compatible.

## Upgrade Notes

- Rate limiting is disabled by default; opt-in by configuring `RateLimiting`
- Remote endpoint properties (`RemoteAddress`, `RemotePort`) are nullable
- When using proxy headers, explicitly enable `TrustProxyHeaders` only behind trusted reverse proxies
