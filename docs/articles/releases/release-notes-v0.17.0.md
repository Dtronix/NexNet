# NexNet v0.17.0

*Released 2026-10-08*

## Highlights

NexNet now uses its **own MessagePack serializer** and **MemoryPack is removed**. The wire format is portable, endian-defined and documented in a standalone **protocol specification**, and NexNet core has no third-party dependencies.

## Breaking Changes

- **Protocol ([#78](https://github.com/Dtronix/NexNet/pull/78)).** The protocol version byte stays `1`, but message bodies are incompatible with 0.16. A 0.16 peer is disconnected with `ProtocolError` after the preamble. Upgrade clients and servers together.
- **`[MemoryPackable]` is replaced by `[NexusObject]` ([#78](https://github.com/Dtronix/NexNet/pull/78)).** Mark every serialized member with `[NexusKey(n)]`, or register a custom `NexusFormatter<T>`. Types used only at runtime need `[assembly: NexusSerializable<T>]`; analyzer NEXNET038 reports missing ones.
- **`INexusDuplexUnmanagedChannel` is removed ([#78](https://github.com/Dtronix/NexNet/pull/78)).** Use `INexusDuplexChannel<T>`. Primitive arrays are written as one block (ext 78).
- **`HashLock` values changed ([#78](https://github.com/Dtronix/NexNet/pull/78)).** The hash is now built from the structure of what goes on the wire, including return types, return kind and `[NexusCollection]` item types. Names of types, members and enum members are no longer hashed, so renames keep the lock. Rebuild, then copy the new value from the compile error into each `[NexusVersion(HashLock = ...)]`.
- **`MsgPackWriter.FixedWidth` and the `Write*Forced` helpers are removed ([#78](https://github.com/Dtronix/NexNet/pull/78)).**

## What's New

- **MessagePack serializer ([#78](https://github.com/Dtronix/NexNet/pull/78)).** `MsgPackWriter` / `MsgPackReader`, built-in formatters for BCL types and collections, and generator-emitted formatters for `[NexusObject]`, `[NexusUnion<T>]` and `[assembly: NexusFormatter<TFormatter, T>]`. New diagnostics NEXNET028–034 and NEXNET036–038. Object graphs are 20–40% smaller on the wire than with MemoryPack; invocation round trips are equal or faster.
- **Untrusted-input hardening ([#78](https://github.com/Dtronix/NexNet/pull/78)).** Depth limit, strict UTF-8, length checks before allocation and randomized hashing, on by default. Configure with `ConfigBase.SerializerOptions` (`Untrusted` / `Trusted`).
- **Wire protocol specification ([#78](https://github.com/Dtronix/NexNet/pull/78)).** `docs/internals/protocol-specification.md` documents framing, protocol messages and payload encoding.
- **Native AOT without warnings ([#78](https://github.com/Dtronix/NexNet/pull/78)).** All remaining AOT warnings, including the MemoryPack ones, are gone. NexNet, NexNet.Quic and NexNet.Asp set `IsAotCompatible`.
- **Fuzzing ([#78](https://github.com/Dtronix/NexNet/pull/78)).** Six SharpFuzz/libFuzzer harnesses, including one that drives a full `NexusServer` session per input. All six ran 24 hours clean (4.67 billion executions).
- **Documentation ([#78](https://github.com/Dtronix/NexNet/pull/78)).** New serialization article; channels, versioning and README updated for the new serializer and hash rules.

## Bug Fixes & Misc

These session bugs were found by the new session fuzz harness and are also present in 0.16.0:

- Fixed an unknown message type reaching an out-of-range `Slice` before disconnecting ([#78](https://github.com/Dtronix/NexNet/pull/78))
- Fixed a stream ending inside the preamble spinning the read loop until the handshake timeout ([#78](https://github.com/Dtronix/NexNet/pull/78))
- Fixed a reused `NexusPipeManager` attaching a late pipe to another connection's session; managers are no longer pooled ([#78](https://github.com/Dtronix/NexNet/pull/78))
- Fixed `RegisterPipe` / `RentPipe` racing with `CancelAll` and leaving stale pipes ([#78](https://github.com/Dtronix/NexNet/pull/78))
- Fixed sends racing with disconnect on the output `PipeWriter` ([#78](https://github.com/Dtronix/NexNet/pull/78))

Other changes:

- Warnings are treated as errors for every project, including NuGet audit advisories ([#78](https://github.com/Dtronix/NexNet/pull/78))
- The generator no longer ships the test-only `TypeHasherTestGenerator` ([#78](https://github.com/Dtronix/NexNet/pull/78))
- Added integration tests documenting the current invocation lifetime behavior: no execution deadline, slot starvation by hung invocations, and no cancellation on disconnect ([#78](https://github.com/Dtronix/NexNet/pull/78))
- NuGet packages are now published with nuget.org trusted publishing (OIDC) instead of a stored API key ([#78](https://github.com/Dtronix/NexNet/pull/78))

## Full Changelog

https://github.com/Dtronix/NexNet/compare/v0.16.0...v0.17.0
