# NexNet v0.21.0

*Unreleased*

## Behaviour changes

- **Behavior change: a call canceled before it is sent throws `TaskCanceledException`.** When the `CancellationToken` of a `ValueTask` or `ValueTask<T>` method is already canceled, or is canceled while the call waits for invocation credit, the call now throws `TaskCanceledException`. Before, `ValueTask` completed successfully and `ValueTask<T>` returned `default`, so a canceled call could not be told apart from a real result. As before, nothing is sent and no invocation ID is used. Fire-and-forget (`void`) methods and broadcasts are unchanged. See [Hub Invocations: Cancellation](../hub-invocations.md#cancellation).
- **Behavior change: the client gets its authentication token before the transport connects.** `ClientConfig.Authenticate` is now called before the connection opens rather than after. If it throws, no connection is opened: `TryConnectAsync` returns a failed `ConnectionResult` (`StateValue.Exception`, `DisconnectReason.Authentication`) carrying the exception, and `ConnectAsync` rethrows it. Before, the exception escaped `TryConnectAsync` and left the transport open. See [Authentication](../authentication.md#asynchronous-token).
- **Plaintext transports log a warning.** A client on plain TCP, `ws://` or `http://` logs `NexNet: unencrypted transport (<transport>); credentials and data are visible on the network.` at Warning level, once per client instance on its first connect. UDS, TLS, QUIC, `wss://` and `https://` do not warn. A custom `ClientConfig` subclass warns, named by its type, unless it overrides `IsSecureTransport` to return true. See [Authentication: Plaintext Transports](../authentication.md#plaintext-transports).

## New features

- **`ClientConfig.AuthenticateAsync`.** An asynchronous authentication token provider, `Func<CancellationToken, ValueTask<Memory<byte>>>`, for tokens fetched or refreshed from an identity provider. It is called on every connect and reconnect, before the transport connects, and must complete within `HandshakeTimeout`. Setting both `Authenticate` and `AuthenticateAsync` is a configuration error. See [Authentication](../authentication.md#asynchronous-token).
- **`INexusClient.IsSecure`.** True when the client's transport is encrypted or local (TLS, `wss://`, `https://`, QUIC, UDS); false for plain TCP, `ws://` and `http://`. Client snapshots carry it as `NexusSessionSnapshot.IsSecure` (null on server sessions). `ClientConfig.IsSecureTransport` defaults to false, so a custom `ClientConfig` subclass reports `IsSecure` false unless it overrides it.

## Fixes

## Documentation
