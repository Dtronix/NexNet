# NexNet v0.20.0

*Unreleased*

## Behaviour changes

## New features

- **Remote exception detail.** A failed call's `ProxyRemoteInvocationException` now has `RemoteErrorCode`, `RemoteExceptionType` and `RemoteMessage`. The executing side decides what is sent: `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's type and message (off by default, as it can reveal internals), and overriding `OnInvocationException` on a nexus sends an application error code and message instead. The detail is an optional element of the Exception result that older peers ignore; with the default settings the bytes on the wire are unchanged. See [Hub Invocations](../hub-invocations.md#errors).
- **Reconnection policy decides which disconnect reasons reconnect.** `IReconnectionPolicy` has a new `ShouldReconnect(DisconnectReason)` with a default implementation, so existing policies are unchanged. `DefaultReconnectionPolicy.ReconnectReasons` sets the reasons; the default is still `SocketError`, `Timeout` and `ServerRestarting`. Add `ServerShutdown` to reconnect to a server that restarts by stopping. See [Sessions & Lifetimes](../sessions-and-lifetimes.md#automatic-reconnection).

## Diagnostics

## Documentation

- Hub Invocations has a new Errors section; the protocol specification describes the Exception result's detail (§4.4).
- Sessions & Lifetimes: Automatic Reconnection now says reconnection needs `ClientConfig.ReconnectionPolicy` (it is off by default) and which reasons are reconnected for.
