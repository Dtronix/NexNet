# ASP.NET Integration

The `NexNet.Asp` package integrates NexNet servers into ASP.NET Core applications. It plugs into middleware pipelines, enabling dependency injection, authentication, and reverse proxy support via WebSocket and HttpSocket transports.

## Advantages

Integrating through ASP.NET Core provides:

- SSL/TLS termination at the reverse proxy, reducing cryptographic overhead on the application server
- Centralized traffic management for security policies (rate limiting, IP allowlisting, header validation)
- Consistent logging, monitoring, and metrics collection at the proxy level
- An additional layer for DDoS mitigation and protection against common web vulnerabilities
- Additional authentication with the connection prior to handing it to the NexNet server

## Server Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

// Set up forwarded headers for proxy support
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.All);

// Optionally add authentication (Bearer shown for simplicity)
builder.Services.AddAuthentication().AddBearerToken("BearerTokenScheme", ...);
builder.Services.AddAuthorization();

// Register the NexNet server in DI — enables constructor injection on ServerNexus
builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

var app = builder.Build();

// Enable headers and authentication middleware
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

// Start the NexNet server on an HTTP endpoint
// Use UseWebSocketNexusServerAsync for WebSocket transport instead
await app.UseHttpSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c =>
{
    c.Path = "/nexus";

    // Optionally enable ASP.NET authentication
    c.AspEnableAuthentication = true;
    c.AspAuthenticationScheme = "BearerTokenScheme";
}).StartAsync(app.Lifetime.ApplicationStopped);
```

## Client Connection

```csharp
// Use WebSocketClientConfig for WebSocket transport
var config = new HttpSocketClientConfig
{
    // Change to ws:// or wss:// for WebSocket connections
    Url = new Uri("http://127.0.0.1:9001/nexus"),
    // Optional authentication header
    AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "SecretTokenValue")
};

var client = ClientNexus.CreateClient(config, new ClientNexus());
await client.ConnectAsync();
```

## Reverse Proxy Configuration

### Nginx

A configuration that supports both WebSocket upgrades and HttpSocket connections:

```nginx
server {
    server_name example.com;
    location / {
        proxy_pass         http://backend;
        proxy_http_version 1.1;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

## See Also

- [Transports](transports.md) — Transport selection guide and comparison
- [Authentication](authentication.md) — NexNet-level authentication (can be combined with ASP.NET auth)
