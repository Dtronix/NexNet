# NexNet Wire Protocol (version 1)

This document specifies the bytes NexNet sends over a transport (TCP, TLS, UDS, QUIC, WebSocket, HttpSocket).
It covers the connection preamble, message framing, every protocol message body, flow control, the payload encoding
rules for user types, and limits. The design behind the flow control, large values and hostile-peer limits is described in
[flow-control.md](flow-control.md).

Two encodings are used:

- **Framing** is NexNet's own envelope. Its multi-byte fields (the body length and the post-headers) are
  **little-endian**.
- **Message bodies** (except `DuplexPipeWrite` and `MessageChunk`) are
  [MessagePack](https://github.com/msgpack/msgpack/blob/master/spec.md), whose multi-byte numbers are **big-endian**
  as the spec requires.

## 1. Connection preamble

Each side sends an 8-byte header once, immediately after the transport connects:

| Offset | Size | Value |
|---|---|---|
| 0 | 4 | Magic `4E 6E 50 14` (`N`, `n`, `P`, DC4) |
| 4 | 3 | Reserved, must be `00 00 00` |
| 7 | 1 | Protocol version, `1` |

The peer closes the connection with `DisconnectProtocolError` (disconnect reason `ProtocolError`) if:
- the magic bytes differ,
- any reserved byte is non-zero, or
- the protocol version differs from its own.

Message bodies are only defined by this specification. A peer that sends the same preamble but a different body
encoding is disconnected with `ProtocolError` as soon as its first message fails to decode.

## 2. Framing

After the preamble the stream is a sequence of messages:

| Field | Size | Notes |
|---|---|---|
| Type | 1 | `MessageType` value (§3) |
| Body length | 2 | **little-endian** `uint16`; present only for types with a body |
| Post-header | 0, 2 or 3 | `DuplexPipeWrite` and `PipeWindowUpdate`: pipe ID as `[client id][server id]`. `MessageChunk`: invocation ID (`uint16`, little-endian), then the kind (`uint8`, §4.10) |
| Body | *length* | MessagePack (§4), or raw bytes for `DuplexPipeWrite` and `MessageChunk` |

The maximum body is 65,535 bytes. The body length does not count the post-header. Arguments and results larger than
one frame are streamed with `MessageStart` / `MessageChunk` (§6); other data larger than a frame goes through pipes
(§7).

Before the connection completes (§5), a body longer than 16 KiB is a protocol error, detected as soon as the length
is read.

### Pipe IDs

A pipe ID is a pair of one-byte local IDs: the client's and the server's.
- In frames it is written as two bytes, client byte first.
- Inside MessagePack bodies it is a `uint16` whose value is defined as `clientId | (serverId << 8)`.

Both forms are independent of host byte order.

## 3. Message types

| Value | Name | Body | Sent by |
|---|---|---|---|
| 1 | Ping | none | both |
| 20–37 | Disconnect* | none | both |
| 50 | DuplexPipeWrite | raw pipe bytes | both |
| 51 | PipeWindowUpdate | `uint32` LE credit (§7.2) | both |
| 100 | ClientGreeting | §4.1 | client |
| 101 | ClientGreetingReconnection | §4.1 | client |
| 105 | ServerGreeting | §4.2 | server |
| 110 | Invocation | §4.3 | both |
| 111 | InvocationCancellation | §4.5 | both |
| 112 | InvocationResult | §4.4 | both |
| 113 | MessageStart | §4.8 | both |
| 114 | MessageChunk | raw payload | both |
| 115 | MessageAbort | §4.9 | both |
| 116 | InvocationCredit | §4.7 | both |
| 120 | DuplexPipeUpdateState | §4.6 | both |

Every byte in 20–38 is read as a disconnect reason before anything else; no other message type uses that range.
Defined disconnect reasons: 20 SocketError, 21 Graceful, 22 ProtocolError, 23 Timeout, 24 ClientMismatch,
25 ServerMismatch, 28 ServerShutdown, 29 Authentication, 30 ServerRestarting, 32 SocketClosedWhenWriting,
33 RateLimited, 34 Unauthorized, 35 FlowControl (credit or window violation), 37 Stalled (36 is not assigned)
(no progress within the progress timeout, §9).

A server rejects `ServerGreeting`, and a client rejects `ClientGreeting*`, with `DisconnectProtocolError`.
Any type not listed here is a protocol error.

## 4. Message bodies

Every MessagePack body is a **fixed-length MessagePack array**. Readers require the exact element count and reject
trailing bytes.

**"Embedded value"** means a payload value (§10) encoded inline as exactly one MessagePack value. It is never
wrapped in `bin` or length-prefixed.

### 4.1 ClientGreeting / ClientGreetingReconnection
`[version: str|nil, serverNexusHash: int, clientNexusHash: int, authenticationToken: bin, maxMessageSize: int, invocationCredits: int]`

- `authenticationToken` is at most 8 KiB; a longer one is rejected from its `bin` header, before it is read.
- `maxMessageSize`: the largest argument, result or channel item the client accepts (§9).
- `invocationCredits`: how many invocations the server may have queued at the client (§4.7).

### 4.2 ServerGreeting
`[version: int, clientId: int64, maxMessageSize: int, invocationCredits: int]`

`maxMessageSize` and `invocationCredits` have the same meaning for the server. A side whose advertised
`maxMessageSize` is below one frame (65,526 bytes) or whose `invocationCredits` is below 1 is disconnected with
`ProtocolError`.

### 4.3 Invocation
`[invocationId: uint16, methodId: uint16, flags: uint8, arguments: array]`

`arguments` is an embedded array with one element per serialized parameter, in declaration order:
- `CancellationToken` parameters are not sent.
- Pipe and channel parameters are sent as their initial pipe ID byte.
- Methods without serialized parameters send an empty array (`0x90`).

`flags`: bit 0 IgnoreReturn (no result is sent), bit 1 DuplexPipe. An invocation whose body exceeds one frame is
streamed (§6). The `invocationId` of an IgnoreReturn invocation has no meaning and need not be unique; senders use
0. The exception is a streamed one: its ID keys the stream (§6), so it is unique among the sender's outstanding
invocations until the stream has been sent. A receiver must not use the ID of an IgnoreReturn invocation to identify
it; nothing can cancel such an invocation except the session ending.

### 4.4 InvocationResult
- No result (void methods, exceptions, unauthorized): `[invocationId: uint16, state: uint8]`
- With a result, which may itself be nil: `[invocationId: uint16, state: uint8, result: embedded value]`

`state`: 0 Unset, 1 CompletedResult, 2 Exception, 3 Unauthorized. A result for an invocation ID the receiver is not
waiting on is a protocol error. A result whose body exceeds one frame is streamed (§6).

### 4.5 InvocationCancellation
`[invocationId: uint16]`

### 4.6 DuplexPipeUpdateState
`[pipeId: uint16, state: uint8, window: uint32]`

`state` is a set of flags:

| Bit | Name | Meaning |
|---|---|---|
| 0 | ClientWriterServerReaderComplete | the client-to-server direction is complete |
| 1 | ClientReaderServerWriterComplete | the server-to-client direction is complete |
| 2 | Ready | the pipe is open (sent by the side that registers it) |
| 3 | Refused | the open was refused because a limit is reached; sent instead of Ready, with the opener's partial pipe ID |

`Complete` is bits 0–2 together. `window` is the receive credit granted with Ready (§7.1) and 0 otherwise.

### 4.7 InvocationCredit
`[credits: uint16]`

Returns `credits` invocation credits to the peer (§8).

### 4.8 MessageStart
`[invocationId: uint16, kind: uint8, innerType: uint8, totalLength: uint32]`

Starts a large message (§6). `kind` is 0 Argument (`innerType` 110, Invocation) or 1 Result (`innerType` 112,
InvocationResult). `totalLength` is the length of the inner message's body.

### 4.9 MessageAbort
`[invocationId: uint16, kind: uint8, reason: uint8]`

`reason`: 0 Busy, 1 TooLarge, 2 Cancelled, 3 Ack (§6.3).

### 4.10 MessageChunk
Post-header `[invocationId: uint16 LE][kind: uint8]`, body: the next bytes of the inner message's body.

### 4.11 Collection synchronization (channel items on the collection pipe)

Each item is a union value: `[tag: uint16, body: array]`, framed as a channel item (§7.4). The first element of
every body is `flags: uint8`.

| Tag | Message | Body |
|---|---|---|
| 0 | ResetStart | `[flags, version: int, totalValues: int]` |
| 1 | ResetValues | `[flags, values: embedded T[]]` |
| 2 | ResetComplete | `[flags]` |
| 3 | Clear | `[flags, version: int]` |
| 4 | Insert | `[flags, version: int, index: int, value: embedded T]` |
| 5 | Replace | `[flags, version: int, index: int, value: embedded T]` |
| 6 | Move | `[flags, version: int, fromIndex: int, toIndex: int]` |
| 7 | Remove | `[flags, version: int, index: int]` |
| 8 | Noop | `[flags]` |

## 5. Connection completion

The client sends its greeting after the preamble and nothing else until it receives `ServerGreeting`. The server
validates the greeting, runs authentication if configured, and then sends `ServerGreeting`. Until then:
- only a greeting is accepted; any other message is a protocol error;
- the body cap of §2 applies.

Each side applies the peer's advertised `maxMessageSize` and `invocationCredits` once the connection completes (the
server only after authentication). A side's send limit is the smaller of its own `maxMessageSize` and the peer's;
it always enforces its own limit on what it receives.

## 6. Large arguments and results

### 6.1 Sending

An `Invocation` or `InvocationResult` whose body exceeds 65,535 bytes is sent as:

```
MessageStart[invocationId, kind, innerType, totalLength]
MessageChunk[invocationId, kind] + payload   (repeated; payload lengths add up to totalLength)
```

The sender serializes the message fully first. If `totalLength` exceeds its send limit the value is not sent and
the call fails locally. Other messages are never larger than a frame. A stream is keyed by (invocation ID, kind):
an Argument flows from caller to callee, a Result from callee to caller. A large argument's `MessageStart` uses the
invocation's credit (§8).

### 6.2 Receiving

On `MessageStart` the receiver reserves `totalLength` from its session budget (§9). If it can't, it replies
`MessageAbort(Busy)` and discards the stream's chunks up to `totalLength`. Otherwise it buffers the chunks (memory is
allocated as they arrive) and, once `totalLength` bytes are in, handles the inner message as if it had arrived in
one frame. The reservation is held until the inner message has been processed.

### 6.3 Aborts

- A receiver that refuses a stream sends `MessageAbort(Busy)` (or `TooLarge`). The sender stops and replies
  `MessageAbort(Ack)`; no chunk follows the Ack. A refused argument fails its call.
- A sender that stops on its own (the caller canceled) sends `MessageAbort(Cancelled)`; no chunk follows.
- A stream closes when the receiver has seen `totalLength` bytes, an Ack, or the sender's abort. An abort for an
  unknown stream is ignored, as it can race with completion.

Busy and TooLarge come from a stream's receiver; Cancelled and Ack from its sender. Both sides can send and receive
streams with the same key, and the reason tells which stream an abort refers to.

### 6.4 Protocol errors

The receiver disconnects with `ProtocolError` on:
- `totalLength` above its advertised `maxMessageSize`, a `totalLength` of 0, or a `kind`/`innerType` mismatch;
- `MessageStart` for a stream that is already active;
- a Result stream (or an `InvocationResult`) for an invocation ID it is not waiting on;
- a chunk that would exceed the remaining `totalLength`;
- a chunk for a stream that was never started and is not being discarded.

## 7. Pipes

### 7.1 Opening

The opening side picks a local ID, reserves its receive window from its session budget, and sends the ID as the
pipe argument of an invocation. The side that registers the pipe:
- reserves its own receive window and replies `DuplexPipeUpdateState[Ready, window]`, or
- if it has reached its pipe limit or its budget is exhausted, replies `DuplexPipeUpdateState[Refused]` with the
  opener's partial pipe ID. The opener's pipe fails.

On `Ready` the opener grants its own window with `PipeWindowUpdate[window]`. Neither side writes before it holds
credit.

### 7.2 Credit

Each direction of a pipe has a receive window, fixed per side (default 256 KiB, §9).
- A writer sends only as many bytes as it holds credit for, and waits for more otherwise; the wait is local to the
  writer.
- The reader grants credit back with `PipeWindowUpdate[n]` once at least half its window has been consumed since the
  last grant, or at once when it is waiting for the rest of a channel item (§7.4).
- Data beyond the credit granted, or a total grant above 2³¹ bytes, disconnects with `FlowControl`.
- `PipeWindowUpdate` for an unknown pipe is ignored.

### 7.3 Closing

- A side closes a pipe by sending `DuplexPipeUpdateState[Complete]`. A side that receives the peer's Complete and
  has not sent its own replies with Complete.
- A pipe ID and its window reservation are released once a side has both sent and received Complete.
- While a side is closing (sent, not received), data still arriving is discarded, up to the credit granted.
- Data after the peer's Complete, beyond the credit, or for an ID that is neither open nor closing is a protocol
  violation.
- A pipe that stays closing past the progress timeout disconnects with `Stalled`.

Half-closing one direction (bit 0 or bit 1 alone) completes that direction; data the writer still sends on it is
discarded up to its credit.

### 7.4 Channel items

A typed channel (`INexusDuplexChannel<T>`) sends each item as its length followed by the item:

- The length is an unsigned LEB128 varint (7 bits per byte, low bits first, high bit set on every byte but the
  last; at most 5 bytes). The item is exactly one embedded value (§10) of that many bytes.
- Items are concatenated in the pipe's byte stream, independent of frame boundaries.
- A length above the reader's `maxMessageSize`, a malformed prefix, or an item that does not decode to exactly its
  length resets the pipe (the reader sends Complete); the session stays up. The writer checks the length against its
  send limit first and fails the write locally.
- An item longer than the reader's window: the reader reserves the difference from its session budget (waiting in
  FIFO order if it is exhausted) and grants it as extra credit; that credit is not granted back once the item is
  consumed.

Raw `INexusDuplexPipe` data has no framing; an application message on a raw pipe must fit in the window.

## 8. Invocation credits

- Each side advertises `invocationCredits` in its greeting: how many invocations the peer may have queued at it.
- A side takes one credit for every invocation it sends (fire-and-forget included; a streamed argument at its
  `MessageStart`) and waits locally when it has none.
- The receiver returns credit when it dequeues an invocation for execution (or drops a refused streamed argument),
  in `InvocationCredit` messages sent once half its advertised credits are pending.
- An invocation received without credit, or credit returned beyond what was advertised, disconnects with
  `FlowControl`.
- How many invocations run at once (`MaxConcurrentConnectionInvocations`) is local and not advertised; pipe and
  collection methods stop counting against it once their pipe is open.

## 9. Limits and timeouts

| Setting | Untrusted default | Trusted default | Meaning |
|---|---|---|---|
| `SessionBudget` | 16 MiB | 64 MiB | Receive memory per session, counted in reservations |
| `MaxPipes` | 32 | 32 | Pipes open per session |
| `PipeWindow` | 256 KiB | 1 MiB | Receive window per pipe |
| `MaxMessageSize` | 4 MiB | 16 MiB | Largest argument, result or channel item |
| `InvocationCredits` | 32 | 128 | Invocations the peer may queue |

"Trusted" is selected with the trusted serializer options (§11). Every window, the invocation queue
(`InvocationCredits` × 64 KiB) and every large message are reserved from the session budget; a configuration whose
budget can't hold `MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64 KiB` is rejected.

The **progress timeout** (30 s by default) disconnects with `Stalled` when there is no progress on:
- an inbound large message, or a channel item given extra credit, since its last data;
- a closing pipe, since it started closing;
- an outbound transport flush.

Waiting for credit is never a stall. The idle timeout is refreshed only by complete messages.

## 10. Payload encoding

### 10.1 Canonical encoding
Writers always use the smallest form. Non-negative integers use the unsigned family (`fixint`, `uint8`/`16`/`32`/`64`);
negative integers use negative `fixint` or `int8`/`16`/`32`/`64`. Floats are always `float32` or `float64`.
Readers accept every valid form and check ranges.

### 10.2 `[NexusObject]` types
- An object is an array of length `maxKey + 1`. Element *i* is the member with `[NexusKey(i)]`; unused keys are nil.
- A null reference is nil.
- Readers skip elements past their highest known key and leave members missing from a shorter array at their defaults.

### 10.3 Unions
`[tag: uint16, payload]`, or nil for null. An unknown tag is rejected.

### 10.4 Built-in types

| .NET type | Encoding |
|---|---|
| `bool` | `c2`/`c3` |
| Integers, `char` (as `uint16`) | canonical integer |
| `float` / `double` / `Half` (as `float32`) | `ca` / `cb` |
| `string` | UTF-8 `str`, nil for null |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>` | `bin` |
| Arrays, `List<T>`, `Memory<T>` and `ReadOnlyMemory<T>` of `sbyte, short, ushort, int, uint, long, ulong, float, double, char, Half` | ext 78 (§10.5) |
| Other `T[]`, `List<T>`, list interfaces, `HashSet<T>`, `Queue<T>`, `Stack<T>` (top first) | array |
| `Dictionary<K,V>` and dictionary interfaces | map |
| `Nullable<T>` | nil or `T` |
| enums | underlying integer |
| `DateTime` | `int64` `ToBinary()` (keeps `Kind`) |
| `DateTimeOffset` | `[UtcTicks: int64, offsetMinutes: int16]` |
| `TimeSpan` | `int64` ticks |
| `DateOnly` / `TimeOnly` | `int32` DayNumber / `int64` ticks |
| `Guid` | `bin` 16 bytes, RFC 4122 (big-endian) order |
| `decimal` | `bin` 16 bytes: `GetBits` as four little-endian `int32` (lo, mid, hi, flags) |
| `BigInteger` | `bin`, little-endian two's complement |
| `Uri`, `Version` | `str` |
| `ValueTuple<…>`, `Tuple<…>`, `KeyValuePair<K,V>` | array |

### 10.5 Ext 78: primitive array
Ext type code `78`.
- The data is one element-kind byte followed by the elements' raw **little-endian** bytes.
- Kinds: 1 sbyte, 2 int16, 3 uint16, 4 int32, 5 uint32, 6 int64, 7 uint64, 8 float32, 9 float64, 10 char, 11 Half.
- The data length minus one must be a multiple of the element size.
- An empty array is `d4 4e <kind>`.

## 11. Decoding limits and hardening

Always enforced:
- Array and map counts and `str`/`bin`/`ext` lengths are checked against the remaining bytes before anything is allocated.
- `0xc1` is rejected.
- Skipping is iterative, so nesting depth uses no stack.
- Message bodies must match their exact shape, with no trailing bytes.
- Array elements and map entries (keys and values) are counted cumulatively per value; above the limit
  (`max(65536, MaxMessageSize / 16)` by default) the value fails to decode.

With the default untrusted options:
- Container depth is limited to 64.
- Invalid UTF-8 is rejected.
- Dictionaries and sets with non-string keys use randomized hashing.

Configure these with `ConfigBase.SerializerOptions`.
