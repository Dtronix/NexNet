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
| 20–38 | Disconnect* | none | both |
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
33 RateLimited, 34 Unauthorized, 35 FlowControl (credit or window violation), 37 Stalled (no progress within the
progress timeout, §9). 26, 27, 31, 36 and 38 are not assigned, but are still disconnects (§5.6).

A server rejects `ServerGreeting`, and a client rejects `ClientGreeting*`, with `DisconnectProtocolError`.
Any type not listed here is a protocol error.

## 4. Message bodies

Every MessagePack body is a **fixed-length MessagePack array**. Readers require the exact element count and reject
trailing bytes.

**"Embedded value"** means a payload value (§10) encoded inline as exactly one MessagePack value. It is never
wrapped in `bin` or length-prefixed.

### 4.1 ClientGreeting / ClientGreetingReconnection
`[version: str|nil, serverNexusHash: int, clientNexusHash: int, authenticationToken: bin, maxMessageSize: int, invocationCredits: int]`

- `authenticationToken` is `bin` or `nil`, at most 8 KiB; a longer one is rejected from its `bin` header, before it
  is read (§5.4).
- `maxMessageSize`: the largest argument, result or channel item the client accepts (§9).
- `invocationCredits`: how many invocations the server may have queued at the client (§4.7).

### 4.2 ServerGreeting
`[version: int, clientId: int64, maxMessageSize: int, invocationCredits: int]`

`version` is the hash of the **client** nexus the server expects (§5.2), not a version string. `clientId` is the
session ID the server assigned.

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

**Method ID validation.** A receiver accepts an `Invocation` only if `methodId` is a method or collection of its own
nexus in the negotiated version: on the server, the version selected by the greeting (§5.2); on the client, its
client nexus. Any other `methodId` disconnects with `ProtocolError`. The check runs before the invocation credit is
counted (§8); a streamed invocation is checked once it has been reassembled. The C# implementation looks up the
int64 key `(versionHash << 16) | methodId` in a generated set, where `versionHash` is the negotiated version's
nexus hash. Another implementation may keep any equivalent set of the method IDs it implements.

Source: `src/NexNet/Internals/NexusSession.Receiving.cs` (the `Invocation` case), `VMHash` and
`VersionMethodHashSet` in `src/NexNet.Generator/Emission/NexusEmitter.cs`.

### 4.4 InvocationResult
- No result (void methods, exceptions, unauthorized): `[invocationId: uint16, state: uint8]`
- With a result, which may itself be nil: `[invocationId: uint16, state: uint8, result: embedded value]`

`state`: 0 Unset, 1 CompletedResult, 2 Exception, 3 Unauthorized, 4 Canceled. An invocation without IgnoreReturn gets
exactly one result, unless the session ends first; that includes a method that was canceled (by an
InvocationCancellation or on its own), which gets Canceled. A result for an invocation ID the receiver is not waiting on is a protocol
error. A result whose body exceeds one frame is streamed (§6).

An Exception result may carry a detail as its third element: an array of exactly 3,
`[code: int32|nil, type: str|nil, message: str|nil]`, the application error code, the exception's type name and its
message. Without a detail it is `[invocationId, state]`. Senders truncate `type` to 256 and `message` to 2048 UTF-16
chars, never splitting a surrogate pair. A receiver ignores a detail that has another shape, a code outside int32 or
longer strings, and treats the result as an Exception without detail; this is never a protocol error. Peers that do
not read the detail accept it as an embedded value.

### 4.5 InvocationCancellation
`[invocationId: uint16]`

Asks the receiver to cancel an invocation it has received, including one still queued for execution: that one starts
with its cancellation token already canceled. The receiver still sends its result (§4.4), Canceled if the method
stopped. A cancellation for an invocation that has already completed is ignored. The canceling side keeps the invocation ID outstanding until that result arrives, so the result is not a
protocol error and the ID is not reused before it.

#### Caller-side cancellation

A caller may cancel an invocation that expects a result (no IgnoreReturn). What happens depends on whether the
invocation has been sent:

- **Canceled before it is sent.** This covers a token that is already canceled and a cancel while the call waits for
  invocation credit (§8). Nothing is sent, no invocation ID stays reserved, and the call completes as canceled.
- **Canceled after it is sent** (or while it is being written). The caller completes the call as canceled at once,
  without waiting for the peer, and sends `InvocationCancellation`. It keeps the invocation ID outstanding until the
  peer's `InvocationResult` arrives. That late result, whatever its state, is consumed and discarded, which then
  releases the ID. It is not a protocol error.
- **Canceled while a large argument is streaming** (§6). The caller stops, sends `MessageAbort(Cancelled)`, and
  completes the call like a call canceled before it was sent. The method never runs, so no result follows.
- **Session ends.** Every call still waiting completes as canceled.

IgnoreReturn invocations can't be canceled.

**Current C# behavior:** for the first case, the C# client returns without a result: a `ValueTask` method returns
normally and a `ValueTask<T>` method returns `default`. The same applies to the large-argument case. Unit F03
changes the C# client to throw `TaskCanceledException`, which is the required behavior for other clients (Java:
`CancellationException`; JavaScript: `AbortError`). Neither case puts anything on the wire, so the difference is
not visible to the peer.

Source: `src/NexNet/Invocation/SessionInvocationStateManager.cs` (`InvokeMethodWithResultCore`,
`CancelInvocation`, `UpdateInvocationResult`), `src/NexNet/Internals/RegisteredInvocationState.cs`.

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

Returns `credits` invocation credits to the peer (§8). `credits` is at least 1; `InvocationCredit[0]` disconnects
with `FlowControl`.

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

### 5.1 Before the connection completes

The rules are the same on both sides. A side's connection completes:
- on the server, after the greeting is validated and authentication succeeds, just before `ServerGreeting` is sent;
- on the client, when it receives a valid `ServerGreeting`.

Until then the side accepts only:
- a disconnect byte (20–38, §5.6), which is read before anything else;
- one greeting of the right direction: `ClientGreeting`/`ClientGreetingReconnection` on the server,
  `ServerGreeting` on the client.

Anything else is a `ProtocolError`, and so is a greeting of the wrong direction. That includes `Ping`: a client must
not ping before `ServerGreeting` arrives (§5.5). A frame body longer than 16 KiB is a `ProtocolError` as soon as
its length is read (§2).

The client sends the preamble and its greeting, then nothing else until `ServerGreeting`. The server sends
nothing but its preamble, then `ServerGreeting` or a disconnect.

Each side also runs a **handshake timeout** (`HandshakeTimeout`, 15 s by default, real time), started when its
read loop starts. If the connection has not completed by then, the side closes the transport with reason
`Timeout` and sends **no** disconnect byte. On the server the timeout includes the time spent in
`OnAuthenticate`.

Source: `src/NexNet/Internals/NexusSession.Receiving.cs` (`Process`, the pre-completion check), and
`src/NexNet/Internals/NexusSession.cs` (`MaxPreAuthBodyLength`, `CheckHandshakeComplete`).

### 5.2 Greeting validation

The nexus hashes are **opaque int32 constants** produced by the NexNet source generator from the nexus
interfaces. A client in another language takes them from the generator output and never recomputes them. Three
hashes are involved:

| Hash | Meaning |
|---|---|
| client nexus hash | hash of the client nexus interface (the methods the server may invoke on the client) |
| server nexus hash | hash of the server nexus interface, or of one of its `[NexusVersion]` versions |
| versions table | a versioned server's map from version string to that version's hash |

The client fills its greeting as follows:
- `version`: the newest version string of the server nexus it was generated against, or nil if that nexus is
  not versioned. A non-nil version is at most 16 characters.
- `serverNexusHash`: the hash of that version, or the server nexus hash if unversioned.
- `clientNexusHash`: its client nexus hash.

The server checks the greeting in this order. The first failure ends the session; the server sends the disconnect
byte (§5.6).

| # | Check | Disconnect |
|---|---|---|
| 1 | More than 3 greetings on this connection (see the note below) | `ProtocolError` |
| 2 | A greeting was already received on this connection | `ProtocolError` |
| 3 | The body does not decode, or `authenticationToken` is longer than 8 KiB | `ProtocolError` |
| 4 | `version` is longer than 16 UTF-16 code units | `ProtocolError` |
| 5 | `clientNexusHash` differs from the server's client nexus hash | `ClientMismatch` |
| 6 | `maxMessageSize` below 65,526, or `invocationCredits` below 1 | `ProtocolError` |
| 7 | Unversioned server and `version` is not nil | `ServerMismatch` |
| 8 | Versioned server and `version` is nil or not in its versions table | `ServerMismatch` |
| 9 | `serverNexusHash` differs from the hash of the requested version (unversioned: the server nexus hash) | `ServerMismatch` |
| 10 | Authentication is configured and `OnAuthenticate` throws or returns no identity | `Authentication` |

If all checks pass, the server completes the connection and sends `ServerGreeting` with `version` set to its client
nexus hash. The version negotiated by checks 7–9 selects the method set for §4.3.

**Greeting attempts.** The server counts greetings and refuses a fourth (check 1). Check 2 refuses any second
greeting anyway, so in practice exactly one greeting is accepted per connection, and a client that wants to retry
opens a new connection.

The client checks `ServerGreeting` in this order:

| # | Check | Disconnect |
|---|---|---|
| 1 | A `ServerGreeting` was already received | `ProtocolError` |
| 2 | `version` differs from its own client nexus hash | `ClientMismatch` |
| 3 | `maxMessageSize` below 65,526, or `invocationCredits` below 1 | `ProtocolError` |

Source: `src/NexNet/Internals/NexusSession.Receiving.cs` (`MessageHandler`, the `ClientGreeting` and
`ServerGreeting` cases; `CompleteServerHandshake`), `src/NexNet/Internals/NexusSession.cs` (`StartAsClient`,
`MaxVersionStringLength`, `MaxGreetingAttempts`, `ApplyPeerLimits`).

### 5.3 ClientGreeting and ClientGreetingReconnection

`ClientGreetingReconnection` (101) has the same body as `ClientGreeting` (100). The C# client sends 101 when it
reconnects under its reconnection policy, and 100 otherwise. The server treats 101 exactly like 100: the same checks
apply, and it creates a new session. Nothing from the earlier session is resumed: pipes, collections, invocations
and the session ID are all new. Session resumption is not implemented. A new client may always send 100.

Source: `src/NexNet/Internals/NexusSession.Receiving.cs` (the `ClientGreetingReconnection` case falls through to
`ClientGreeting`), `src/NexNet/Internals/NexusSession.cs` (`StartAsClient`).

### 5.4 Authentication token

`authenticationToken` is `bin` or `nil`. Senders send an empty `bin` (`c4 00`) when they have no token; readers
treat `nil` like an empty token. The length is checked against 8,192 bytes from the `bin` header, before the
bytes are read; a longer token is a `ProtocolError`.

The server passes the token bytes to its authentication handler only when authentication is configured;
otherwise it ignores them. The token never appears anywhere else in the protocol.

Source: `src/NexNet/Messages/IClientGreetingMessageBase.cs` (`ReadAuthenticationToken`),
`src/NexNet/Messages/ClientGreeting.cs`.

### 5.5 Ping and idle timeout

`Ping` (type 1) is a single byte with no length and no body.

- **Only the client pings.** Every `PingInterval` (10 s by default), starting once the connection completes, the
  client first checks its idle timeout and then sends `Ping`.
- **The server only replies.** For every `Ping` it receives, it sends a `Ping` back. Pings that arrive before the
  reply is sent may be answered with a single `Ping`. The server never pings on its own.
- **The idle timeout is local to each side** and is not advertised. Each side disconnects with `Timeout` (sending
  the disconnect byte) when it has received no complete frame for `Timeout` (30 s by default). Any complete frame
  counts, not only `Ping`; a partial frame does not. The server checks every `Timeout` / 4. The client checks on
  each ping tick.

So a client must ping well within the server's idle timeout, which it cannot learn from the protocol today.

**Forward note:** unit S04 adds a trailing field `idleTimeoutMs` to `ServerGreeting`. Clients will derive their
ping interval from it (a third of it by default). Older readers ignore trailing fields.

Source: `src/NexNet/NexusClient.cs` (`PingTimer`), `src/NexNet/NexusServer.cs` (`ConnectionWatchdog`),
`src/NexNet/Internals/NexusSession.Receiving.cs` (the `Ping` case), `src/NexNet/Internals/SessionControlSender.cs`
(`RequestPong`), `src/NexNet/Internals/NexusSession.cs` (`DisconnectIfTimeout`).

### 5.6 Disconnect

A disconnect is a single byte, the reason (20–38), with no length and no body.

**Receiving.** A byte in 20–38 read where a message type is expected ends the session at once with that reason.
This holds in every state, including before the connection completes. The receiver:
- does not process any bytes after it;
- does not reply;
- closes the transport.

Unassigned codes in the range (26, 27, 31, 36, 38) are still disconnects, reported with their numeric value.

**Sending.** A side that ends the session for a reason the peer should know writes the disconnect byte, waits
`DisconnectDelay` (200 ms by default; 0 disables the wait; at most 10 s) so the byte can reach the wire, and then
closes the transport. Nothing is sent after the disconnect byte.

| Cause | Reason | Byte sent |
|---|---|---|
| Application disconnect (`DisconnectAsync`) | `Graceful` | yes |
| Server stopping | `ServerShutdown` | yes |
| Protocol violation detected while reading, including a bad preamble | `ProtocolError` or the specific reason | yes |
| Greeting rejected (§5.2) | as listed there | yes |
| Idle timeout (§5.5) | `Timeout` | yes |
| Progress timeout (§9) | `Stalled` | yes |
| Authorization returns Disconnect (§7.6) | `Unauthorized` | yes |
| Handshake timeout (§5.1) | `Timeout` | no |
| Transport ended or failed | `SocketError` | no |
| Disconnect byte received | the received reason | no |

Source: `src/NexNet/Internals/NexusSession.Receiving.cs` (`Process`, the 20–38 check),
`src/NexNet/Internals/NexusSession.cs` (`DisconnectCore`, `DisconnectStartedCore`),
`src/NexNet/Transports/ConfigBase.cs` (`DisconnectDelay`).

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
- An invocation received without credit, an `InvocationCredit` whose `credits` is 0, or credit returned beyond
  what was advertised, disconnects with `FlowControl`.
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
