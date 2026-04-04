# Authorization

NexNet provides declarative, source-generator-driven authorization for server nexus methods and synchronized collections. Authorization is enforced server-side before argument deserialization, preventing wasted work for unauthorized calls.

## Setup

### 1. Define a Permission Enum

The permission enum must use the default `int` backing type:

```csharp
public enum Permission { Read, Write, Admin }
```

### 2. Decorate Methods

Apply `[NexusAuthorize<TPermission>]` to server nexus methods:

```csharp
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus
{
    [NexusAuthorize<Permission>(Permission.Admin)]
    public ValueTask AdminOnly() { ... }

    [NexusAuthorize<Permission>(Permission.Read, Permission.Write)]
    public ValueTask MultiPermission() { ... }

    [NexusAuthorize<Permission>()]  // Marker-only: requires auth, no specific permission
    public ValueTask AnyAuthenticated() { ... }

    public ValueTask PublicMethod() { ... }  // No auth check
}
```

### 3. Authorize Collections

Collections can also be authorized on the interface:

```csharp
public partial interface IServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<Permission>(Permission.Admin)]
    INexusList<string> SecureItems { get; }
}
```

### 4. Implement OnAuthorize

Override `OnAuthorize` to implement your authorization logic:

```csharp
protected override ValueTask<AuthorizeResult> OnAuthorize(
    ServerSessionContext<ClientProxy> context,
    int methodId,
    string methodName,
    ReadOnlyMemory<int> requiredPermissions)
{
    var identity = context.Identity;
    // Check permissions against your user/role system
    // requiredPermissions contains the int-cast enum values from the attribute

    return new ValueTask<AuthorizeResult>(
        HasPermissions(identity, requiredPermissions)
            ? AuthorizeResult.Allowed
            : AuthorizeResult.Unauthorized);
}
```

### 5. Handle Unauthorized on Client

```csharp
try
{
    await client.Proxy.AdminOnly();
}
catch (ProxyUnauthorizedException)
{
    // Server denied the invocation
}
```

## Authorization Results

| Result | Behavior |
|--------|----------|
| `Allowed` | Method invocation proceeds normally |
| `Unauthorized` | Returns error to caller without invoking the method. Collections silently drop the request |
| `Disconnect` | Immediately disconnects the session. Use for collections or severe violations |

If `OnAuthorize` throws an exception, the session is disconnected as a fail-safe to prevent accidental authorization bypass.

## Authorization Caching

Authorization results can be cached per-session with configurable TTL to avoid calling `OnAuthorize` on every invocation. Caching is disabled by default.

### Server-Wide Default

```csharp
var serverConfig = new TcpServerConfig
{
    EndPoint = endpoint,
    AuthorizationCacheDuration = TimeSpan.FromSeconds(30)  // null = disabled (default)
};
```

### Per-Method Override

```csharp
[NexusAuthorize<Permission>(Permission.Read, CacheDurationSeconds = 60)]   // 60s override
[NexusAuthorize<Permission>(Permission.Admin, CacheDurationSeconds = 0)]   // Never cache
[NexusAuthorize<Permission>(Permission.Write)]                              // Use server default
```

### Cache Resolution

| Method Attribute | Server Config | Effective Cache |
|------------------|---------------|-----------------|
| Not set (`-1`) | `null` | No caching |
| Not set (`-1`) | `30s` | 30s |
| `0` | `30s` | No caching (explicit disable) |
| `60` | `30s` | 60s (method wins) |
| `10` | `null` | 10s (method wins) |

Only `Allowed` and `Unauthorized` results are cached. `Disconnect` and exception paths are never cached. The cache is per-session and automatically cleared on reconnection.

### Explicit Invalidation

Inside nexus methods:

```csharp
InvalidateAuthorizationCache();           // Clear all cached results for this session
InvalidateAuthorizationCache(methodId);   // Clear a specific method's cached result
```

## Compile-Time Diagnostics

The source generator enforces correct usage with compile-time errors:

| ID | Description |
|----|-------------|
| NEXNET024 | `[NexusAuthorize]` used on a client nexus (server-only feature) |
| NEXNET025 | `[NexusAuthorize]` used but `OnAuthorize` is not overridden |
| NEXNET026 | Mixed permission enum types across `[NexusAuthorize]` attributes in the same nexus |
| NEXNET027 | Permission enum is not backed by `int` (the default underlying type) |

## See Also

- [Authentication](authentication.md) — Token-based connection authentication
- [Synchronized Collections](synchronized-collections.md) — Collection-level authorization
