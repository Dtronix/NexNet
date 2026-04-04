# NexNet AOT Feasibility Analysis

## Summary

NexNet is **AOT-compatible** with the fixes applied on this branch.
All NexNet-originated AOT/trim warnings have been resolved.
The only remaining warnings are from the upstream MemoryPack.Core 1.21.4 dependency.

## Test Results

- Full solution build: **0 warnings, 0 errors**
- Integration tests: **2622 passed, 0 failed**
- Generator tests: **148 passed, 0 failed**
- AOT publish analysis: **Only upstream MemoryPack warnings remain**

## Fixes Applied

### Fix #1 (HIGH) - AOT-safe argument serialization
**Files:** `IProxyInvoker.cs`, `ProxyInvocationBase.cs`, `SessionInvocationStateManager.cs`,
`ISessionInvocationStateManager.cs`, `InvocationMessage.cs`, `NexusBroadcastClient.cs`,
`MethodEmitter.cs` (source generator)

Changed the proxy invocation pipeline from passing `ITuple?` (which required
`MemoryPackSerializer.Serialize(arguments.GetType(), arguments)` at runtime) to passing
pre-serialized `Memory<byte>`. The source generator now emits
`MemoryPackSerializer.Serialize(__proxyInvocationArguments)` at the call site where the
concrete `ValueTuple<T1, T2, ...>` type is known at compile time.

### Fix #2 - `DeserializeArguments<T>()` annotation
**Files:** `InvocationMessage.cs`, `IInvocationMessage.cs`

Added `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]` to the `T`
parameter on both the interface and implementation to satisfy MemoryPack's requirements.

### Fix #3 - `TryGetResult<T>()` annotation
**File:** `InvocationResultMessage.cs`

Added `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]` to the `T` parameter.

### Fix #4 - `MessagePool<T>` and `PoolManager` annotations
**Files:** `MessagePool.cs`, `PoolManager.cs`

Added `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]` to the `T`
parameter on `MessagePool<T>`, `PoolManager.Pool<T>()`, and `PoolManager.Rent<T>()`.

### Fix #5 - `SocketConnection.Counters` reflection removal
**File:** `SocketConnection.cs`

Replaced the `System.Reflection`-based `Delegate.CreateDelegate` / `GetProperty` approach
for accessing `Pipe.Length` (a non-public property) with `[UnsafeAccessor]`, which is
AOT-native and has zero overhead.

### Fix #6 - `Enum.GetValues(Type)` AOT incompatibility
**File:** `Helpers.cs`

Replaced `Enum.GetValues(typeof(Counter))` with `Enum.GetValues<Counter>()`.

## Remaining Upstream Warnings

| Warning | Source | Notes |
|---------|--------|-------|
| IL2104 | MemoryPack.Core 1.21.4 | Assembly-level trim warnings |
| IL3053 | MemoryPack.Core 1.21.4 | Assembly-level AOT warnings |
| IL3050 | MemoryPack.Core 1.21.4 | `Enum.GetValues(Type)` in MemoryPack internals |

These are all within the MemoryPack package and cannot be fixed in NexNet.
MemoryPack uses source-generated formatters, so these warnings are likely
false positives for the types actually used. A future MemoryPack update
may resolve these.
