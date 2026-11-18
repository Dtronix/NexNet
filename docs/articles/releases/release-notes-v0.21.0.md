# NexNet v0.21.0

*Unreleased*

## Behaviour changes

- **Behavior change: a call canceled before it is sent throws `TaskCanceledException`.** When the `CancellationToken` of a `ValueTask` or `ValueTask<T>` method is already canceled, or is canceled while the call waits for invocation credit, the call now throws `TaskCanceledException`. Before, `ValueTask` completed successfully and `ValueTask<T>` returned `default`, so a canceled call could not be told apart from a real result. As before, nothing is sent and no invocation ID is used. Fire-and-forget (`void`) methods and broadcasts are unchanged. See [Hub Invocations: Cancellation](../hub-invocations.md#cancellation).

## New features

- **WebSocket `AllowedOrigins`.** `WebSocketServerConfig.AllowedOrigins` restricts the `Origin` header of WebSocket requests to prevent Cross-Site WebSocket Hijacking when cookies are used. A request from another origin is answered 403. Empty (the default) allows every origin, as before. `UseWebSocketNexusServerAsync` passes the list to `WebSocketOptions.AllowedOrigins`, and `MapWebSocketNexus` checks it on every request. See [ASP.NET Integration: Browser Clients](../asp-net-integration.md#browser-clients).
- **Opt-in query-string access token.** `AccessTokenQueryParameter` on `WebSocketServerConfig` and `HttpSocketServerConfig` (null by default) promotes a query-string token to `Authorization: Bearer <token>` before `AspAuthenticationScheme` runs, for clients such as browsers that cannot send the header. The parameter is removed from the request's query string and its value is never logged by NexNet. The NexNet greeting token remains the canonical mechanism. See [ASP.NET Integration: Browser Clients](../asp-net-integration.md#browser-clients).

## Fixes

- **Duplicate nexus method names are reported as NEXNET042 instead of crashing the generator.** A nexus interface that overloads a method name, or declares a name that an inherited interface also declares, made the source generator throw, so the build reported only a generator failure (CS8785). It is now the error NEXNET042, reported at the duplicate method, and that nexus is not generated. Method names must be unique across a nexus interface and its inherited interfaces. A nexus class that overloads the name of an authorized method no longer crashes the generator either; the authorization of the overload that implements the interface method is used.
- **Generator diagnostics are reported at their source location.** Nexus validation and serialization diagnostics (NEXNET001 – NEXNET042) had no location, so the build and the IDE showed them without a file or line. They now point at the method, member, interface or class they are about.

## Documentation
