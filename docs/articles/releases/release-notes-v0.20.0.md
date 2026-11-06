# NexNet v0.20.0

*Unreleased*

## Behaviour changes

## New features

- **Remote exception detail.** A failed call's `ProxyRemoteInvocationException` now has `RemoteErrorCode`, `RemoteExceptionType` and `RemoteMessage`. The executing side decides what is sent: `ConfigBase.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage` sends the exception's type and message (off by default, as it can reveal internals), and overriding `OnInvocationException` on a nexus sends an application error code and message instead. The detail is an optional element of the Exception result that older peers ignore; with the default settings the bytes on the wire are unchanged. See [Hub Invocations](../hub-invocations.md#errors).

## Diagnostics

## Documentation

- Hub Invocations has a new Errors section; the protocol specification describes the Exception result's detail (§4.4).
