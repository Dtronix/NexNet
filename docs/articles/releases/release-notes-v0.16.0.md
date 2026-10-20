# NexNet v0.16.0

*Released 2026-10-03*

## Highlights

NexNet is now **Native AOT compatible**, **`HashLock` values are deterministic**, and all timers and timeouts can be driven by a configurable **`TimeProvider`**.

## Breaking Changes

- **`HashLock` values changed ([#70](https://github.com/Dtronix/NexNet/pull/70)).** Previously they could differ between builds. Rebuild, then copy the new value from the compile error into each `[NexusVersion(HashLock = ...)]`. Upgrade clients and servers together.
- **`IProxyInvoker` takes pre-serialized arguments ([#66](https://github.com/Dtronix/NexNet/pull/66)).** `ProxyInvoke*Core` methods now take `Memory<byte>` instead of `ITuple?`. This only affects code that calls or implements `IProxyInvoker` directly. Keep `NexNet` and `NexNet.Generator` on the same version.

## What's New

- **Native AOT ([#66](https://github.com/Dtronix/NexNet/pull/66), [#74](https://github.com/Dtronix/NexNet/pull/74)).** All NexNet AOT and trim warnings are fixed. New `NexNetSample.Aot.*` samples are included, and CI fails if new AOT warnings appear. The only warnings left come from MemoryPack (IL2104, IL3053, IL3050).
- **`TimeProvider` ([#76](https://github.com/Dtronix/NexNet/pull/76)).** New `ConfigBase.Time` property (default `TimeProvider.System`). It controls ping, reconnect, timeouts, the client pool, rate limiting and the authorization cache. Use `FakeTimeProvider` in tests to control time.
- **Documentation site ([#72](https://github.com/Dtronix/NexNet/pull/72)).** New DocFX site with API reference and guides. The README is shorter.

## Bug Fixes & Misc

- Fixed duplicate items in a client `NexusList` after `EnableAsync()` ([#66](https://github.com/Dtronix/NexNet/pull/66))
- Fixed the `BufferWriter.ReleaseTo` overflow check (d2531fa)
- Added a regression test to keep generator hashing deterministic ([#73](https://github.com/Dtronix/NexNet/pull/73))
- Updated dependencies, including SourceLink 10.0.401, which clears the NU1902 vulnerability warning

> **QUIC on Linux:** libmsquic 2.5.11 / 2.6.1 and later fail the TLS handshake (`QUIC_STATUS_TLS_ERROR`) unless `SslClientAuthenticationOptions.TargetHost` is set to a DNS name. Set `TargetHost` on every `QuicClientConfig`, especially when connecting by IP address.
