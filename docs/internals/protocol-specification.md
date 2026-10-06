# NexNet Wire Protocol (version 1)

This document specifies the bytes NexNet sends over a transport (TCP, TLS, UDS, QUIC, WebSocket, HttpSocket).
It covers the connection preamble, message framing, every protocol message body, the payload encoding rules for
user types, and limits.

Two encodings are used:

- **Framing** is NexNet's own envelope. Its one multi-byte field, the body length, is **little-endian**.
- **Message bodies** (except `DuplexPipeWrite`) are [MessagePack](https://github.com/msgpack/msgpack/blob/master/spec.md),
  whose multi-byte numbers are **big-endian** as the spec requires.

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
| Post-header | 0 or 2 | Only `DuplexPipeWrite`: pipe ID as `[client id][server id]` |
| Body | *length* | MessagePack (§4), or raw bytes for `DuplexPipeWrite` |

The maximum body is 65,535 bytes. Data larger than that must be streamed through duplex pipes.

### Pipe IDs

A pipe ID is a pair of one-byte local IDs: the client's and the server's.
- In frames it is written as two bytes, client byte first.
- Inside MessagePack bodies it is a `uint16` whose value is defined as `clientId | (serverId << 8)`.

Both forms are independent of host byte order.

## 3. Message types

| Value | Name | Body | Sent by |
|---|---|---|---|
| 1 | Ping | none | both |
| 20–34 | Disconnect* | none | both |
| 50 | DuplexPipeWrite | raw pipe bytes | both |
| 100 | ClientGreeting | §4.1 | client |
| 101 | ClientGreetingReconnection | §4.1 | client |
| 105 | ServerGreeting | §4.2 | server |
| 110 | Invocation | §4.3 | both |
| 111 | InvocationCancellation | §4.5 | both |
| 112 | InvocationResult | §4.4 | both |
| 120 | DuplexPipeUpdateState | §4.6 | both |

A server rejects `ServerGreeting`, and a client rejects `ClientGreeting*`, with `DisconnectProtocolError`.

## 4. Message bodies

Every body is a **fixed-length MessagePack array**. Readers require the exact element count and reject trailing bytes.

**"Embedded value"** means a payload value (§5) encoded inline as exactly one MessagePack value. It is never
wrapped in `bin` or length-prefixed.

### 4.1 ClientGreeting / ClientGreetingReconnection
`[version: str|nil, serverNexusHash: int, clientNexusHash: int, authenticationToken: bin]`

### 4.2 ServerGreeting
`[version: int, clientId: int64]`

### 4.3 Invocation
`[invocationId: uint16, methodId: uint16, flags: uint8, arguments: array]`

`arguments` is an embedded array with one element per serialized parameter, in declaration order:
- `CancellationToken` parameters are not sent.
- Pipe and channel parameters are sent as their initial pipe ID byte.
- Methods without serialized parameters send an empty array (`0x90`).

The worst-case overhead of the other fields is 9 bytes, so arguments are limited to 65,526 bytes.

### 4.4 InvocationResult
- No result (void methods, exceptions, unauthorized): `[invocationId: uint16, state: uint8]`
- With a result, which may itself be nil: `[invocationId: uint16, state: uint8, result: embedded value]`

`state`: 0 Unset, 1 CompletedResult, 2 Exception, 3 Unauthorized.

### 4.5 InvocationCancellation
`[invocationId: int]`

### 4.6 DuplexPipeUpdateState
`[pipeId: uint16, state: uint8]`

### 4.7 Collection synchronization (channel items on the collection pipe)

Each item is a union value: `[tag: uint16, body: array]`. The first element of every body is `flags: uint8`.

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

### 4.8 Typed channel items
A typed channel (`INexusDuplexChannel<T>`) sends its items as the raw bytes of `DuplexPipeWrite` frames on its pipe:

- Each item is exactly one embedded value (§5).
- Items are concatenated with no length prefix or separator.
- The byte stream is independent of frame boundaries. An item may be split across several frames, and one frame
  may carry many items.

A reader decodes every complete item it has buffered and keeps an incomplete trailing item until more bytes arrive.
A buffered incomplete item larger than the configured limit (16 MiB by default, §6) is a protocol error.

## 5. Payload encoding

### 5.1 Canonical encoding
Writers always use the smallest form. Non-negative integers use the unsigned family (`fixint`, `uint8`/`16`/`32`/`64`);
negative integers use negative `fixint` or `int8`/`16`/`32`/`64`. Floats are always `float32` or `float64`.
Readers accept every valid form and check ranges.

### 5.2 `[NexusObject]` types
- An object is an array of length `maxKey + 1`. Element *i* is the member with `[NexusKey(i)]`; unused keys are nil.
- A null reference is nil.
- Readers skip elements past their highest known key and leave members missing from a shorter array at their defaults.

### 5.3 Unions
`[tag: uint16, payload]`, or nil for null. An unknown tag is rejected.

### 5.4 Built-in types

| .NET type | Encoding |
|---|---|
| `bool` | `c2`/`c3` |
| Integers, `char` (as `uint16`) | canonical integer |
| `float` / `double` / `Half` (as `float32`) | `ca` / `cb` |
| `string` | UTF-8 `str`, nil for null |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>` | `bin` |
| Arrays, `List<T>`, `Memory<T>` and `ReadOnlyMemory<T>` of `sbyte, short, ushort, int, uint, long, ulong, float, double, char, Half` | ext 78 (§5.5) |
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

### 5.5 Ext 78: primitive array
Ext type code `78`.
- The data is one element-kind byte followed by the elements' raw **little-endian** bytes.
- Kinds: 1 sbyte, 2 int16, 3 uint16, 4 int32, 5 uint32, 6 int64, 7 uint64, 8 float32, 9 float64, 10 char, 11 Half.
- The data length minus one must be a multiple of the element size.
- An empty array is `d4 4e <kind>`.

## 6. Limits and hardening

Always enforced:
- Array and map counts and `str`/`bin`/`ext` lengths are checked against the remaining bytes before anything is allocated.
- `0xc1` is rejected.
- Skipping is iterative, so nesting depth uses no stack.
- Message bodies must match their exact shape, with no trailing bytes.

With the default untrusted options:
- Container depth is limited to 64.
- Invalid UTF-8 is rejected.
- Dictionaries and sets with non-string keys use randomized hashing.
- A channel item may buffer at most 16 MiB.

Configure these with `ConfigBase.SerializerOptions`.
