# NexNet Native AOT Findings

## Summary

NexNet is **Native AOT compatible with zero trim/AOT warnings**. A Native AOT publish of
`NexNetSample.Aot.Client` reports no `warning IL` lines, and CI enforces this on every build.

NexNet has no third-party serializer dependency. Payloads are serialized by NexNet's own MessagePack
serializer through formatters that the source generator emits at compile time and registers from a
module initializer, so no runtime reflection or dynamic code generation is involved.

## Verification

- Local: `dotnet publish src/Samples/NexNetSample.Aot.Client -c Release -r win-x64`, run from a
  Developer PowerShell for VS (`vswhere.exe` and the MSVC linker on PATH): publish succeeded with
  **0 IL warnings** (0 warnings of any kind).
- CI (`.github/workflows/dotnet.yml`, step "Native AOT publish"): publishes the same sample for
  `linux-x64` with the trim/AOT warnings promoted to errors, and fails if the log contains any
  `warning IL` line. The grep is the dependable check, because some ILCompiler warnings bypass
  `TreatWarningsAsErrors`.
- `NexNet`, `NexNet.Quic` and `NexNet.Asp` set `<IsAotCompatible>true</IsAotCompatible>`, so the trim,
  AOT and single-file analyzers run during every library build.

`dotnet build` does not run ILCompiler; only a publish does. That is how the earlier warnings below
went unnoticed while the build-only check passed.

## Warnings fixed for 0.17

| Warning | Cause | Fix |
|---------|-------|-----|
| IL2091 | `[DynamicallyAccessedMembers(All)]` on the generic parameter of `PayloadSerializer`, `IInvocationMessage.DeserializeArguments<T>`, `InvocationResultMessage.TryGetResult<T>`, `MessagePool<T>` and `PoolManager.Pool<T>`/`Rent<T>`, instantiated with unannotated type parameters | Annotations removed; the formatter registry needs no reflection |
| IL3050 (x2) | The same `All` annotations keep every member of the message types, including nested enums and through them `Enum.GetValues(Type)`, which is `[RequiresDynamicCode]` | Same as above |
| IL2091 | `NexNet.Asp`: `AddNexusServer<TServerNexus, TClientProxy>` registers `TServerNexus` with the DI container | `TServerNexus` is annotated with `DynamicallyAccessedMemberTypes.PublicConstructors`, the requirement of `AddTransient<T>()` |

## Earlier fixes still in place

- **AOT-safe argument serialization.** Generated proxies serialize arguments at the call site, where the
  concrete types are known at compile time, and pass pre-serialized bytes to the invoker.
- **`SocketConnection.Counters`.** Reading `Pipe.Length` (non-public) uses `[UnsafeAccessor]` instead of
  reflection.
- **`Enum.GetValues`.** `Helpers.cs` uses the generic `Enum.GetValues<Counter>()`.
