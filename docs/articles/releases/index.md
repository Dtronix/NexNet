# Release Notes

Per-version release notes for NexNet. Each page is also the body of the matching
[GitHub release](https://github.com/Dtronix/NexNet/releases): the release build for a `vX.Y.Z` tag publishes
`release-notes-vX.Y.Z.md` and fails if it is missing.

| Version | Released | Highlights |
|---------|----------|------------|
| [v0.18.0](release-notes-v0.18.0.md) | 2026-10-20 | Flow control and limits for every session: large values streamed up to `MaxMessageSize`, per-pipe credit windows, invocation credits, a session memory budget, stall detection and pre-authentication limits. |
| [v0.17.0](release-notes-v0.17.0.md) | 2026-10-08 | NexNet now uses its own MessagePack serializer and MemoryPack is removed. |
| [v0.16.0](release-notes-v0.16.0.md) | 2026-10-03 | NexNet is now Native AOT compatible, `HashLock` values are deterministic, and all timers and timeouts can be driven by a configurable `TimeProvider`. |
| [v0.15.0](release-notes-v0.15.0.md) | 2026-03-16 | This release upgrades to .NET 10 and introduces method-level authorization — a declarative, source-generator-driven permission system for securing server nexus methods and collections. |
| [v0.14.1](release-notes-v0.14.1.md) | 2025-12-30 | Additional security fixes ([#63](https://github.com/Dtronix/NexNet/pull/63)). |
| [v0.14.0](release-notes-v0.14.0.md) | 2025-12-30 | This release introduces connection rate limiting for DoS protection, remote endpoint access, critical bug fixes, and internal code cleanup. |
| [v0.13.0](release-notes-v0.13.0.md) | 2025-12-22 | Session Management Abstraction; Security Hardening and Performance Fixes; Pool Consolidation and Memory Safety Improvements |
| [v0.12.0](release-notes-v0.12.0.md) | 2025-12-17 | Collection system overhaul (server, client and relay lists), client connection pooling, logging rework, incremental generator. |
| [v0.11.0](release-notes-v0.11.0.md) | 2025-08-10 | Protocol versioning and security: interface type hashing, invocation verification and handshake validation. |
| [v0.10.0](release-notes-v0.10.0.md) | 2025-06-17 | Additional Integration Tests; Distributed Collections |
| [v0.9.1](release-notes-v0.9.1.md) | 2025-05-02 | Maintenance release; no notes were published. |
| [v0.9.0](release-notes-v0.9.0.md) | 2025-04-15 | EPIC: WebSocket, HttpSocket Transports & ASP Integration |
| [v0.8.1](release-notes-v0.8.1.md) | 2025-02-18 | No notes were published. |
| [v0.8.0](release-notes-v0.8.0.md) | 2024-01-12 | No notes were published. |
| [v0.7.0](release-notes-v0.7.0.md) | 2024-01-11 | Invocation Logging |
| [v0.6.0](release-notes-v0.6.0.md) | 2023-11-29 | Use DotNet 8 |
| [v0.5.0](release-notes-v0.5.0.md) | 2023-10-23 | Pipe Notifications, Checks, & Extensions |
| [v0.4.0](release-notes-v0.4.0.md) | 2023-09-17 | Docs; Logging Cleanup |
| [v0.3.3](release-notes-v0.3.3.md) | 2023-09-17 | Benchmark and Duplex Pipe Concurrent Usage Fixes |
| [v0.3.2](release-notes-v0.3.2.md) | 2023-09-13 | Channel Converters; Await and Disconnection Fixes; Null Proxy Return Warning Fixes |
| [v0.3.1](release-notes-v0.3.1.md) | 2023-09-08 | Channel Extensions |
| [v0.3.0](release-notes-v0.3.0.md) | 2023-09-07 | Channels |
| [v0.2.0](release-notes-v0.2.0.md) | 2023-08-28 | Duplex pipes for binary streaming; QUIC transport. |
| [v0.1.0](release-notes-v0.1.0.md) | 2023-05-10 | Initial preview release. |

Source downloads and git tags are on the [GitHub releases page](https://github.com/Dtronix/NexNet/releases).
