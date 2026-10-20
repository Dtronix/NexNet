# NexNet v0.15.0

*Released 2026-03-16*

## Highlights

This release upgrades to **.NET 10** and introduces **method-level authorization** — a declarative, source-generator-driven permission system for securing server nexus methods and collections.

## What's New

### Method-Level Authorization via `[NexusAuthorize<TPermission>]` ([#65](https://github.com/Dtronix/NexNet/pull/65))

A new opt-in authorization system that lets you secure individual server methods and synchronized collections with declarative attributes.

**Key features:**
- Decorate methods with `[NexusAuthorize<TPermission>(Permission.Read, Permission.Write)]` to require permissions
- Override `OnAuthorize` on your server nexus to implement custom authorization logic
- Authorization checks run **before argument deserialization**, preventing wasted work for unauthorized calls
- Optional per-session result caching with configurable TTL (server-wide or per-method)
- Marker-only mode (no specific permissions) for simple "is authenticated" checks

**New types and APIs:**
- `NexusAuthorizeAttribute<TPermission>` — declarative attribute for methods/collections
- `AuthorizeResult` enum — `Allowed`, `Unauthorized`, `Disconnect`
- `ProxyUnauthorizedException` — thrown on the client when a call is denied
- `OnAuthorize(context, methodId, methodName, requiredPermissions)` — virtual method to override
- `InvalidateAuthorizationCache()` / `InvalidateAuthorizationCache(int methodId)` — cache management
- `ServerConfig.AuthorizationCacheDuration` — server-wide cache default

**New source generator diagnostics:**
- **NEXNET024**: `[NexusAuthorize]` used on client nexus (error)
- **NEXNET025**: `[NexusAuthorize]` used but `OnAuthorize` not overridden (error)
- **NEXNET026**: Mixed permission enum types in same nexus (error)
- **NEXNET027**: Permission enum not backed by `int` (error)

**Example usage:**
```csharp
// 1. Define permissions
public enum Permission { Read, Write, Admin }

// 2. Decorate interface methods
public interface IServerNexus
{
    [NexusAuthorize<Permission>(Permission.Read)]
    ValueTask GetData();

    [NexusAuthorize<Permission>(Permission.Admin)]
    ValueTask<int> DeleteAll();
}

// 3. Implement authorization logic
protected override ValueTask<AuthorizeResult> OnAuthorize(
    ServerSessionContext<TProxy> context,
    int methodId,
    string methodName,
    ReadOnlyMemory<int> requiredPermissions)
{
    // Check context.Identity or your own auth system
    return ValueTask.FromResult(AuthorizeResult.Allowed);
}
```

### .NET 10 Upgrade ([#64](https://github.com/Dtronix/NexNet/pull/64))

- Upgraded target framework from `net9.0` to `net10.0` across all projects
- Removed `System.IO.Pipelines` package dependency (now included in .NET 10 runtime)
- Updated `Microsoft.AspNetCore.Mvc.Testing` to 10.0.1
- Updated `NUnit3TestAdapter` to 6.0.1
- CI updated to use .NET 10 SDK

### Bug Fixes & Misc

- Fixed broadcast message processor hanging callers on server shutdown — the processor channel writer is now completed and remaining items drained so `EnqueueWaitForResult` callers don't block indefinitely
- Fixed broadcast processor not being restartable after shutdown — the processor channel is now recreated on each `Run()` call, enabling reconnection scenarios
- Fixed race condition in `NexusBroadcastClient.EnableAsync` where `Disconnected()` could null `_client` before the completion check, causing a `NullReferenceException` during reconnection
- Fixed `SocketException` not being handled as an expected rejection in connection rate limiting tests on .NET 10 Linux
- Fixed debug build configuration (2680a50)

## Breaking Changes

- **Minimum framework version is now .NET 10.** Projects consuming NexNet must target `net10.0` or later.

## Full Changelog

https://github.com/Dtronix/NexNet/compare/v0.14.1...v0.15.0
