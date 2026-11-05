# NexNet v0.20.0

*Unreleased*

## Behaviour changes

## New features

- **Built-in relay for pipes and channels.** `ForwardToAsync` copies what one pipe receives to another pipe with flow control (a slow destination stalls the source instead of buffering on the relay) and half-closes the destination when the source completes. `BridgeAsync` forwards both directions of two pipes and completes both when one ends abnormally. The channel overload `ForwardToAsync<T>` relays items as raw bytes without deserializing them, and throws `NexusMessageTooLargeException` before sending an item larger than the destination accepts. See [Duplex Pipes: Relaying](../duplex-pipes.md#relaying) and [Channels: Relaying](../channels.md#relaying).

## Diagnostics

## Documentation

- `Relaying` sections in [Duplex Pipes](../duplex-pipes.md#relaying) and [Channels](../channels.md#relaying).
