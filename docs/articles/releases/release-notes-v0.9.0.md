# NexNet v0.9.0

*Released 2025-04-15*

## NexNet.Asp – ASP.NET Core Integration Support

This release introduces a new package, **NexNet.Asp**, which adds support for hosting NexNet servers within ASP.NET Core applications. This enables deployment of NexNet behind web servers and reverse proxies such as Nginx.

### New Features

- **WebSocket and HttpSocket Transports**  
  Added support for client and server communication over WebSocket and a custom HttpSocket implementation.
- **ASP.NET Core Integration Package**  
  Introduced the `NexNet.Asp` package to separate ASP.NET-specific logic from the core NexNet server functionality.
- **Authentication Support**  
  - Clients can now send HTTP authentication headers with both WebSocket and HttpSocket connections.  
  - Server-side authentication integration with ASP.NET is supported via the `ServerConfig` classes.
- **Sample Projects**  
  Added sample client and server projects demonstrating usage of WebSocket and HttpSocket transports.
- **Additional Tests**  
  - New tests for WebSocket and HttpSocket implementations.  
  - Consolidated and cleaned up group invocation tests.

### Fixes
- Resolved a deadlock in `RegisteredInvocationState` caused by execution context capture and continuation blocking the WebSocket pipe reader. Continuations now run asynchronously.
- Addressed inconsistent test results due to execution order; QUIC tests now run first.
- Fixed a race condition in pipe disposal logic that could result in a null semaphore reference during `ReadAsync` calls.
- Updated `TaskCompletionSource` usage to ensure continuations do not block threads.
- Replaced legacy lock usage with the new `.NET 9 Lock` class.
- Stabilized tests and aligned test assertions with NUnit fluent syntax.

### Breaking Changes
- The library now targets **.NET 9**, leveraging new runtime features, including the updated `Lock` implementation. Consumers must update their target framework accordingly.

### What's Changed
* EPIC: WebSocket, HttpSocket Transports & ASP Integration by @DJGosnell in https://github.com/Dtronix/NexNet/pull/40


**Full Changelog**: https://github.com/Dtronix/NexNet/compare/v0.8.1...v0.9.0
