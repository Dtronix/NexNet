# NexNet v0.20.0

*Unreleased*

## Behaviour changes

## New features

- **Built-in relay for pipes and channels.** `ForwardToAsync` copies what one pipe receives to another pipe with flow control (a slow destination stalls the source instead of buffering on the relay) and half-closes the destination when the source completes. `BridgeAsync` forwards both directions of two pipes and completes both when one ends abnormally. The channel overload `ForwardToAsync<T>` relays items as raw bytes without deserializing them, and throws `NexusMessageTooLargeException` before sending an item larger than the destination accepts. See [Duplex Pipes: Relaying](../duplex-pipes.md#relaying) and [Channels: Relaying](../channels.md#relaying).
- **Remote exception detail.** A failed call's `ProxyRemoteInvocationException` now has `RemoteErrorCode`, `RemoteExceptionType` and `RemoteMessage`. The executing side decides what is sent: `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's type and message (off by default, as it can reveal internals), and overriding `OnInvocationException` on a nexus sends an application error code and message instead. The detail is an optional element of the Exception result that older peers ignore; with the default settings the bytes on the wire are unchanged. See [Hub Invocations](../hub-invocations.md#errors).
- **Reconnection policy decides which disconnect reasons reconnect.** `IReconnectionPolicy` has a new `ShouldReconnect(DisconnectReason)` with a default implementation, so existing policies are unchanged. `DefaultReconnectionPolicy.ReconnectReasons` sets the reasons; the default is still `SocketError`, `Timeout` and `ServerRestarting`. Add `ServerShutdown` to reconnect to a server that restarts by stopping. See [Sessions & Lifetimes](../sessions-and-lifetimes.md#automatic-reconnection).
- **UDS socket file permissions and stale-file cleanup.** `UdsServerConfig.SocketFileMode` sets the socket file's mode before the server listens (Unix only; on Windows a non-null value throws `PlatformNotSupportedException`). `UdsServerConfig.RemoveStaleSocketFile` (off by default) deletes a socket file left by a crashed server, but only when nothing listens on it and the path is confirmed to be a socket file. A graceful stop now deletes the socket file only if it is still the file the server created, instead of whatever file is at the path. See [Transports](../transports.md#socket-file-permissions-and-stale-files).

## Diagnostics

## Documentation

- `Relaying` sections in [Duplex Pipes](../duplex-pipes.md#relaying) and [Channels](../channels.md#relaying).
- Hub Invocations has a new Errors section; the protocol specification describes the Exception result's detail (§4.4).
- Sessions & Lifetimes: Automatic Reconnection now says reconnection needs `ClientConfig.ReconnectionPolicy` (it is off by default) and which reasons are reconnected for.
- Channels has a new [Cost of Writes](../channels.md#cost-of-writes) section (one flush per call, batching, framing, extra credit, transport coalescing). Limits and Flow Control replaces "Throughput over High-Latency Links" with [Sizing PipeWindow and SessionBudget](../limits-and-flow-control.md#sizing-pipewindow-and-sessionbudget): a 40 ms example, relays per hop, budget worked examples and the `WaitingForCredit` signal. `llm-usage.md` corrects the `OnDisconnected` and `OnAuthenticate` signatures, the `TryConnectAsync` result, the synchronous `Use*NexusServerAsync`, and lists the namespaces of the UDS, QUIC, WebSocket and HttpSocket configs and `IIdentity`.
