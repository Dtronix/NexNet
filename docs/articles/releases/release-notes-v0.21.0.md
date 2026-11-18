# NexNet v0.21.0

*Unreleased*

## New Features

- **WebSocket `AllowedOrigins`.** `WebSocketServerConfig.AllowedOrigins` restricts the `Origin` header of WebSocket requests to prevent Cross-Site WebSocket Hijacking when cookies are used. A request from another origin is answered 403. Empty (the default) allows every origin, as before. `UseWebSocketNexusServerAsync` passes the list to `WebSocketOptions.AllowedOrigins`, and `MapWebSocketNexus` checks it on every request. See [ASP.NET Integration: Browser Clients](../asp-net-integration.md#browser-clients).
- **Opt-in query-string access token.** `AccessTokenQueryParameter` on `WebSocketServerConfig` and `HttpSocketServerConfig` (null by default) promotes a query-string token to `Authorization: Bearer <token>` before `AspAuthenticationScheme` runs, for clients such as browsers that cannot send the header. The parameter is removed from the request's query string and its value is never logged by NexNet. The NexNet greeting token remains the canonical mechanism. See [ASP.NET Integration: Browser Clients](../asp-net-integration.md#browser-clients).
