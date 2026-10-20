# Flow control, large values and hostile-peer limits (protocol v2)

Status: implemented in protocol version 2; see [protocol-specification.md](protocol-specification.md) for the wire
format. Section 12 records the decisions made during implementation where this design left room.

## 1. Background

An application on NexNet 0.17.0 hung in production. Diagnosis from memory dumps, confirmed against master:

- `NexusPipeReader.BufferData` (`src/NexNet/Pipes/NexusPipeReader.cs`) is awaited inside the session read loop
  (`NexusSession.Receiving.cs`, `DuplexPipeWrite` case). Above the high-water cutoff (256 KiB) it waits until
  buffer + chunk < low-water (16 KiB). A channel reader consumes whole items only, so any item at or above the
  cutoff, or a partial item larger than low-water minus one chunk, deadlocks. Every other pipe, invocation, result
  and ping on the session stops until the ping timeout.
- The pause flags pause the reader's own writer, not the remote one, and are merged add-only. There is no working
  backpressure across the wire.
- `SendMessage` (`NexusSession.Sending.cs`) serializes into the session-wide `_bufferWriter` and runs
  `checked((ushort)…)` before the `try/finally` that resets it. An oversized result breaks every later send on
  the session while pings keep it looking alive.
- The read loop awaits `_invocationSemaphore` (default 2). Pipe/channel methods and collection subscriptions hold
  a slot for the pipe's lifetime, so two live pipes plus one call deadlock the read loop.
- Smaller defects: the flush chunk size can exceed the frame size; the transport receive pipe pauses at 64 KiB,
  below one maximum frame; the read loop awaits sends (ping replies, `NotifyState`); data for an unknown pipe id
  disconnects; unsolicited results are not disposed; the channel reader re-scans a partial item from its start
  (O(n²)); any received byte refreshes the idle timeout; cancellation registrations are never disposed.
- `ProtocolVersion` is still 1 (`NexusSession.cs`). `InvocationCancellationMessage.InvocationId` is `int` while
  every other invocation id is `ushort`.

## 2. Requirements

| Id | Requirement |
|---|---|
| R1 | The read loop never waits on application readers. A slow pipe or channel reader does not delay other pipes, invocations, results or pings. |
| R2 | No size-based deadlock. Any value up to the configured maximum completes as long as the reader keeps reading. |
| R3 | Receive memory per session is bounded by a configured budget plus fixed overhead. |
| R4 | Nothing is allocated for a value before its declared size has been checked and reserved. |
| R5 | A hostile peer causes a failed call or a logged disconnect with a specific reason, never an out-of-memory condition or a stall. |
| R6 | Frames stay at most 64 KiB (u16 length). |
| R7 | Over-limit is a typed exception; the session survives. |
| R8 | The send-path defects above are fixed. |

Maintainer decisions: no compatibility with protocol v1 peers; a value over the limit fails that call or item
and the session stays up; a peer that breaks the protocol is disconnected with a logged reason; invocations may be
dispatched out of order.

## 3. Design summary

| Area | Decision |
|---|---|
| Frames | u16 body length, unchanged. Chunk size is a constant: maximum frame body minus chunk header. `NexusPipeFlushChunkSize` is removed. |
| Read loop | Awaits only the transport. Control replies go to coalesced state drained by a control sender. Invocations are enqueued. `OnAuthenticate` runs off the read loop. |
| Send path | `_writeMutex` is kept. A control sender drains coalesced control state under the mutex. Large messages and pipe data take the mutex once per frame so other senders interleave. A writer-elected combining queue is the follow-up if benchmarks show contention. |
| Memory | One per-session budget, counted in reservations. Every grant of credit and every large-value reservation is drawn from it. No server-wide budget in this work; the host bound is sessions × budget. |
| Pipes | Per-pipe byte credit. Fixed configurable window, granted in the open acknowledgement. Pause flags and water marks are removed. |
| Channel items | Varint length prefix. Items larger than the window get an all-or-nothing extra grant. |
| Large values | Arguments and results larger than one frame are sent as `MessageStart` / `MessageChunk` / `MessageAbort`, reserved in full at `Start`. |
| Invocations | Advertised invocation credits with a coalesced `InvocationCredit` return frame. Execution concurrency stays local. Pipe and collection methods release their execution slot once the pipe registers. |
| Decode guard | Cumulative element counter per value in `MsgPackReader`. |
| Pre-auth | Frame cap checked at the header, nothing allocated before authentication, one `MaxPendingHandshakes` cap. Rate limiting and IP bans stay opt-in. |

## 4. Wire protocol v2

`ProtocolVersion` becomes 2, bumped once in the release that ships these changes. Peers with another version are
rejected by the existing preamble check.

### 4.1 New message types

Non-disconnect message types must stay outside 20–38, because bytes in that range are read as disconnect reasons
before the pre-auth allow-list is applied. A unit test asserts this for every `MessageType` value.

| Type (proposed) | Name | Body |
|---|---|---|
| 51 | `PipeWindowUpdate` | pipe id (header), `+bytes` (u32) |
| 113 | `MessageStart` | invocation id (u16), kind (Argument/Result), inner message type, `totalLen` (u32) |
| 114 | `MessageChunk` | invocation id (u16), kind; payload bytes |
| 115 | `MessageAbort` | invocation id (u16), kind, reason (Busy, TooLarge, Cancelled, Ack) |
| 116 | `InvocationCredit` | `+n` (u16) |

New disconnect reasons use the free codes 35–38, e.g. `DisconnectFlowControl` (credit or window violation),
`DisconnectResourceLimit`, `DisconnectStalled` (progress timeout).

### 4.2 Greeting

`ClientGreeting` and `ServerGreeting` each advertise:

- `MaxMessageSize`: the largest argument, result or channel item the side accepts.
- `InvocationCredits`: how many invocations the peer may have queued at this side.

The sender's limit is min(own configured limit, peer's advertised limit). The receiver still enforces its own
limits. A client's advertised values are used only after authentication succeeds. The pipe window is not in the
greeting; see 4.4.

### 4.3 Large arguments and results

A message whose serialized body exceeds one frame is sent as:

```
MessageStart[invId, kind, innerType, totalLen]
MessageChunk[invId, kind] + bytes   (repeated; payload bytes sum to totalLen)
```

Messages that fit in one frame are unchanged.

**Key.** A large-message stream is keyed by (InvocationId, kind). Direction is implied: an Argument flows from
caller to callee, a Result from callee to caller. Every invocation that sends a large argument gets a tracked
invocation id, including fire-and-forget and router invocations. Today these use id 0
(`ProxyInvocationBase.cs`) or untracked ids (`LocalInvocationRouter.cs`), which would make keys collide. The
caller keeps an invocation id reserved until both its Argument and Result keys are closed.

**Sender.** Serializes the value fully (so `totalLen` is known), checks it against the peer's `MaxMessageSize`
and throws `NexusMessageTooLargeException` locally if it is larger. A large argument's `MessageStart` consumes one
invocation credit. The sender then sends `Start` and chunks without waiting for acceptance.

**Receiver.** On `Start`:
- `totalLen` > advertised `MaxMessageSize` → protocol violation, disconnect.
- Otherwise reserve `totalLen` from the session budget. If the reservation fails, reply
  `MessageAbort(Busy)` and put the key in the aborting state.
- Segments (pooled 64 KiB arrays) are allocated as chunks arrive, not at `Start`.
- When received bytes equal `totalLen`, deserialize from the segment chain as a `ReadOnlySequence<byte>`, return
  the segments and release the reservation.

**Abort and close.**
- A receiver that rejects or abandons a key sends `MessageAbort(reason)` and moves the key to aborting. It
  discards further chunks for that key, without buffering, up to `totalLen`.
- A sender that receives `MessageAbort` stops sending and replies `MessageAbort(Ack)`.
- A sender that cancels (caller cancellation during a large argument) sends `MessageAbort(Cancelled)`.
- A key closes when the receiver has seen `totalLen` bytes, or an Ack, or a sender-side Abort.
- `Busy` surfaces to the caller as a typed exception; the session stays up.

**Validity rules.** Disconnect with `ProtocolError` on:
- `Start` for a key that is already active;
- a Result `Start` (or `InvocationResult`) for an invocation id with no outstanding call;
- a chunk that would exceed the remaining `totalLen`;
- a chunk for a key that was never started and is not aborting.

Ignore an `Abort` for an unknown key, since it can race with completion. Dispose unsolicited single-frame
`InvocationResult` messages instead of leaking them.

### 4.4 Pipe flow control

- Each pipe has a fixed receive window (`PipeWindow`). The receiver reserves it from the session budget when the
  pipe opens and grants it in the open acknowledgement (`Ready`). If the reservation fails, the open is refused
  with Busy. The writer sends nothing until it has received the grant.
- The receiver sends `PipeWindowUpdate[+n]` when the application has consumed at least half the window since the
  last update. Updates are coalesced: one pending credit counter per pipe.
- Data beyond the granted credit is a protocol violation (`DisconnectFlowControl`).
- A window over 2³¹ is a protocol violation.
- `BufferData` becomes synchronous: copy into the reserved space and signal the reader. It never waits.
- `State.ClientWriterPause`, `State.ServerWriterPause`, `NexusPipeHighWaterMark`, `NexusPipeLowWaterMark` and
  `NexusPipeHighWaterCutoff` are removed.
- Writers wait for credit in the application's task only, never in the read loop. Credit waits honour the
  writer's `CancellationToken`.

### 4.5 Channel items

- Each item is preceded by a varint length. The writer serializes the item into a pooled scratch buffer, then
  writes the varint length and the bytes.
- The writer checks the length against the peer's `MaxMessageSize` and throws `NexusMessageTooLargeException`
  locally; the item is not sent and the channel stays usable.
- The receiver reads the prefix first:
  - length > `MaxMessageSize` → reset the pipe with TooLarge and log it; the session stays up;
  - length + prefix > the pipe's window → reserve the difference from the session budget all at once and grant
    it as extra credit. Release it when the item has been consumed. If the budget cannot cover it yet, the
    reservation waits in FIFO order; only this pipe waits.
- The item is deserialized from exactly its length-sized slice. Trailing bytes, or a read past the slice, reset
  the pipe.
- The prefix removes the partial-item re-scan in `NexusChannelReader`.
- This also applies to the collection synchronization pipe. Raw `INexusDuplexPipe` users who frame their own data
  still need items to fit in the window; this is documented.

### 4.6 Pipe close

- A pipe id is freed only after both sides have sent and received Complete or Reset.
- After a local Complete or Reset, the pipe is Closing. Data received while Closing is discarded, up to the credit
  granted. The reservation for that outstanding credit is held until the peer's Complete or Reset arrives.
- Data after the peer's Complete, data beyond the granted credit, or data for an id that is in neither the active
  nor the closing set → protocol violation.
- A pipe that stays Closing past `ProgressTimeout` disconnects the session.

### 4.7 Invocation credits

- Each side advertises `InvocationCredits` in its greeting. The caller decrements a counter per invocation sent,
  including fire-and-forget calls and large-argument `Start`s. At zero, the caller waits locally (cancellable)
  for credit.
- The receiver queues invocations without waiting. It returns credit with a coalesced `InvocationCredit[+n]` when
  an invocation is dequeued for execution, not when it finishes.
- Execution concurrency (`MaxConcurrentConnectionInvocations`) is local and is not advertised.
- Receiving an invocation with no credit left is a protocol violation.
- Pipe and collection methods release their execution slot when the pipe registers (`RegisterDuplexPipe`) and
  count against `MaxPipes` from then on.

## 5. Session internals

### 5.1 Read loop

The read loop awaits only `PipeReader.ReadAsync` on the transport. Specifically:

- Ping replies set a pending-pong flag; they are not awaited.
- Pipe state, window updates, aborts and invocation credits are written to coalesced control state:
  - one pending pong;
  - one pending credit counter and one pending state/reset per pipe;
  - one pending abort per large-message key;
  - one pending invocation-credit counter.
  Outbound control memory is therefore proportional to the number of pipes and keys, not to messages received.
- The control sender drains that state under `_writeMutex` whenever it is signalled.
- Invocations are queued and dispatched to up to `MaxConcurrentConnectionInvocations` workers.
- `OnAuthenticate` runs in its own task. Until it completes, only the allow-listed greeting frames are accepted.

### 5.2 Send path

- `SendMessage` serializes, checks the length against the frame (or switches to a large message), and resets the
  buffer in a `finally` that covers serialization and the check.
- Large messages and pipe data take `_writeMutex` once per frame.
- Pipe writes send every chunk the granted credit allows, and flush the transport once per pipe flush rather than
  once per chunk.

### 5.3 Timeouts

- `ProgressTimeout` measures time since the last progress:
  - an inbound large message or channel item since its last chunk;
  - a Closing pipe since it entered Closing;
  - an outbound transport flush that has not completed.
  Waiting for credit is never a stall. A pipe writer waiting for credit is cancelled through its own token.
- The idle timeout is refreshed only by complete frames.

### 5.4 Decode guard

`MsgPackReader` keeps a cumulative element counter per value, alongside the existing `_depth`. It is checked in
`ReadArrayHeader` and `ReadMapHeader`. The default limit is max(64K, `MaxMessageSize` / 16). Exceeding it fails
the deserialization: the call fails, or the channel item resets the pipe.

## 6. Configuration and defaults

All values are configurable. The config validates when it is built and throws if the budget cannot hold the
fixed reservations:

```
MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64 KiB ≤ SessionBudget
```

| Setting | Untrusted | Trusted |
|---|---|---|
| `SessionBudget` | 16 MiB | 64 MiB |
| `MaxPipes` | 32 | 32 |
| `PipeWindow` | 256 KiB | 1 MiB |
| `MaxMessageSize` (arguments, results, channel items) | 4 MiB | 16 MiB |
| `InvocationCredits` | 32 | 128 |
| Check total | 8 + 4 + 2 = 14 MiB | 32 + 16 + 8 = 56 MiB |

The defaults are selected by `SerializerOptions.Security`. Only the numbers differ between Untrusted and Trusted;
the behaviour is the same.

Existing settings kept: `MaxConcurrentConnectionInvocations`, `Timeout`, `HandshakeTimeout`.

Transport receive `PipeOptions`: pause threshold at least 2 × (maximum frame + header), e.g. 256 KiB, resume at
half.

## 7. Pre-authentication

Before `NexusCompletedConnection`:

- Body length is checked when the header is read. A pre-auth frame larger than the greeting maximum (about
  16 KiB) disconnects before its body is buffered. The 8 KiB auth-token check moves ahead of deserialization.
- The allow-list stays an allow-list. A test sends every `MessageType` value before the greeting and expects a
  disconnect.
- No budget, credits or pipes exist. Only `ServerGreeting` or a disconnect is sent; asserted, not enforced by a
  runtime mode.
- `MaxPendingHandshakes` (server-wide, default 256) closes new transports when full. It also bounds concurrent
  `OnAuthenticate` calls and pre-auth transport memory.
- `HandshakeTimeout` stays one configurable value, documented as having to cover the application's
  `OnAuthenticate` latency.
- `ConnectionRateLimiter`, per-IP caps and bans remain opt-in. Only pre-auth protocol violations may feed the
  ban counter, and only when the application enables it. Authentication failures never cause a ban.
- Pre-auth failures are logged as rate-limited, aggregated warnings. No peer-supplied value is used as a log key
  or metric tag.

## 8. Work sequence

Each step is a PR on master that keeps the test suite green. `ProtocolVersion` is bumped once, before the release
tag. Every step after step 0 is gated by the benchmarks and fuzz targets from step 0.

| Step | Content | Size |
|---|---|---|
| 0. Gates | Benchmarks with master baselines: concurrent invocations (1/8/32 callers), pipe MB/s (1 MiB and 64 MiB, UDS and TCP), large-value round trip (128 KiB / 1 MiB / 4 MiB), invocation latency under pipe load, TCP with injected latency (20 ms). Fuzz targets in `src/NexNet.Fuzz` for frame sequences (Start/Chunk/Abort, window updates, pipe close, item prefixes), asserting: budget never exceeded, no hang beyond the timeouts, only `ProtocolError`-class disconnects. | M |
| 1. Correctness | `SendMessage` buffer reset and local `NexusMessageTooLargeException`; oversized result fails the call; chunk size constant; transport pause threshold; dispose unsolicited results; dispose cancellation registrations; `InvocationCancellation` id to `ushort`; unknown pipe id discarded instead of throwing. One regression test each. | S–M |
| 2. Pre-auth | Header-time frame cap, allow-list test, `MessageType` range assertion, `OnAuthenticate` off the read loop, `MaxPendingHandshakes`, rate-limited pre-auth logging. | S–M |
| 3. Read loop | Coalesced control state and control sender; invocation queue; slot released on `RegisterDuplexPipe`; idle timeout on complete frames. Tests: a stalled pipe reader does not block pings, results or other pipes; 2 pipes + 1 call with default slots. | M |
| 4. Flow control | Session budget; per-pipe windows with grant in `Ready`; `PipeWindowUpdate`; synchronous `BufferData`; pause flags and water marks removed; two-sided close; `ProgressTimeout`; config validation. Rewrite affected tests in the same PR. | L |
| 5. Invocation credits | Greeting fields, `InvocationCredit`, caller-side wait, violation disconnect. | M |
| 6. Channel framing | Varint prefix, item-sized grants, pipe reset on TooLarge, simplified `NexusChannelReader`. Gate: channel throughput benchmarks (Ints, Persons, IntArrays256) against baseline. | M |
| 7. Large values | `MessageStart`/`Chunk`/`Abort`, tracked ids for all streaming invocations, segment pool, validity rules, element counter in `MsgPackReader`. | L |
| 8. Release | `ProtocolVersion` = 2, update `protocol-specification.md`, full fuzz run and benchmark comparison. | S |

## 9. Tests required

- The application's reproductions: a burst of 40 × 40 KiB items with a slow reader followed by a call; one item
  of 300 KiB; a duplex pipe completed while the peer's buffer is full.
- A result over 64 KiB within `MaxMessageSize` succeeds; one over `MaxMessageSize` fails the call and the next
  call succeeds.
- Three pipe methods plus one call with default slots.
- Hostile peers: slow trickle, never reads, floods pipe opens, declares a large length then stops, sends beyond
  its window, invokes beyond its credits, never completes a close, sends a prefix that disagrees with its item,
  floods pings without reading, connects and violates in a loop.
- Pre-auth: oversized greeting header, every message type before the greeting, handshake slowloris against
  `MaxPendingHandshakes`.

## 10. Deferred

| Item | Tracking |
|---|---|
| Caller-path allocations (pooled `IValueTaskSource<T>`), server dispatch via `IThreadPoolWorkItem` | #79 |
| Metrics meter and session snapshot API | #80 |
| Pipe methods with results and `CancellationToken`. Today the proxy returns once the call is sent and callers then use the pipe; awaiting a remote result would deadlock that pattern, so this needs its own API design (e.g. the result on `pipe.CompleteTask`). | — |
| Remote exception detail (Off / Message option plus an application mapping hook) | — |
| Broadcast collection queues bounded in bytes | — |
| Window auto-tuning (growth only, driven by refill timing) if the latency-injected benchmark shows a need | — |
| Writer-elected combining queue in place of `_writeMutex` if the concurrent-invocation benchmark shows contention | — |
| Server-wide memory budget, counted in reservations | — |

## 11. Alternatives considered

- **Larger frames (u24/u32 length).** Rejected. TLS records (16 KiB), TCP segments and QUIC packets are already
  smaller than 64 KiB, so the per-frame overhead of u16 is about 0.01%. A large frame length would let a declared
  length grow the transport buffer before any budget check, and would let one frame delay pings.
- **Session-level credit only.** Rejected. A stalled pipe holding session credit stops every other pipe (fails R1).
- **Relying on transport flow control.** Rejected. Without per-pipe credit the read loop must stop reading,
  buffer without bound, or reset slow pipes.
- **Zero-copy reassembly from ref-counted transport blocks.** Deferred. Pinning amplification (one byte holding a
  64 KiB block) and lifetime bugs, for a small gain over one `memcpy`.
- **Dedicated send loop.** Not taken. It adds a thread-pool wakeup on each side of every round trip; the control
  sender meets R1 with less change.
- **Budget charge with Busy instead of invocation credits.** Not taken. Fire-and-forget calls have no result to
  carry Busy, and the Busy replies to a peer that does not read form an outbound backlog.
- **Adaptive windows.** Deferred. Ping RTT samples are too sparse, granted credit cannot be revoked, and nothing
  in R1–R8 requires it.

## 12. Implementation notes

Decisions made while implementing, where the design above did not settle the detail:

| Area | Decision |
|---|---|
| Opening side's window | `Ready` carries the registering side's window. The opening side reserves its own window when it creates the pipe and grants it with `PipeWindowUpdate` on receiving `Ready`; the registering side's writer starts with no credit. |
| Refusing an open | A new state bit, `Refused` (bit 3, the former pause flag), is sent instead of `Ready` with the opener's partial pipe ID. The opener's `ReadyTask` fails with `NexusBusyException`. |
| Two-sided close | A side that receives the peer's `Complete` without having sent its own answers with `Complete` (through the control sender). A pipe is freed once both were exchanged and the application released it. |
| Waiting for a channel item | While an item is incomplete and the writer's remaining credit is less than the bytes still missing, the reader grants back all consumed credit at once; otherwise an item smaller than the window could stall under the half-window rule. With enough credit outstanding the normal half-window grants apply, so small items do not cause extra `PipeWindowUpdate` frames. |
| Channel item encoding | The writer serializes a batch (`WriteAsync(IEnumerable)`) with one writer into a scratch buffer, noting where each item ends, and copies the items out with their prefixes every 64 KiB. The reader decodes a whole buffer with one `MsgPackReader`, limited to each item in turn (the `MaxElements` count restarts per item). Framing costs about 10 ns per item (one copy when writing, one prefix read when reading), which is noticeable only for very small items. |
| `MessageChunk` | The key (invocation ID, kind) is a 3-byte post-header, outside the body length, so payloads are sent from the staged buffer without copying. |
| Abort routing | Both sides can send and receive streams with the same key. Busy and TooLarge come from a stream's receiver, Cancelled and Ack from its sender; the reason selects the stream. |
| Large-message reservations | The reservation moves to the reassembled message and is released when the message is disposed (after the invocation runs, or the result is read), so queued large invocations stay within the budget. |
| Invocation credit return | Coalesced and sent once half the advertised credits are pending, keeping the per-call cost to counters. |
| Router invocations | Two passes. `INexusSession.TryWriteInvocation` writes the invocation, without waiting, to every session that has credit and no send in progress (a flush to a backed-up transport holds the write mutex, so that session is busy); like `ChannelWriter.TryWrite`, success means committed to the output, not delivered. The busy sessions are then sent to concurrently, at most 256 at a time, all within one `ProgressTimeout`; a session still without credit is skipped with a rate-limited warning. A single-frame invocation is written unchanged to every session (fire-and-forget, invocation ID 0); a streamed one is sent as a per-session copy with a tracked ID. A broadcast is delayed by at most one `ProgressTimeout` in total, and healthy sessions not at all. |
| Decode guard | `NexusSerializerOptions.MaxElements` (0 = no limit) holds the element limit; a configuration resolves 0 to `max(65536, MaxMessageSize / 16)`. |
| Channel item stall | `ProgressTimeout` applies to channel items that received extra credit: the writer holds the credit to send them, so no data means it stopped. Items within the window hold only memory already reserved. |
