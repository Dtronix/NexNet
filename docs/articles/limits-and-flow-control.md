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
| `MaxConcurrentConnectionInvocations` | 2 / 32 | 1 – 1000 | Invocations from one session that run at the same time on this (the receiving) side. |
| `MaxSeparatePoolInvocations` | 4 / 16 | 1 – 1000 | Invocations of `[NexusMethod(SeparatePool = true)]` methods from one session that run at the same time on this side, in their own pool. |
| `InvocationQueueWarningThreshold` | 5 s | 0 – 10 min (0 disables) | An invocation that waits longer than this for an execution slot logs a warning, at most once every 30 s per session. |
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

A slow plain method holds its slot until it returns. Mark it `[NexusMethod(SeparatePool = true)]` and it runs in a separate pool of the session instead: its own FIFO queue and `MaxSeparatePoolInvocations` slots, so it never takes a `MaxConcurrentConnectionInvocations` slot and a full separate pool never holds back plain calls (nor the other way round). Both queues together hold at most `InvocationCredits` invocations; the credit is returned when an invocation of either pool starts. It is not `TaskCreationOptions.LongRunning`: the method runs on the thread pool, so it must be truly asynchronous (`Process.WaitForExitAsync`, not `WaitForExit()` or `.Result`), and the cap is per session, not server-wide. See [Hub Invocations: Slow methods](hub-invocations.md#slow-methods).

How long invocations wait for a slot is recorded by the `nexnet.invocation.queue_wait` histogram (tagged `nexnet.invocation.pool` = `normal` or `separate`), and a session snapshot shows how long the oldest queued invocation has waited (`OldestQueuedWait`, and `SeparatePoolOldestQueuedWait` for the separate pool). `InvocationQueueWarningThreshold` applies to both pools. See [Metrics and Diagnostics](metrics.md).

Invocations sent to several sessions (`All`, `Others`, `Groups`, `Clients`) are delivered in two passes:

1. The invocation is written immediately to every session that can take it without waiting: it has credit, and nothing else is being sent to it.
2. The sessions that were busy are waited for together, at most 256 at a time and all within one `ProgressTimeout`. A session that still has no credit by then is skipped for this invocation, with a warning in the log.

A client that is slow or unresponsive therefore delays neither the other clients nor the broadcast by more than one `ProgressTimeout`, however many such clients there are. Each client receives the broadcasts from one caller in the order they were made.

## Pipes

Each direction of a pipe has its own window of `PipeWindow` bytes. The writer sends only what the reader has granted and waits in `FlushAsync` for more; the reader grants credit back as the application consumes data. The window is reserved from the session budget while the pipe is open.

Opening a pipe beyond `MaxPipes`, or when the budget can't hold another window, fails with `NexusBusyException`: from `CreatePipe` on the opening side, or from `ReadyTask` when the remote side refuses it. The session stays connected.

An application that frames its own messages on a raw pipe must keep each message within `PipeWindow`, as the writer can't send more than one window ahead of the reader. Channels handle larger items themselves.

## Channels

Each channel item is sent with its length in front of it. The reader waits until a whole item has arrived. An item larger than the pipe's window gets extra credit for the difference, reserved from the session budget until the item is consumed.

An item may be at most `MaxMessageSize` bytes. Writing a larger one throws `NexusMessageTooLargeException` and sends nothing. Receiving one, or an item that does not decode to exactly its length, resets the channel's pipe without disconnecting the session, with a warning logged at most once every 10 seconds per session.

## Sizing PipeWindow and SessionBudget

A pipe can have at most one window of data in flight, so its throughput is limited to `PipeWindow` divided by the round-trip time:

| Round-trip time | 256 KiB window | 1 MiB window | 4 MiB window |
|-----------------|---------------:|-------------:|-------------:|
| 1 ms (LAN) | 256 MB/s | 1 GB/s | 4 GB/s |
| 20 ms | 13 MB/s | 52 MB/s | 210 MB/s |
| 40 ms | 6.6 MB/s | 26 MB/s | 105 MB/s |
| 100 ms | 2.6 MB/s | 10 MB/s | 42 MB/s |

For example, at 40 ms the default 256 KiB window gives about 6.6 MB/s (262,144 B ÷ 0.04 s) per pipe, and the Trusted 1 MiB window about 26 MB/s. At 100 ms they give about 2.6 MB/s and 10.5 MB/s. This bound applies however fast the link is; adding bandwidth does not help, a larger window does.

For bulk transfers over long distances, raise `PipeWindow` on the receiving side to at least the link's bandwidth multiplied by its round-trip time. The window is set by the side that receives: a client uploading to a server needs the larger window in the server's config, a download in the client's.

### Relays

A relay (`ForwardToAsync` or `BridgeAsync`, see [Duplex Pipes](duplex-pipes.md#relaying)) is two pipes on two sessions, and each one has its own window and round-trip time. The bound applies per hop: each hop carries at most its window ÷ its RTT, and the slowest hop bounds the end-to-end throughput, since a slow destination stalls the source. Each hop's window is set by its receiving side: for client A → relay → client B, the relay's config sets the window of the first hop and client B's config the window of the second. Raise the window on the hop with the longest round-trip time.

### Fitting the budget

Every open pipe reserves its window from the session budget, so raising `PipeWindow` means raising `SessionBudget` too. Building a client or server checks:

```
MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64 KiB ≤ SessionBudget
```

| Configuration | Reservation | Budget |
|---------------|-------------|--------|
| Defaults | 32 × 256 KiB + 4 MiB + 32 × 64 KiB = 14 MiB | 16 MiB |
| `NexusSerializerOptions.Trusted` defaults | 32 × 1 MiB + 16 MiB + 128 × 64 KiB = 56 MiB | 64 MiB |
| `PipeWindow` = 4 MiB, other defaults | 32 × 4 MiB + 4 MiB + 32 × 64 KiB = 134 MiB | raise `SessionBudget` to at least 134 MiB |
| `PipeWindow` = 4 MiB, `MaxPipes` = 4 | 4 × 4 MiB + 4 MiB + 32 × 64 KiB = 22 MiB | raise `SessionBudget` to at least 22 MiB |

The Trusted defaults (1 MiB window, 64 MiB budget) already suit links up to about 40 ms at tens of MB/s per pipe. Lowering `MaxPipes` keeps the budget small when only a few pipes carry bulk data.

### Is a pipe window-bound?

The `nexnet.pipe.credit_wait` histogram records how long pipe writers waited for window credit (see [Metrics and Diagnostics](metrics.md#flow-control)).

The session snapshot (see [Metrics and Diagnostics](metrics.md#session-snapshot)) lists each pipe in `NexusSessionSnapshot.Pipes`. A pipe's `WaitingForCredit` is null while its writer is not waiting, and otherwise how long it has waited for send credit (with `WaitingBytes` still to send). A `WaitingForCredit` that is non-null and keeps growing on the sending side means the writer is window-bound: the receiver is reading slowly, or the round-trip time is the limit. If the receiver's `Buffered` stays near zero while the writer waits, the round-trip time is the limit and a larger window helps; if `Buffered` stays near the window, the receiving application is the limit.

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

Waiting for credit is never a stall: a writer blocked on a slow reader waits as long as the reader takes. When the timeout is reached the session disconnects with `DisconnectReason.Stalled`, and the warning logged names what made no progress. [Metrics and Diagnostics](metrics.md) shows how to watch budgets, windows and queues before that happens.

A peer that sends more than it was granted, on a pipe or in invocations, is disconnected with `DisconnectReason.FlowControl`. This only happens with a faulty or hostile peer.

## See Also

- [Duplex Pipes](duplex-pipes.md) — Byte streaming
- [Channels](channels.md) — Typed streaming
- [Hub Invocations](hub-invocations.md) — Method signatures and argument rules
- [Rate Limiting](rate-limiting.md) — Connection limits and unauthenticated connections
- [Sessions & Lifetimes](sessions-and-lifetimes.md) — Ping, timeouts and reconnection
