# NexNet v0.18.0

*Released 2026-10-20*

## Highlights

**Flow control and limits for every session.** A slow reader, a large value or a busy method now slows down only its own operation: other pipes, invocations, results and pings on the same session keep flowing. Every session runs within a fixed memory budget, and limits fail the operation that reached them instead of the connection.

- **Large arguments and results.** Values up to `MaxMessageSize` (4 MiB by default) are streamed automatically; the 65,526-byte argument limit is gone.
- **Per-pipe flow control.** Each pipe has its own credit window, so a slow reader can no longer stall the session or hang the server.
- **Broadcasts don't wait on slow clients.** Healthy clients receive a broadcast immediately; unresponsive ones are waited for together, at most `ProgressTimeout`.
- **Hostile-peer limits.** Pre-authentication frame limits, a cap on pending handshakes, decode element limits and stall detection.

---

## Breaking Changes

- **Protocol.** Message bodies are incompatible with 0.17. The protocol version byte stays `1` (it changes only from 1.0 on), so a 0.17 peer passes the preamble and is disconnected with `ProtocolError` at the first message it can't parse. Upgrade clients and servers together.
- **Removed settings.** `NexusPipeFlushChunkSize`, `NexusPipeHighWaterMark`, `NexusPipeLowWaterMark`, `NexusPipeHighWaterCutoff` and `NexusSerializerOptions.MaxBufferedItemSize`. Use `PipeWindow`, `SessionBudget` and `MaxMessageSize` (see [Limits and Flow Control](../limits-and-flow-control.md)).
- **New limits are enforced.**
  - At most `MaxPipes` (32) pipes, channels and collection connections per session; opening more throws `NexusBusyException`.
  - Arguments, results and channel items are limited to `MaxMessageSize` (4 MiB, or 16 MiB with trusted serializer options); larger ones throw `NexusMessageTooLargeException`. Previously arguments over 65,526 bytes threw `ArgumentOutOfRangeException`.
  - Building a client or server throws `InvalidOperationException` if `SessionBudget` can't hold `MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64 KiB`.
- **`IMethodInvoker` signatures.** `RegisterDuplexPipe(byte, IInvocationMessage)`, `RegisterCancellationToken(IInvocationMessage)` and `ReturnCancellationToken(IInvocationMessage, CancellationTokenSource)`. Only code that implements or calls `IMethodInvoker` directly is affected; keep `NexNet` and `NexNet.Generator` on the same version.
- **Invocations may run out of order.** The read loop queues invocations and runs up to `MaxConcurrentConnectionInvocations` at a time; it no longer waits for a free slot.
- **Fire-and-forget cancellation.** A method invoked without waiting for its result (a broadcast, or a `ValueTask` method invoked on several clients) gets a token that is canceled when the session ends. Previously its token was never canceled.

## New Features

### Large values

- Arguments and results larger than one 64 KiB frame are streamed in frames and reassembled by the receiver, which reserves the size from its session budget first. A receiver without room fails the call with `NexusBusyException`; the session stays connected.

### Flow control

- **Pipes:** a receive window per pipe (`PipeWindow`, 256 KiB or 1 MiB trusted). Writers wait in `FlushAsync` for credit; readers grant it back as they consume. Closing is acknowledged by both sides.
- **Channels:** items are length-prefixed. An item larger than the pipe's window gets extra credit from the session budget; an item that breaks the framing resets only that channel's pipe.
- **Invocations:** each side advertises how many invocations it queues (`InvocationCredits`); callers wait locally for credit instead of overrunning the peer. Pipe and channel methods free their execution slot once their pipe is registered.
- **Session budget:** `SessionBudget` bounds the receive memory of a session; pipe windows, the invocation queue and large values are reserved from it.

### Limits and timeouts

- `ProgressTimeout` (30 s) disconnects with `DisconnectReason.Stalled` when an operation holding session memory stops making progress. Waiting for a slow reader is never a stall.
- A peer that sends beyond the credit it was granted is disconnected with `DisconnectReason.FlowControl`.
- `NexusSerializerOptions.MaxElements` limits array elements and map entries per value (default `max(65536, MaxMessageSize / 16)`).
- Before authentication: frames over 16 KiB are rejected before they are buffered, `ServerConfig.MaxPendingHandshakes` (256) caps connections that have not authenticated, `OnAuthenticate` runs off the read loop, and pre-authentication failures are logged as one aggregated warning per interval.

### Broadcasts

- Broadcasts are written immediately to every client that can take them; busy clients are waited for concurrently, at most 256 at a time, within one `ProgressTimeout`. A single-frame broadcast is encoded once and the same bytes are copied to every client.

## Bug Fixes

- **Session hang under load.** A pipe or channel buffer that reached its cutoff blocked the session's read loop, so a channel item at or above the cutoff (or a partial item) stopped every other pipe, invocation, result and ping on the session until the ping timeout.
- **Back-pressure did not reach the writer.** The pause flags paused the reader's own writer instead of the remote one.
- A message larger than one frame broke every later send on the session; it now fails alone.
- Disconnecting from another thread could let the read loop process a buffer the transport had already recycled.
- The collection server flushed a channel's pipe writer concurrently with the session's writer.
- A fire-and-forget method that threw did not return its arguments to the pool.
- Two fire-and-forget calls running at once could exchange cancellation token sources, returning one to the pool while still in use.
- Cancelling a call while it waited for invocation credit could complete its state twice (`InvalidOperationException`).
- Unsolicited invocation results were not returned to their pool; cancellation registrations outlived their invocation state.
- The transport receive pipe paused below one maximum frame (64 KiB); it now pauses at 256 KiB and resumes at 128 KiB.

## Performance

Measured against 0.17.0 on the same machine (BenchmarkDotNet, `src/NexNetBenchmarks`).

- **Invocations:** round-trip times unchanged (within ±4%); 5–7 B more allocated per call with trusted serializer options, 13–28 B untrusted (invocation credit returns). With 8 and 32 concurrent callers, calls are 15–20% faster.
- **Pipe uploads:** 64 MiB at 753 MB/s over TCP (+20%) and 1,486 MB/s over UDS (+4%), with 1.7–4.7× fewer allocations. A 1 MiB upload over TCP, which failed in 0.17.0, runs at 573 MB/s.
- **Invocations with a pipe argument:** about 10% faster, with 2.6× fewer allocations.
- **Invocations while a pipe streams:** about 8% slower (107 µs), with less than half the allocations.
- **Large values:** a 4 MiB argument echoed back as the result in 13.5 ms (311 MB/s each way).
- **Broadcasts with an unresponsive client:** healthy clients receive in about 0.25 ms instead of waiting for the client's timeout.
- **Channels:** small items are 10–20% slower because of the length prefix; arrays of 256 integers are unchanged.
- **High-latency links:** a pipe's throughput is limited to `PipeWindow` ÷ round-trip time (12.7 MB/s at 20 ms with the default 256 KiB window), where 0.17.0 timed out. Raise `PipeWindow` for long-distance bulk transfers.

## Migration Guide from v0.17.0

### Required Changes

1. **Upgrade clients and servers together.** 0.17 and 0.18 peers can't connect to each other.
2. **Remove the deleted settings** (`NexusPipeFlushChunkSize`, `NexusPipeHighWaterMark`, `NexusPipeLowWaterMark`, `NexusPipeHighWaterCutoff`, `MaxBufferedItemSize`). If you tuned them, set `PipeWindow` and `SessionBudget` instead.
3. **Sessions with more than 32 concurrent pipes, channels or collections** must raise `MaxPipes` (and `SessionBudget`), or handle `NexusBusyException`.
4. **Code that implements `IMethodInvoker`** must adopt the new signatures.

### Optional Improvements

- Remove application-level chunking of arguments larger than 64 KiB; values up to `MaxMessageSize` are sent directly.
- Handle `NexusBusyException` and `NexusMessageTooLargeException` where pipes are opened or large values are sent.
- For bulk transfers over high-latency links, raise `PipeWindow` to at least bandwidth × round-trip time.

## Documentation

- New: [Limits and Flow Control](../limits-and-flow-control.md), [Release Notes](index.md).
- Updated: duplex pipes, channels, hub invocations, serialization, sessions and lifetimes, rate limiting.
- The wire protocol specification describes framing, large messages, pipe credit, channel item framing, invocation credits and limits.
