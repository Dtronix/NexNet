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

## Multiple Endpoints

A server can serve WebSocket and HttpSocket next to TCP, TLS or UDS listeners (see [Transports: Multiple Endpoints](transports.md#multiple-endpoints)). `AddNexusServer` with options configures the server and registers a hosted service that starts it once the application has started (after Kestrel binds) and stops it when the application stops:

```csharp
builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>(o =>
{
    o.Configure(new TcpTlsServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 9002), ... }); // primary
    o.AddListener(new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint("/run/app.sock") }, "uds");
});

var app = builder.Build();
var server = app.Services.GetRequiredService<NexusServer<ServerNexus, ServerNexus.ClientProxy>>();

app.UseWebSockets();
app.MapNexusWebSocket(server, new WebSocketServerConfig { Path = "/nexus/ws", MaxMessageSize = 1024 * 1024 });
app.UseHttpSockets();
app.MapNexusHttpSocket(server, new HttpSocketServerConfig { Path = "/nexus/http", AspEnableAuthentication = true });

await app.RunAsync(); // the hosted service starts and stops the server
```

- `MapNexusWebSocket(server, config, name)` and `MapNexusHttpSocket(server, config, name)` add the configuration with `AddExternalEndpoint` (named `websocket` / `httpsocket` by default) unless it is the server's primary configuration. Its session settings, ASP.NET authentication and proxy-header settings apply to the sessions of that path only. The server must be configured and stopped.
- `MapWebSocketNexus(config, server)` and `MapHttpSocketNexus(config, server)` route the same way: the primary configuration keeps today's behavior, another configuration of an already configured server becomes its own endpoint.
- `UseWebSocketNexusServerAsync` and `UseHttpSocketNexusServerAsync` configure the server when it is not configured yet, and otherwise attach their configuration as another endpoint instead of throwing.
- When the options leave the primary configuration's `Logger` or `MeterFactory` unset, the application's `ILogger` and `IMeterFactory` are used. If the server fails to start (for example a listener can not bind), the hosted service logs it and stops the application.
- `Context.Endpoint.IsSecure` is `HttpRequest.IsHttps` for WebSocket and HttpSocket sessions; behind a reverse proxy that terminates TLS it is false.

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

For `https://`/`wss://` URLs, `SslClientAuthenticationOptions` on the client config sets the client certificate and server certificate validation. See [TLS and Certificates](transports.md#tls-and-certificates).

## Browser Clients

Browsers cannot set an `Authorization` header on a WebSocket and send cookies with every request, so a server that browsers reach needs two extra settings.

### Restrict origins

With cookie authentication, any web page the user visits can open a WebSocket to your server with the user's cookies (Cross-Site WebSocket Hijacking). Set `AllowedOrigins` whenever cookies are used:

```csharp
app.UseWebSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c =>
{
    c.NexusConfig.Path = "/nexus";
    c.NexusConfig.AllowedOrigins.Add("https://app.example.com");
});
```

- A request whose `Origin` header is not in the list is answered 403 and never reaches the nexus. Origins compare case-insensitively.
- An empty list (the default) or a `*` entry allows every origin.
- A request without an `Origin` header is allowed. Non-browser clients (the NexNet .NET client, for example) do not send one.
- `UseWebSocketNexusServerAsync` also passes the list to ASP.NET's `WebSocketOptions.AllowedOrigins`. `MapWebSocketNexus` checks it on every request, so the check holds when you call `UseWebSockets` yourself.
- For HttpSocket, set the same list on `HttpSocketOptions.AllowedOrigins` (`c.SocketOptions` in `UseHttpSocketNexusServerAsync`).

### Authenticate the connection

The NexNet greeting token (`ClientConfig.Authenticate`, checked in `OnAuthenticate`) is the canonical way to authenticate any client, browsers included: it travels inside the connection, not in the URL. See [Authentication](authentication.md).

Use a query-string token only when ASP.NET authentication must run before the upgrade (`AspEnableAuthentication = true`) and the client cannot send an `Authorization` header. It is off by default. Set `AccessTokenQueryParameter` on `WebSocketServerConfig` or `HttpSocketServerConfig`:

```csharp
c.NexusConfig.AspEnableAuthentication = true;
c.NexusConfig.AspAuthenticationScheme = "BearerTokenScheme";
c.NexusConfig.AccessTokenQueryParameter = "access_token";
// The client connects to wss://example.com/nexus?access_token=<token>
```

- When the request has no `Authorization` header, the parameter's value becomes `Authorization: Bearer <token>` before `AspAuthenticationScheme` runs. A request that has an `Authorization` header keeps it and the query value is ignored.
- The scheme is evaluated again with the promoted header, even if `UseAuthentication` already ran for the request.
- The parameter is removed from the request's query string, and NexNet never logs its value. Proxies and ASP.NET request logging can still record full URLs, so keep such tokens short-lived.

## Client Certificates

When Kestrel terminates TLS and accepts client certificates, the certificate reaches the nexus as `Context.RemoteCertificate` for both WebSocket and HttpSocket sessions:

```csharp
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(9001, listen =>
    listen.UseHttps(new HttpsConnectionAdapterOptions
    {
        ServerCertificate = serverCertificate,
        ClientCertificateMode = ClientCertificateMode.AllowCertificate,
        // Accept any certificate here and decide in the nexus, e.g. against an enrolled fingerprint.
        ClientCertificateValidation = (_, _, _) => true,
    })));
```

- The value is `HttpContext.Connection.ClientCertificate` when the connection is accepted. NexNet does not call `GetClientCertificateAsync`, so with `ClientCertificateMode.DelayCertificate` the session sees no certificate.
- When a reverse proxy terminates TLS, the application server never sees the client certificate and `Context.RemoteCertificate` is null.

## The ASP.NET Principal

The principal of the HTTP request that opened the connection (`HttpContext.User`) reaches the nexus as `Context.User` (`ClaimsPrincipal?`) for WebSocket and HttpSocket sessions. It is readable in `OnAuthenticate`, `OnConnected` and every invocation:

```csharp
protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte> authToken)
{
    var user = Context.User;
    if (user?.Identity?.IsAuthenticated == true && user.IsInRole("admin"))
        return new ValueTask<IIdentity?>(new UserIdentity(user.Identity.Name!));

    return new ValueTask<IIdentity?>((IIdentity?)null);
}
```

- With `AspEnableAuthentication = true` it is the principal of `AspAuthenticationScheme` (or the default scheme when none is set); a request that fails it never reaches the nexus. HttpSocket answers it `401`; WebSocket accepts the upgrade and refuses the session inside the NexNet protocol with `DisconnectReason.Authentication`, so browsers, which cannot read the status of a refused upgrade, report `AuthenticationError` like other clients.
- With `AspEnableAuthentication = false` it is whatever the pipeline set, for example `UseAuthentication` with a default scheme; otherwise ASP.NET's empty, unauthenticated principal.
- It is captured once when the connection is accepted and is not re-validated during the session. Only the principal is kept, not the `HttpContext`.
- It is extra input, not the session's identity: with `Authenticate = true`, `OnAuthenticate` still decides, and returning null refuses the connection. `Context.Identity` is what `OnAuthenticate` returns.
- `Context.User` is null for TCP, TLS, UDS and QUIC sessions and in a client nexus.

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

The HttpSocket upgrade token is `nexnet-httpsockets`; the configuration above forwards it because it passes the client's `Upgrade` header through.

## See Also

- [Transports](transports.md) — Transport selection guide and comparison
- [Authentication](authentication.md) — NexNet-level authentication (can be combined with ASP.NET auth)
