# Limits and Flow Control

Every session runs within a fixed amount of receive memory, and a peer can only send what the receiving side has room for. A slow reader, a large value or a busy method slows down only its own operation: other pipes, invocations, results and pings on the same session keep flowing.

## Settings

All settings are on `ConfigBase`, so they apply to every client and server configuration. Several defaults are larger when `SerializerOptions` is `NexusSerializerOptions.Trusted`.

| Setting | Default (untrusted / trusted) | Range | Limits |
|---------|-------------------------------|-------|--------|
| `SessionBudget` | 16 MiB / 64 MiB | 64 KiB – 1 GiB | Receive memory per session. Pipe windows, invocation queues and large values are reserved from it. |
| `MaxPipes` | 32 | 1 – 255 | Pipes (including channels and collection connections) open on a session at once. |
| `PipeWindow` | 256 KiB / 1 MiB | 4 KiB – 1 GiB | Bytes a peer may send on one pipe before this side reads them. |
| `MaxMessageSize` | 4 MiB / 16 MiB | 64 KiB – 1 GiB | Largest argument, result or channel item accepted. |
| `InvocationCredits` | 32 / 128 | 1 – 10,000 | Invocations the peer may have queued on this side at once. |
| `MaxConcurrentConnectionInvocations` | 2 | 1 – 1000 | Invocations from one session that run at the same time. |
| `ProgressTimeout` | 30 s | 50 ms – 10 min | Longest time an operation that holds resources may go without progress. |

Building a client or server throws `InvalidOperationException` if the budget can't hold the fixed reservations:

```
MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64 KiB ≤ SessionBudget
```

With the untrusted defaults that is 32 × 256 KiB + 4 MiB + 32 × 64 KiB = 14 MiB of the 16 MiB budget. Raise `SessionBudget` together with any of the other values.

Each side announces its `MaxMessageSize` and `InvocationCredits` when connecting. A side never sends a value larger than the smaller of both `MaxMessageSize` settings, and never queues more invocations than the peer's credits.

```csharp
var config = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    PipeWindow = 1024 * 1024,           // 1 MiB per pipe
    MaxPipes = 16,
    SessionBudget = 32 * 1024 * 1024,   // 16 × 1 MiB + 4 MiB + 32 × 64 KiB fits
};
```

## Invocations

Arguments and results are serialized whole. One up to 64 KiB is sent in a single frame; a larger one, up to `MaxMessageSize`, is split into frames automatically and reassembled by the receiver, which reserves its size from the session budget first.

| Situation | Result |
|-----------|--------|
| The value is larger than `MaxMessageSize` | The call throws `NexusMessageTooLargeException`. Nothing is sent. |
| The receiver's budget can't hold the value right now | The call throws `NexusBusyException`. It may succeed later. |
| The peer's invocation queue is full | The call waits until the peer frees a credit. A `CancellationToken` on the method ends the wait. |

The session stays connected in every case. Results follow the same rules in the other direction.

The receiving side queues up to `InvocationCredits` invocations and runs `MaxConcurrentConnectionInvocations` of them at a time. An invocation with a pipe or channel argument frees its execution slot once the pipe is registered, so long-running streams do not block other calls.

Invocations sent to several sessions (`All`, `Others`, `Groups`, `Clients`) wait up to `ProgressTimeout` for each session's credit. A session that has none by then is skipped, with a warning in the log, so one unresponsive client can't hold up a broadcast indefinitely.

## Pipes

Each direction of a pipe has its own window of `PipeWindow` bytes. The writer sends only what the reader has granted and waits in `FlushAsync` for more; the reader grants credit back as the application consumes data. The window is reserved from the session budget while the pipe is open.

Opening a pipe beyond `MaxPipes`, or when the budget can't hold another window, fails with `NexusBusyException`: from `CreatePipe` on the opening side, or from `ReadyTask` when the remote side refuses it. The session stays connected.

An application that frames its own messages on a raw pipe must keep each message within `PipeWindow`, as the writer can't send more than one window ahead of the reader. Channels handle larger items themselves.

## Channels

Each channel item is sent with its length in front of it. The reader waits until a whole item has arrived. An item larger than the pipe's window gets extra credit for the difference, reserved from the session budget until the item is consumed.

An item may be at most `MaxMessageSize` bytes. Writing a larger one throws `NexusMessageTooLargeException` and sends nothing. Receiving one, or an item that does not decode to exactly its length, resets the channel's pipe without disconnecting the session.

## Throughput over High-Latency Links

A pipe can have at most one window of data in flight, so its throughput is limited to `PipeWindow` divided by the round-trip time:

| Round-trip time | 256 KiB window | 1 MiB window | 4 MiB window |
|-----------------|---------------:|-------------:|-------------:|
| 1 ms (LAN) | 256 MB/s | 1 GB/s | 4 GB/s |
| 20 ms | 13 MB/s | 52 MB/s | 210 MB/s |
| 100 ms | 2.6 MB/s | 10 MB/s | 42 MB/s |

For bulk transfers over long distances, raise `PipeWindow` on the receiving side to at least the link's bandwidth multiplied by its round-trip time, and raise `SessionBudget` so that `MaxPipes` windows still fit. Lowering `MaxPipes` keeps the budget small when only a few pipes carry bulk data.

## Timeouts and Disconnects

| Setting | Default | Disconnects when |
|---------|---------|------------------|
| `Timeout` | 30 s | Nothing at all is received, including pings. |
| `HandshakeTimeout` | 15 s | The connection is not established and authenticated in time. |
| `ProgressTimeout` | 30 s | An operation holding resources stops making progress (see below). |

`ProgressTimeout` applies to:

- a large argument or result being received, since its last frame;
- a channel item that was given extra credit, since its last data;
- a pipe that is closing, until the peer confirms the close;
- a send to the transport that does not complete.

Waiting for credit is never a stall: a writer blocked on a slow reader waits as long as the reader takes. When the timeout is reached the session disconnects with `DisconnectReason.Stalled`.

A peer that sends more than it was granted, on a pipe or in invocations, is disconnected with `DisconnectReason.FlowControl`. This only happens with a faulty or hostile peer.

## See Also

- [Duplex Pipes](duplex-pipes.md) — Byte streaming
- [Channels](channels.md) — Typed streaming
- [Hub Invocations](hub-invocations.md) — Method signatures and argument rules
- [Rate Limiting](rate-limiting.md) — Connection limits and unauthenticated connections
- [Sessions & Lifetimes](sessions-and-lifetimes.md) — Ping, timeouts and reconnection
