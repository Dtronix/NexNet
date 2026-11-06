# NexNet v0.20.0

*Released 2026-11-06*

## Highlights

- **Built-in relay for pipes and channels.** `ForwardToAsync` and `BridgeAsync` forward one pipe or channel to another with flow control ([#94](https://github.com/Dtronix/NexNet/pull/94)).
- **Slow methods in a separate pool.** `[NexusMethod(SeparatePool = true)]` runs a method outside the `MaxConcurrentConnectionInvocations` slots, so fast calls on the same connection do not queue behind it ([#101](https://github.com/Dtronix/NexNet/pull/101)).
- **Remote exception detail.** A failed call can carry an application error code and message, or the exception's type and message, to the caller ([#95](https://github.com/Dtronix/NexNet/pull/95)).
- **Send pipe for every transport.** Concurrent calls over TLS and QUIC are faster: 32 concurrent TLS round trips went from 41.3 to 7.0 µs per call. The vendored Pipelines.Sockets.Unofficial code is removed ([#93](https://github.com/Dtronix/NexNet/pull/93)).
- **Fix: a rejected authentication could reach the client as `SocketError`.** The client now always sees `DisconnectReason.Authentication` ([#102](https://github.com/Dtronix/NexNet/pull/102)).

---

## Behaviour Changes

- **`MaxConcurrentConnectionInvocations` defaults to 32 with Trusted serializer options.** With `SerializerOptions = NexusSerializerOptions.Trusted` and no explicit value, up to 32 invocations from one connection now run at once on the receiving side (was 2). Untrusted configurations keep 2, and an explicit value always wins. Set `MaxConcurrentConnectionInvocations = 2` to keep the old behaviour ([#99](https://github.com/Dtronix/NexNet/pull/99)).
- **A graceful close waits up to 2 s for buffered output on every transport.** TCP and UDS did not drain before. An abortive close drops buffered output ([#93](https://github.com/Dtronix/NexNet/pull/93)).
- **A TCP/UDS connect timeout throws a `TransportException` built from `SocketError.TimedOut`.** Before, the error came from closing the socket mid-connect ([#93](https://github.com/Dtronix/NexNet/pull/93)).
- **Writes to the underlying stream can be up to 1 MiB** (one TLS record run, QUIC send or WebSocket message). WebSocket output is now one binary message per write instead of one per pipe segment. The receiver reads the messages as one byte stream, as before ([#93](https://github.com/Dtronix/NexNet/pull/93)).
- **A UDS server's graceful stop deletes the socket file only if it is still the file the server created.** Before, it deleted whatever file was at the path ([#98](https://github.com/Dtronix/NexNet/pull/98)).

There are no breaking changes to the public API. `ITransport.User` and `IReconnectionPolicy.ShouldReconnect` are new members with default implementations, so custom transports and policies keep compiling.

## New Features

- **Built-in relay for pipes and channels.** `ForwardToAsync` copies what one pipe receives to another pipe with flow control (a slow destination stalls the source instead of buffering on the relay) and half-closes the destination when the source completes. `BridgeAsync` forwards both directions of two pipes and completes both when one ends abnormally. The channel overload `ForwardToAsync<T>` relays items as raw bytes without deserializing them, and throws `NexusMessageTooLargeException` before sending an item larger than the destination accepts. See [Duplex Pipes: Relaying](../duplex-pipes.md#relaying) and [Channels: Relaying](../channels.md#relaying) ([#94](https://github.com/Dtronix/NexNet/pull/94)).
- **`[NexusMethod(SeparatePool = true)]` for slow methods.** A marked plain method runs in a separate invocation pool of the session, with its own queue and `ConfigBase.MaxSeparatePoolInvocations` slots (4 default, 16 with Trusted serializer options, 1 – 1000), so a slow call no longer holds a `MaxConcurrentConnectionInvocations` slot and fast calls on the same connection do not queue behind it. Both queues share `InvocationCredits`. It is not `TaskCreationOptions.LongRunning`: the method runs on the thread pool and must be truly asynchronous. Decided by the executing side; the method hash and the wire are unchanged. On a pipe or channel method it has no effect and warns (NEXNET039). See [Hub Invocations: Slow methods](../hub-invocations.md#slow-methods) ([#101](https://github.com/Dtronix/NexNet/pull/101)).
- **Remote exception detail.** A failed call's `ProxyRemoteInvocationException` now has `RemoteErrorCode`, `RemoteExceptionType` and `RemoteMessage`. The executing side decides what is sent: `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's type and message (off by default, as it can reveal internals), and overriding `OnInvocationException` on a nexus sends an application error code and message instead. The detail is an optional element of the Exception result that older peers ignore; with the default settings the bytes on the wire are unchanged. See [Hub Invocations](../hub-invocations.md#errors) ([#95](https://github.com/Dtronix/NexNet/pull/95)).
- **Reconnection policy decides which disconnect reasons reconnect.** `IReconnectionPolicy` has a new `ShouldReconnect(DisconnectReason)` with a default implementation, so existing policies are unchanged. `DefaultReconnectionPolicy.ReconnectReasons` sets the reasons; the default is still `SocketError`, `Timeout` and `ServerRestarting`. Add `ServerShutdown` to reconnect to a server that restarts by stopping. See [Sessions & Lifetimes](../sessions-and-lifetimes.md#automatic-reconnection) ([#96](https://github.com/Dtronix/NexNet/pull/96)).
- **ASP.NET principal in the session.** On WebSocket and HttpSocket servers, `Context.User` is the `ClaimsPrincipal` of the request that opened the connection (`HttpContext.User`), readable in `OnAuthenticate`, `OnConnected` and every invocation. With `AspEnableAuthentication` it is the principal of `AspAuthenticationScheme` (`ApplyAuthentication` now assigns it to `HttpContext.User`). It is captured at accept and not re-validated; `OnAuthenticate` still decides. `ITransport.User` is a new default member, so custom transports keep compiling. Null for other transports and on clients. See [ASP.NET Integration](../asp-net-integration.md#the-aspnet-principal) ([#97](https://github.com/Dtronix/NexNet/pull/97)).
- **UDS socket file permissions and stale-file cleanup.** `UdsServerConfig.SocketFileMode` sets the socket file's mode before the server listens (Unix only; on Windows a non-null value throws `PlatformNotSupportedException`). `UdsServerConfig.RemoveStaleSocketFile` (off by default) deletes a socket file left by a crashed server, but only when nothing listens on it and the path is confirmed to be a socket file. See [Transports](../transports.md#socket-file-permissions-and-stale-files) ([#98](https://github.com/Dtronix/NexNet/pull/98)).
- **`ConfigBase.InvocationQueueWarningThreshold`.** An invocation that waits longer than this (5000 ms default, 0 disables, 0 – 600000) for an execution slot logs a warning naming the method and the wait, at most once every 30 seconds per session, with the number of slow invocations since the last warning ([#99](https://github.com/Dtronix/NexNet/pull/99)).

## Fixes

- **A rejected authentication could reach the client as a socket close.** The server's read loop could finish and complete the transport output before the disconnect frame (`DisconnectAuthentication`, 29) was sent. The client then reported `SocketError`, which can trigger reconnects. The session now waits for an in-progress disconnect before completing the transport. On TLS the read loop also disposed the `SslStream` under the pending send; the input reader now leaves the stream open ([#102](https://github.com/Dtronix/NexNet/pull/102)).
- **`BufferWriter` segment clearing.** A released segment now clears its memory before it returns to the segment pool. Before, it was cleared after, so a segment taken from the pool by another thread in between could lose its new memory ([#93](https://github.com/Dtronix/NexNet/pull/93)).

## Performance

Changes in [#93](https://github.com/Dtronix/NexNet/pull/93), measured on an i7-10700 over loopback:

- **Send pipe for every transport.** Sessions write into a pipe drained by a send loop, so a flush no longer holds the session's write lock across a network write, and frames queued during one write go out together in the next. TLS 1.3 / QUIC, mean per call: 32 concurrent round trips 41.3 → 7.0 µs / 28.0 → 10.6 µs; fire-and-forget 34.4 → 9.6 µs / 28.1 → 9.6 µs.
- **TCP and UDS on `Socket`.** A receive loop into a pipe and the send pipe over a `NetworkStream` replace the vendored `SocketConnection`.
- **`AsyncMutex` and `CreditGate`.** The session write lock and the invocation credit wait use pooled FIFO waiters. Allocations per call under contention dropped, e.g. 32 concurrent TCP round trips 1,168 → 233 B and 8 concurrent UDS calls 375 → 45 B. Single round trips are unchanged.
- **Invocation results written straight to the output**, without staging and copying the body. 16 KiB echo over TCP 155 → 120 µs, over TLS 217 → 158 µs.
- Duplex-pipe uploads over UDS/TCP are 3 – 10 % below the vendored `SocketConnection` across runs. TLS 64 MiB uploads allocate 1,063 KB instead of 475 KB, the same as TCP.

About 6,960 vendored lines are removed. `BufferWriter` remains and keeps its credit.

## Diagnostics

- **`nexnet.invocation.queue_wait` histogram** (`s`, tags `nexnet.role`, `nexnet.transport`): time each received invocation waited from being queued until it took an execution slot.
- **`nexnet.pipe.credit_wait` histogram** (`s`, tags `nexnet.role`, `nexnet.transport`): time a pipe writer waited for the peer to grant window credit, recorded when the wait ends.
- **`nexnet.invocation.pool` tag** (`normal`, `separate`) on `nexnet.invocation.queued`, `nexnet.invocation.executing` and `nexnet.invocation.queue_wait`. Sums over the tag equal the previous values.
- **`NexusInvocationSnapshot.OldestQueuedWait`**: how long the oldest queued invocation has waited for a slot, or null when none is queued.
- **`NexusInvocationSnapshot.SeparatePoolQueued`, `SeparatePoolExecuting`, `SeparatePoolMaxExecuting` and `SeparatePoolOldestQueuedWait`** for the separate pool; the existing fields cover the normal pool.

## Documentation

- `Relaying` sections in [Duplex Pipes](../duplex-pipes.md#relaying) and [Channels](../channels.md#relaying).
- Hub Invocations has a new Errors section; the protocol specification describes the Exception result's detail (§4.4).
- Getting Started has a section on invocation concurrency: what `MaxConcurrentConnectionInvocations` limits, its defaults and when to raise it.
- Hub Invocations explains that a caller's cancellation reaches the remote method only through its `CancellationToken` parameter, also while the invocation is queued.
- Sessions & Lifetimes: Automatic Reconnection now says reconnection needs `ClientConfig.ReconnectionPolicy` (it is off by default) and which reasons are reconnected for.
- Channels has a new [Cost of Writes](../channels.md#cost-of-writes) section (one flush per call, batching, framing, extra credit, transport coalescing). Limits and Flow Control replaces "Throughput over High-Latency Links" with [Sizing PipeWindow and SessionBudget](../limits-and-flow-control.md#sizing-pipewindow-and-sessionbudget): a 40 ms example, relays per hop, budget worked examples and the `WaitingForCredit` signal. `llm-usage.md` corrects the `OnDisconnected` and `OnAuthenticate` signatures, the `TryConnectAsync` result, the synchronous `Use*NexusServerAsync`, and lists the namespaces of the UDS, QUIC, WebSocket and HttpSocket configs and `IIdentity` ([#100](https://github.com/Dtronix/NexNet/pull/100)).

## Build

- CI runs on `ubuntu-26.04`, and its GitHub Actions moved to majors that run on Node 24 ([#103](https://github.com/Dtronix/NexNet/pull/103)).
