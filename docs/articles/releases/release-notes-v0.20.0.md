# NexNet v0.20.0

*Unreleased*

## Behaviour changes

## New features

- **Built-in relay for pipes and channels.** `ForwardToAsync` copies what one pipe receives to another pipe with flow control (a slow destination stalls the source instead of buffering on the relay) and half-closes the destination when the source completes. `BridgeAsync` forwards both directions of two pipes and completes both when one ends abnormally. The channel overload `ForwardToAsync<T>` relays items as raw bytes without deserializing them, and throws `NexusMessageTooLargeException` before sending an item larger than the destination accepts. See [Duplex Pipes: Relaying](../duplex-pipes.md#relaying) and [Channels: Relaying](../channels.md#relaying).
- **Remote exception detail.** A failed call's `ProxyRemoteInvocationException` now has `RemoteErrorCode`, `RemoteExceptionType` and `RemoteMessage`. The executing side decides what is sent: `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's type and message (off by default, as it can reveal internals), and overriding `OnInvocationException` on a nexus sends an application error code and message instead. The detail is an optional element of the Exception result that older peers ignore; with the default settings the bytes on the wire are unchanged. See [Hub Invocations](../hub-invocations.md#errors).

## Diagnostics

## Documentation

- `Relaying` sections in [Duplex Pipes](../duplex-pipes.md#relaying) and [Channels](../channels.md#relaying).
- Hub Invocations has a new Errors section; the protocol specification describes the Exception result's detail (§4.4).
