# NexNet MessagePack Serializer: Implementation Plan

Status: planning, experiment branch `worktree-messagepack-eval`. Last updated 2026-10-03.

This plan replaces MemoryPack with a serializer that NexNet owns and that writes the MessagePack wire format. The work is staged as an experiment. Both serializers stay buildable side by side until benchmarks show whether the change is worth it. Background research: `messagepack-v4-report.md`.

---

## 0. Decisions

| Area | Decision |
|---|---|
| Approach | **Option C.** NexNet implements the MessagePack format itself. Pieces of MessagePack-CSharp v4 (MIT) may be copied only after review, with attribution. No MessagePack or MemoryPack package dependency once the work is finished. |
| Code location | A **shared project** (`NexNet.Serialization.shproj`) compiled into the `NexNet` assembly. It is not a separate DLL. |
| Experiment structure | Two backends selected by an MSBuild property, `NexNetSerializer=MessagePack|MemoryPack`. |
| What the switch covers | **User payloads only**: method arguments, return values, channel items and collection items. Framing and protocol messages use the new design in both builds. |
| Scope | Full migration, with a benchmark go/no-go gate before MemoryPack is removed. |
| User attributes | NexNet's own: `[NexusObject]`, `[NexusKey(n)]`, `[NexusIgnore]`, `[NexusConstructor]`, `[NexusUnion<T>(tag)]`. Assembly-level: `[NexusSerializable<T>]` and `[NexusFormatter<TFormatter, T>]`. |
| Protocol messages | MessagePack-encoded with hand-written serialize/deserialize code. Deserialization fills in pooled instances. |
| Payload wire format | Standard MessagePack. Primitive arrays and spans are written as one **little-endian ext blob** so they can be copied in bulk. |
| Integer encoding | Compact: the smallest form the spec allows. An internal fixed-width mode exists for benchmarks only. |
| Framing | Explicit **little-endian** body length. The pipe ID is defined as a **byte pair** (client ID, server ID). `ProtocolVersion` goes to 2. Reserved header byte `[4]` carries the **payload-format ID**. Everything is written down in `docs/wire-protocol.md`. |
| BCL types | Favor fidelity and speed: `DateTime` as int64 `ToBinary()`, `Guid` as bin16 in RFC 4122 byte order, `decimal` as bin16. |
| Third-party types | User formatters derived from `NexusFormatter<T>`, registered with `[assembly: NexusFormatter<TFormatter, T>]`. |
| Types only used at runtime | Formatters are generated for every `[NexusObject]` type. Closed BCL generics need `[assembly: NexusSerializable<T>]`. An analyzer flags channel call sites whose `T` has no formatter. |
| Unmanaged channels | **Remove** `INexusDuplexUnmanagedChannel<T>` and its APIs. This is a breaking change. |
| Security | Hardening for untrusted data is **on by default** and can be configured. |
| Wire tests | Extend **StreamStruct** (our repo) with MessagePack field types. |
| Test layout | New projects `NexNet.Serialization.Tests` (unit tests, golden vectors, cross-checks against the official MessagePack package, property tests) and `NexNet.Fuzz` (SharpFuzz). |
| Out of scope | Mitigating MemoryPack issue #381 in the MemoryPack backend. |

---

## 1. Core concepts

### 1.1 The MessagePack format in brief
MessagePack is a binary format that describes itself. Every value starts with one **type byte**. Some type bytes also hold the value: a positive "fixint" (0x00–0x7f) holds 0–127, and a negative fixint (0xe0–0xff) holds −32 to −1. Others say what follows: `0xcd` means "a 2-byte unsigned integer comes next", and `0xd9` means "a 1-byte string length and that many UTF-8 bytes come next". Multi-byte numbers are **big-endian**, as the spec requires.

Containers are length-prefixed:
- An **array** header gives the element count; the elements follow.
- A **map** header gives the number of key/value pairs.
- **bin** is raw bytes with a length prefix.
- **ext** is raw bytes with a length prefix plus a one-byte application type code. Codes 0–127 are free for applications; negative codes are reserved, and −1 is the standard timestamp.

Because every value describes its own shape, a reader can **skip** a value without knowing its schema. NexNet relies on this to detect incomplete items in streams without throwing exceptions.

The type codes NexNet uses:

| Range / code | Meaning |
|---|---|
| `0x00–0x7f` | positive fixint (the value itself) |
| `0x80–0x8f` | fixmap (count in the low 4 bits) |
| `0x90–0x9f` | fixarray (count in the low 4 bits) |
| `0xa0–0xbf` | fixstr (byte length in the low 5 bits) |
| `0xc0` / `0xc2` / `0xc3` | nil / false / true |
| `0xc1` | never used. **Must be rejected** |
| `0xc4` `0xc5` `0xc6` | bin8 / bin16 / bin32 |
| `0xc7` `0xc8` `0xc9` | ext8 / ext16 / ext32 (length, then type code, then data) |
| `0xca` `0xcb` | float32 / float64 |
| `0xcc` `0xcd` `0xce` `0xcf` | uint8 / uint16 / uint32 / uint64 |
| `0xd0` `0xd1` `0xd2` `0xd3` | int8 / int16 / int32 / int64 |
| `0xd4`–`0xd8` | fixext 1 / 2 / 4 / 8 / 16 (type code, then data) |
| `0xd9` `0xda` `0xdb` | str8 / str16 / str32 |
| `0xdc` `0xdd` | array16 / array32 |
| `0xde` `0xdf` | map16 / map32 |
| `0xe0–0xff` | negative fixint |

### 1.2 Canonical (compact) encoding
The spec lets a writer encode the same number in several ways. The integer 5 can be `0x05`, `cc 05`, `cd 00 05`, and so on. NexNet always writes the **smallest** form; this is called canonical or compact encoding. Readers accept **every** valid form. Canonical output has two benefits: it matches what the official MessagePack-CSharp writer produces, so golden-vector tests can compare bytes exactly, and it is the most compact. For positive signed values the official writer uses the unsigned family (`cc`/`cd`/`ce`/`cf`). NexNet does the same, and the golden vectors verify it.

Fixed-width mode (benchmark only) always writes the full-width form for the value's .NET type. For example, an `int` is always `d2` plus 4 bytes. The output is still valid MessagePack, and writing it needs no branches. It exists only to measure how much compact encoding costs.

### 1.3 The NexNet primitive-array ext (little-endian)
Encoding an `int[]` as a MessagePack array means writing a type byte and doing a byte swap for every element. NexNet instead writes arrays and spans of fixed-size primitives as **one ext value**, type code `NexusExtType.PrimitiveArray = 78`. The data is one byte giving the element kind, followed by the elements' raw **little-endian** bytes, copied in a single block with `MemoryMarshal.AsBytes(span)`. This recovers most of MemoryPack's speed advantage on numeric data while the payload stays valid MessagePack: any reader can parse or skip the ext, and other languages only need to know this one ext code. On a big-endian machine the codec swaps each element (`BinaryPrimitives.ReverseEndianness(span, span)`, which is vectorized). Primitive types contain no padding, so a block copy is well defined. `bool[]` is excluded, because a raw copy could carry bytes other than 0 or 1. Booleans stay as normal MessagePack arrays and are validated element by element.

### 1.4 Framing versus payload
The **framing** is NexNet's own envelope, and it is not MessagePack:
- An 8-byte protocol header, sent once per connection.
- For each message: a 1-byte message type, a 2-byte body length, an optional fixed post-header (the pipe ID for `DuplexPipeWrite`), and then the body.

The **body** of every message except `DuplexPipeWrite` is MessagePack. `DuplexPipeWrite` carries raw pipe bytes. The framing uses little-endian on purpose. Every current CPU is little-endian, so encoding and decoding the field costs nothing. The MessagePack bodies are big-endian because the spec says so. `wire-protocol.md` documents this one split explicitly.

### 1.5 Filling pooled instances during deserialization
NexNet pools its message objects. Today `MessagePool<T>` takes an instance from the pool and MemoryPack writes into it (`Deserialize(seq, ref item)`). The new design keeps this pattern. Every protocol message has a hand-written `Deserialize(ref MsgPackReader)` that writes into `this`. Every generated formatter has the signature `Deserialize(ref MsgPackReader, ref T value)` and reuses `value` when it is not null. When reusing an instance, generated code assigns **every** member, using the default value for members missing from the payload, so nothing stale survives from the instance's previous use.

### 1.6 One source generator sees everything
NexNet's source generator already walks every method parameter, return type and collection element so that `TypeHasher` can compute the interface hash. That same walk will now **emit the serializers**. This has three effects:
- Arguments are written inline as a MessagePack array, with no `ValueTuple` and no allocation.
- Generated code calls other generated code directly, with no resolver lookup.
- The hash and the serializer come from the same walk, so the version hash checked at the handshake always matches what is actually written.

This removes the AOT problem a third-party serializer would have caused, where two generators cannot see each other's output.

### 1.7 Types that are only known at runtime
Some `T` values never appear in a nexus interface. Examples are `client.CreateChannel<T>()` and `pipe.GetChannelWriter<T>()`. Generated code cannot call those formatters directly, so they are looked up in a static generic cache, `NexusFormatterCache<T>.Formatter`. Each assembly's generated code fills the cache from a `[ModuleInitializer]`. Closed generic types made from BCL generics (`List<Foo>`, `Dictionary<string, Foo>`) that only appear at runtime must be declared with `[assembly: NexusSerializable<List<Foo>>]`. An analyzer reports channel call sites whose `T` has no formatter.

### 1.8 Untrusted-data hardening
The server deserializes bytes that remote clients send, and those clients may be hostile. MessagePack-CSharp v3 published 14 advisories in four months for problems in this area. The defenses are:
- **Depth limit.** Generated and built-in formatters count nesting depth. The default limit is 64, and it is configurable.
- **Length checks before allocating.** An array or map header that claims more elements than there are bytes left is rejected; each element needs at least one byte. `str`, `bin` and `ext` lengths are checked against the remaining bytes.
- **Skip without recursion.** `TrySkip` uses a loop with a pending-element counter. Recursion is what caused the v3 stack-overflow advisory.
- **Strict UTF-8** when reading. Invalid sequences throw instead of being replaced silently.
- **Hash-flooding resistance.** Dictionaries and hash sets deserialized from untrusted data use a comparer that adds a random seed for non-string keys. `HashCode.Combine` is already seeded per process. String keys are already protected by .NET's randomized string hashing.
- **Size caps.** A cap on how much a single channel item may buffer, plus the existing 64 KiB message body limit.

---

## 2. Architecture

### 2.1 Layers
The work touches four layers.

1. **Framing** (`NexusSession.Sending/Receiving`, `NexusPipeWriter`, `NexusPipeManager`). Only byte order and protocol-header changes. It is the same in both backends.
2. **Protocol messages** (`src/NexNet/Messages/*`, collection messages). The MemoryPack attributes are replaced by hand-written MessagePack code. Same in both backends.
3. **The serializer core** (the new shared project `NexNet.Serialization`). It contains the writer, reader, formatter base class, registry, built-in formatters, ext codec, options, attributes and exception type. It is compiled into NexNet in both builds, because protocol messages and collection unions use it.
4. **Payload serialization** (generated code plus the runtime paths for channels, collections and results). This is the only layer the backend switch affects. Under `MessagePack` it uses `NexNet.Serialization`; under `MemoryPack` it uses the current MemoryPack calls.

The source generator (`NexNet.Generator`) is updated to emit the serializer code for layer 4, to read the backend switch, and to hash `[NexusObject]` types in `TypeHasher`.

### 2.2 The shared project
`src/NexNet.Serialization/NexNet.Serialization.shproj` and `NexNet.Serialization.projitems` hold the sources. `NexNet.csproj` imports them:

```xml
<Import Project="..\NexNet.Serialization\NexNet.Serialization.projitems" Label="Shared" />
```

The public types live in the `NexNet.Serialization` namespace inside the NexNet assembly. Test projects **reference `NexNet`** rather than importing the shared project, so the same type is never defined twice (CS0436). `NexNet.Serialization.Tests` and `NexNet.Fuzz` are added to the existing `InternalsVisibleTo` list in `NexNet.csproj`.

**What this means for users:** shared DTO assemblies now reference `NexNet` to get the attributes, where today they reference MemoryPack. If that turns out to be a problem, the attributes can be split into a second shared project, `NexNet.Serialization.Attributes.projitems`. A DTO project could then import it with `internal` visibility. The generator matches attributes by fully qualified metadata name, so a copy defined in another assembly is still recognized. This is recorded as an open question (§14).

Proposed file layout:

| Path (under `src/NexNet.Serialization/`) | Contents |
|---|---|
| `Attributes/*.cs` | `NexusObjectAttribute`, `NexusKeyAttribute`, `NexusIgnoreAttribute`, `NexusConstructorAttribute`, `NexusUnionAttribute<T>`, `NexusSerializableAttribute<T>`, `NexusFormatterAttribute<TFormatter,T>` |
| `MsgPackCode.cs` | Type-code constants and range helpers |
| `MsgPackWriter.cs` plus partials `.Integers.cs`, `.Strings.cs`, `.Binary.cs`, `.Ext.cs` | The writer |
| `MsgPackReader.cs` plus partials `.Integers.cs`, `.Strings.cs`, `.Binary.cs`, `.Ext.cs`, `.Skip.cs` | The reader |
| `NexusFormatter.cs`, `NexusFormatterCache.cs`, `NexusFormatterRegistry.cs` | Formatter base class and lookup |
| `NexusSerializerOptions.cs`, `SerializerSecurity.cs`, `NexusSerializationException.cs` | Options and errors |
| `Formatters/*.cs` | Built-in formatters (§5.7) |
| `Ext/NexusExtType.cs`, `Ext/PrimitiveArrayCodec.cs`, `Ext/PrimitiveKind.cs` | The ext type |
| `Internal/RandomizedEqualityComparer.cs`, `Internal/PooledArrayBufferWriter.cs` | Helpers |

### 2.3 How the backend switch works
`src/NexNet.props` declares the property and makes it visible to the generator:

```xml
<PropertyGroup>
  <NexNetSerializer Condition="'$(NexNetSerializer)' == ''">MessagePack</NexNetSerializer>
  <DefineConstants Condition="'$(NexNetSerializer)' == 'MemoryPack'">$(DefineConstants);NEXNET_MEMORYPACK</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <CompilerVisibleProperty Include="NexNetSerializer" />
</ItemGroup>
```

`NexNet.csproj` references `MemoryPack` only when `NexNetSerializer == MemoryPack`. Test projects **always** reference MemoryPack, test-only, so DTOs carrying both attribute sets compile in either build without `#if`. The generator reads the value in an incremental pipeline step:

```csharp
var backend = context.AnalyzerConfigOptionsProvider.Select(static (p, _) =>
    p.GlobalOptions.TryGetValue("build_property.NexNetSerializer", out var v)
    && string.Equals(v, "MemoryPack", StringComparison.OrdinalIgnoreCase)
        ? SerializerBackend.MemoryPack
        : SerializerBackend.MessagePack);
```

The backend value is combined into the nexus pipeline. `MethodEmitter` picks MemoryPack or MessagePack code accordingly, and `TypeHasher` picks its walk mode the same way (§6.6). A NuGet consumer gets the `CompilerVisibleProperty` from a `build/NexNet.Generator.props` file inside the generator package. After the go decision (§12) the switch and the MemoryPack path are deleted.

The **payload-format byte** in the protocol header is `1` when `NEXNET_MEMORYPACK` is defined and `2` otherwise. A MemoryPack build and a MessagePack build therefore refuse each other at the protocol header with a clear log message, instead of producing deserialization garbage.

---

## 3. Phase 0: Framing and protocol header

Goal: framing is defined exactly regardless of the machine, and peers can detect a mismatched protocol version or payload format. No serializer changes yet.

### 3.1 Byte-order inventory and fixes

| Site | Today | Change |
|---|---|---|
| `NexusSession.Sending.cs:65`, `:163`, `:176` | `BitConverter.TryWriteBytes(span, contentLength)` | `BinaryPrimitives.WriteUInt16LittleEndian(span, contentLength)` |
| `ReadingHelpers.TryReadUShort` (used at `Receiving.cs:233`, `:250`) | `BitConverter.ToUInt16` | `BinaryPrimitives.ReadUInt16LittleEndian` |
| `ReadingHelpers` `TryReadULong`/`TryReadInt`/`TryReadUInt` | dead code | Delete |
| `NexusSession.cs:40`, `Receiving.cs:387` (protocol tag) | Reads the magic bytes as a native `uint` and compares | Compare bytes with `header[..4].SequenceEqual("NnP\x14"u8)`. Same behavior, but obviously independent of byte order |
| `NexusPipeManager.cs:225`, `:243` (pipe ID composition) | `BitConverter.ToUInt16([client, server])` | Explicit: `(ushort)(clientId | (serverId << 8))` |
| `NexusPipeWriter.cs:179` (pipe ID in frame) | `BitConverter.TryWriteBytes(_pipeId.Span, id)` | Write two bytes: `span[0] = (byte)id; span[1] = (byte)(id >> 8);` (client byte, then server byte) |
| `Receiving.cs:250` (pipe ID read) | `TryReadUShort` | Read two bytes and compose them explicitly |
| `NexusServer.cs:491` (`GenerateSecureSessionId`) | `BitConverter.ToUInt32(random)` | No change. Local randomness, not part of the wire format |

**Why define the pipe ID as a byte pair.** A pipe ID is really two independent 1-byte IDs: the client's local pipe number and the server's. It travels in two forms: as raw frame bytes, and as a number inside payloads (`DuplexPipeUpdateStateMessage.PipeId`). Writing the composition down as `client | (server << 8)`, with the frame always carrying the client byte first, means both forms agree on any machine. The composed value equals what little-endian machines produce today, so the frame bytes do not change.

### 3.2 Protocol header v2
Layout: `[N][n][P][0x14][PayloadFormat][0x00][0x00][ProtocolVersion=2]`.

- Add `internal enum PayloadFormat : byte { MemoryPack = 1, MessagePack = 2 }` and `NexusSession.LocalPayloadFormat`, set from `NEXNET_MEMORYPACK`.
- `SendProtocolHeader` writes byte 4. `ConfirmProtocol` now checks that byte 4 equals `LocalPayloadFormat`; previously it required 0. Bytes 5 and 6 must still be zero.
- A mismatch causes `DisconnectReason.ProtocolError` with the trace log `"Payload format mismatch: local {x}, remote {y}"`. No new disconnect code is needed.
- Bump `ProtocolVersion` to 2.

### 3.3 Spec document
Write `docs/wire-protocol.md` (and link it from DocFX). It covers:
- the connection preamble;
- the message type table (from `MessageType.cs`) and which side may send each type;
- the frame layout, including the little-endian body length;
- the `DuplexPipeWrite` post-header (client byte, server byte);
- the MessagePack body of every protocol message (§4);
- the payload encoding rules for generated types (§6.2), the built-in type representations (§5.7) and the ext type (§1.3);
- disconnect semantics and limits (65,535-byte body, argument limit, depth).

### 3.4 Tests
- StreamStruct: confirm `ushort` is little-endian on any host. If its byte order is not explicit, add `ushort_le`/`ushort_be` (§10.2).
- Update `RawTcpClient.SendProtocolHeaderAsync` with the format byte and version 2. Add tests for a payload-format mismatch and for a version-1 header, both expecting a protocol error.
- Pipe-ID tests: compose and decompose, and check that frame bytes put the client byte first.

**Exit criteria:** the existing suite passes with MemoryPack still serializing the payloads; the new header tests pass.

---

## 4. Phase 1: Protocol messages as hand-written MessagePack

This phase depends on the core writer and reader from Phase 2 (§5). In practice, build §5.1–5.4 first, then this phase, then the rest of §5.

### 4.1 Message contract
`IMessageBase` gains two methods, and the MemoryPack attributes are removed from all message classes:

```csharp
internal interface IMessageBase : IDisposable
{
    static abstract MessageType Type { get; }
    IPooledMessage? MessageCache { set; }

    /// Writes this message's MessagePack body.
    void Serialize(ref MsgPackWriter writer);

    /// Populates this (pooled) instance from a MessagePack body.
    void Deserialize(ref MsgPackReader reader);
}
```

Every body is a **fixed-length MessagePack array**, and readers require the exact element count. Strictness is what we want here: the protocol version gates any change to a message.

### 4.2 Body layouts

| Message | Array elements, in order |
|---|---|
| `ClientGreetingMessage` / `ClientGreetingReconnectionMessage` | `version` (str or nil), `serverNexusHash` (int), `clientNexusHash` (int), `authenticationToken` (bin or nil) |
| `ServerGreetingMessage` | `version` (int), `clientId` (int64) |
| `InvocationMessage` | `invocationId` (uint16), `methodId` (uint16), `flags` (uint8), `arguments` (**an embedded MessagePack array**, §4.3) |
| `InvocationResultMessage` | `invocationId` (uint16), `state` (uint8), and when there is a result, `result` (an embedded MessagePack value). **2 elements = no result** (void); **3 elements = a result is present**, which may itself be nil |
| `InvocationCancellationMessage` | `invocationId` (int) |
| `DuplexPipeUpdateStateMessage` | `pipeId` (uint16, `client | server << 8`), `state` (uint8) |
| Collection list messages | Union: `[tag, [flags, ...fields]]` with the current tags 0–8 (§9) |

### 4.3 Embedding values instead of wrapping them in `bin`
Arguments, results and collection values are **already MessagePack**, produced by generated or registry formatters. Wrapping them in `bin` would add a header of up to 3 bytes and turn the body into "MessagePack containing opaque bytes". Instead the writer copies them in directly with `WriteRaw`, so the whole body is one valid MessagePack value. On read, `ReadRawValue()` uses `TrySkip` to find the extent of exactly one value and returns that slice. Skipping also validates the structure before anything is allocated.

```csharp
// InvocationMessage
public void Serialize(ref MsgPackWriter writer)
{
    writer.WriteArrayHeader(4);
    writer.Write(InvocationId);
    writer.Write(MethodId);
    writer.Write((byte)Flags);
    writer.WriteRaw(Arguments.Span);            // a MessagePack array produced by generated code
}

public void Deserialize(ref MsgPackReader reader)
{
    if (reader.ReadArrayHeader() != 4)
        throw NexusSerializationException.InvalidMessage(Type);

    InvocationId = reader.ReadUInt16();
    MethodId = reader.ReadUInt16();
    Flags = (InvocationFlags)reader.ReadByte();

    var raw = reader.ReadRawValue();            // validated slice of exactly one value
    var length = checked((int)raw.Length);
    var rented = ArrayPool<byte>.Shared.Rent(length);
    raw.CopyTo(rented);
    Arguments = rented.AsMemory(0, length);
    _isArgumentPoolArray = true;                // replaces [MemoryPackOnDeserialized]
}
```

The copy into a pooled array is needed because the receive buffer is reused after the handler returns. The same thing happens today through `[MemoryPoolFormatter<byte>]`.

### 4.4 Argument limit
The body is still limited to 65,535 bytes. The worst-case overhead of an invocation body is 1 byte (fixarray header) + 3 (uint16 invocation ID) + 3 (uint16 method ID) + 2 (uint8 flags) = 9 bytes. So `IInvocationMessage.MaxArgumentSize` becomes `ushort.MaxValue - 9` = **65,526**, written as a calculated constant with a comment, replacing the MemoryPack-derived 65,521. Methods with no serialized parameters still send an empty array (`0x90`) so every body has the same shape.

### 4.5 Send and receive paths
- `NexusSession.Sending.cs:59`: replace `MemoryPackSerializer.Serialize(_bufferWriter, body)` with:
  ```csharp
  var writer = new MsgPackWriter(_bufferWriter);
  body.Serialize(ref writer);
  writer.Flush();
  ```
- `MessagePool<T>.Deserialize`/`DeserializeInterface` (`MessagePool.cs:92`, `:118`): take an instance from the pool as today, then:
  ```csharp
  var reader = new MsgPackReader(bodySequence, _options);
  item.Deserialize(ref reader);
  if (reader.Remaining != 0)
      throw NexusSerializationException.TrailingBytes(T.Type);
  ```
  `_options` comes from the session's `NexusSerializerOptions` (§8). `PoolManager` passes them to each pool when it is created.
- The receive loop already turns any exception from deserialization into `DisconnectReason.ProtocolError` (`Receiving.cs` catch block), so no change is needed there.
- `ClientGreetingMessage.Dispose` keeps clearing the authentication token before returning its buffer to the pool.

### 4.6 Tests
- Golden vectors: one hex vector per message with representative values, checked in as `.hex` text, compared in both directions.
- Cross-checks: each body must decode with MessagePack-CSharp as a generic `object[]` with the expected values (§10.1).
- Malformed input: wrong array count, trailing bytes, a truncated embedded value, `0xc1`, a huge `bin` length. Each must be rejected without a large allocation.
- `RawTcpClient`: replace `MemoryPackSerializer.Serialize/Deserialize` with StreamStruct MessagePack definitions (§10.2).

**Exit criteria:** all integration tests pass in the MemoryPack build (payloads still MemoryPack, protocol now MessagePack), and the message golden vectors pass.

---

## 5. Phase 2: The serializer core (`NexNet.Serialization`)

### 5.1 Writer
`MsgPackWriter` is a `ref struct` over `IBufferWriter<byte>`. It keeps the current span from the output and a count of bytes written but not yet committed, so it only calls `Advance` and `GetSpan` when the span runs out or at `Flush`. A call through the `IBufferWriter` interface therefore happens once per span refill, not once per value.

```csharp
public ref struct MsgPackWriter
{
    private readonly IBufferWriter<byte> _output;
    private Span<byte> _span;        // span obtained from _output, not yet advanced
    private int _buffered;           // bytes written into _span
    internal bool FixedWidth;        // benchmark-only (§1.2)

    public MsgPackWriter(IBufferWriter<byte> output)
    {
        _output = output;
        _span = default;
        _buffered = 0;
        FixedWidth = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Reserve(int size)
    {
        if (_span.Length - _buffered < size)
            Refill(size);
        return _span.Slice(_buffered);
    }

    private void Refill(int size)
    {
        Flush();
        _span = _output.GetSpan(Math.Max(size, 512));
    }

    public void Flush()
    {
        if (_buffered > 0)
        {
            _output.Advance(_buffered);
            _buffered = 0;
        }
        _span = default;
    }
}
```

**Rule:** every code path that writes ends with `Flush()`. Generated code calls `Flush` once, after the whole value. Nested formatters do not flush.

**Integer algorithm.** Pick the smallest form with one comparison ladder. For positive values, `uint` handles everything:

```csharp
public void Write(int value)
{
    if (FixedWidth) { WriteInt32Forced(value); return; }
    if (value >= 0) { Write((uint)value); return; }

    Span<byte> s;
    if (value >= -32)                    // negative fixint 0xe0..0xff
    {
        s = Reserve(1);
        s[0] = unchecked((byte)value);
        _buffered += 1;
    }
    else if (value >= sbyte.MinValue)
    {
        s = Reserve(2);
        s[0] = MsgPackCode.Int8;
        s[1] = unchecked((byte)value);
        _buffered += 2;
    }
    else if (value >= short.MinValue)
    {
        s = Reserve(3);
        s[0] = MsgPackCode.Int16;
        BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
        _buffered += 3;
    }
    else
    {
        s = Reserve(5);
        s[0] = MsgPackCode.Int32;
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), value);
        _buffered += 5;
    }
}

public void Write(uint value)
{
    Span<byte> s;
    if (value <= 0x7f)       { s = Reserve(1); s[0] = (byte)value; _buffered += 1; }
    else if (value <= 0xff)  { s = Reserve(2); s[0] = MsgPackCode.UInt8; s[1] = (byte)value; _buffered += 2; }
    else if (value <= 0xffff){ s = Reserve(3); s[0] = MsgPackCode.UInt16; BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)value); _buffered += 3; }
    else                     { s = Reserve(5); s[0] = MsgPackCode.UInt32; BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), value); _buffered += 5; }
}
```

The same pattern covers `long`/`ulong` (adding the 64-bit forms), `short`/`ushort`, and `sbyte`/`byte`. `float` and `double` always use `ca`/`cb` with a big-endian write of `BitConverter.SingleToInt32Bits`/`DoubleToInt64Bits`. They are never shrunk, so values round-trip bit for bit.

**String algorithm (UTF-8 with the header filled in afterwards).** A MessagePack string header needs the UTF-8 **byte** length, which is unknown until the string is encoded. Instead of encoding twice, the writer reserves room for the largest header the string could need, encodes straight into the buffer, and then moves the bytes back if a smaller header turns out to be enough.

1. If `value` is null, write nil.
2. Compute `max = Encoding.UTF8.GetMaxByteCount(chars.Length)`.
3. **Fast path:** if `max <= 31`, reserve `1 + max`, encode at offset 1, and write `0xa0 | written` at offset 0. Done.
4. If `chars.Length` is above a large-string threshold (64 KiB of chars), call `Encoding.UTF8.GetByteCount` first to get the exact length. This avoids reserving 3× the size. Then write the exact header and encode.
5. Otherwise choose `headerSize` from `max` (2 for ≤255, 3 for ≤65,535, else 5), reserve `headerSize + max`, and encode at offset `headerSize`.
6. Work out the header size the actual `written` length needs (1 if ≤31, 2 if ≤255, 3 if ≤65,535, else 5). If it is smaller than `headerSize`, move the bytes back with `span.Slice(headerSize, written).CopyTo(span.Slice(needed))`. `Span.CopyTo` handles overlapping regions correctly.
7. Write the header for `written` at offset 0 and advance by `needed + written`.

Encoding uses `System.Text.Unicode.Utf8.FromUtf16(chars, dest, out _, out written, replaceInvalidSequences: true)`. Lone surrogates become U+FFFD on write, matching .NET's default encoder. The outcome is canonical output (the smallest header), at most one encoding pass, and usually no memmove for ASCII-heavy strings longer than 31 bytes. Benchmarks will compare this with an "assume ASCII first" variant (§11).

**Other writer methods:** `WriteNil`, `Write(bool)`, `WriteArrayHeader(int)`, `WriteMapHeader(int)`, `WriteBinary(ReadOnlySpan<byte>)` and `WriteBinary(ReadOnlySequence<byte>)` (header, then copy), `WriteExtHeader(sbyte typeCode, int length)`, `WriteRaw(ReadOnlySpan<byte>)` and `WriteRaw(ReadOnlySequence<byte>)`, and `WriteInt32Forced` with the other fixed-width variants. Every header method picks the smallest form.

### 5.2 Reader
`MsgPackReader` is a `ref struct` that wraps `SequenceReader<byte>`, which handles multi-segment input. It also holds the options and the current depth.

```csharp
public ref struct MsgPackReader
{
    private SequenceReader<byte> _reader;
    private readonly NexusSerializerOptions _options;
    private int _depth;

    public MsgPackReader(ReadOnlySequence<byte> source, NexusSerializerOptions options) { ... }
    public MsgPackReader(ReadOnlyMemory<byte> source, NexusSerializerOptions options) { ... }

    public long Consumed => _reader.Consumed;
    public long Remaining => _reader.Remaining;
}
```

The reader has two kinds of method:
- **Strict reads** (`ReadInt32`, `ReadString`, `ReadArrayHeader`, …) for a region already known to be complete. Truncated or malformed data throws `NexusSerializationException`.
- **`TrySkip`**, the completeness probe, which returns `false` when the data is incomplete.

Copying a `MsgPackReader` (`var probe = reader;`) takes a cheap snapshot, because `SequenceReader<byte>` is itself a struct. That is how the channel reader probes ahead without disturbing its position (§7.2).

**Integer read algorithm.** Read the type byte, then branch:

```csharp
public int ReadInt32()
{
    var code = ReadCode();
    if (code <= MsgPackCode.MaxFixPositive) return code;               // 0x00..0x7f
    if (code >= MsgPackCode.MinFixNegative) return unchecked((sbyte)code); // 0xe0..0xff
    return code switch
    {
        MsgPackCode.UInt8  => ReadByteRaw(),
        MsgPackCode.UInt16 => ReadUInt16BigEndian(),
        MsgPackCode.UInt32 => checked((int)ReadUInt32BigEndian()),
        MsgPackCode.UInt64 => checked((int)ReadUInt64BigEndian()),
        MsgPackCode.Int8   => unchecked((sbyte)ReadByteRaw()),
        MsgPackCode.Int16  => ReadInt16BigEndian(),
        MsgPackCode.Int32  => ReadInt32BigEndian(),
        MsgPackCode.Int64  => checked((int)ReadInt64BigEndian()),
        _ => throw NexusSerializationException.UnexpectedCode(code, "integer"),
    };
}
```

Every integer read accepts all eight integer forms and checks the range (an overflow throws). `checked` turns overflow into an `OverflowException`, which is wrapped as `NexusSerializationException`. The big-endian reads use `SequenceReader`'s `TryReadBigEndian`.

**Length checks.** `ReadArrayHeader` returns the count only if `count <= Remaining`, because each element needs at least one byte. `ReadMapHeader` requires `2 * count <= Remaining`. `str`, `bin` and `ext` lengths must be `<= Remaining`. These checks run **before** the caller allocates anything, and they are always on; they cost one comparison each.

**Depth.** Generated and built-in container formatters call `reader.Enter()` before reading children and `reader.Exit()` after. `Enter` throws when `++_depth > _options.MaxDepth`.

**Strings.** `ReadString` handles nil and the fixstr/str8/str16/str32 forms. It decodes from the unread span when the bytes are contiguous, which is the common case; otherwise it copies into a pooled or stack buffer first. Decoding uses `Utf8.ToUtf16(..., replaceInvalidSequences: !_options.StrictUtf8)`. Strict mode (the default when the data is untrusted) throws on invalid UTF-8.

**Binary.** `ReadBinary()` returns a `ReadOnlySequence<byte>` slice, so nothing is copied. `ReadBinaryToPooled()` rents a pooled array and copies into it, for message fields.

### 5.3 Skipping without recursion
```csharp
/// Advances past exactly one MessagePack value.
/// Returns false (position unchanged) if the data ends before the value is complete.
/// Throws on malformed data (e.g. 0xc1).
public bool TrySkip()
{
    var r = _reader;                 // work on a copy; commit only on success
    long pending = 1;                // values still to skip
    while (pending > 0)
    {
        if (!r.TryRead(out byte code)) return false;
        pending--;
        long skipBytes;
        switch (code)
        {
            case <= 0x7f: case >= 0xe0: case 0xc0: case 0xc2: case 0xc3:
                skipBytes = 0; break;                                  // value is in the type byte
            case >= 0x80 and <= 0x8f: pending += 2L * (code & 0x0f); skipBytes = 0; break; // fixmap
            case >= 0x90 and <= 0x9f: pending += code & 0x0f; skipBytes = 0; break;        // fixarray
            case >= 0xa0 and <= 0xbf: skipBytes = code & 0x1f; break;                       // fixstr
            case 0xcc: case 0xd0: skipBytes = 1; break;
            case 0xcd: case 0xd1: skipBytes = 2; break;
            case 0xca: case 0xce: case 0xd2: skipBytes = 4; break;
            case 0xcb: case 0xcf: case 0xd3: skipBytes = 8; break;
            case 0xd4: skipBytes = 2; break;  case 0xd5: skipBytes = 3; break;               // fixext: type byte + data
            case 0xd6: skipBytes = 5; break;  case 0xd7: skipBytes = 9; break;
            case 0xd8: skipBytes = 17; break;
            case 0xc4: case 0xd9: if (!TryReadLength8(ref r, out skipBytes)) return false; break;
            case 0xc5: case 0xda: if (!TryReadLength16(ref r, out skipBytes)) return false; break;
            case 0xc6: case 0xdb: if (!TryReadLength32(ref r, out skipBytes)) return false; break;
            case 0xc7: if (!TryReadLength8(ref r, out skipBytes)) return false; skipBytes += 1; break;   // + ext type byte
            case 0xc8: if (!TryReadLength16(ref r, out skipBytes)) return false; skipBytes += 1; break;
            case 0xc9: if (!TryReadLength32(ref r, out skipBytes)) return false; skipBytes += 1; break;
            case 0xdc: if (!TryReadLength16(ref r, out var a16)) return false; pending += a16; skipBytes = 0; break;
            case 0xdd: if (!TryReadLength32(ref r, out var a32)) return false; pending += a32; skipBytes = 0; break;
            case 0xde: if (!TryReadLength16(ref r, out var m16)) return false; pending += 2 * m16; skipBytes = 0; break;
            case 0xdf: if (!TryReadLength32(ref r, out var m32)) return false; pending += 2 * m32; skipBytes = 0; break;
            default: throw NexusSerializationException.UnexpectedCode(code, "any"); // 0xc1
        }
        if (r.Remaining < skipBytes) return false;
        r.Advance(skipBytes);
    }
    _reader = r;
    return true;
}
```

`pending` is a `long`, so headers that claim billions of elements cannot overflow it. The loop is bounded by the input length, because every pending value needs at least one byte and the loop returns `false` once the data runs out. Deeply nested input costs no stack. `ReadRawValue()` is `TrySkip` that also returns the skipped slice; it throws, rather than returning `false`, when the data is truncated.

### 5.4 Formatter base class, cache and registry
```csharp
public abstract class NexusFormatter<T>
{
    public abstract void Serialize(ref MsgPackWriter writer, T value);
    /// Populate-capable: reuses <paramref name="value"/> when non-null and the type allows it.
    public abstract void Deserialize(ref MsgPackReader reader, ref T value);
}

public static class NexusFormatterCache<T>
{
    internal static NexusFormatter<T>? Formatter;
}

public static class NexusFormatterRegistry
{
    public static void Register<T>(NexusFormatter<T> formatter)
        => Interlocked.CompareExchange(ref NexusFormatterCache<T>.Formatter, formatter, null);

    public static NexusFormatter<T> Get<T>()
        => NexusFormatterCache<T>.Formatter ?? throw NexusSerializationException.MissingFormatter(typeof(T));
}
```

- **Generated formatters** are `sealed`, with `public static readonly X Instance`. Because `Instance` is declared as the sealed type, a call like `X.Instance.Serialize(ref w, v)` gets **devirtualized** by the JIT, so generated code calling generated code needs no virtual dispatch. Code that only knows `T` at runtime (channels, collections) goes through `NexusFormatterCache<T>`, which costs one virtual call.
- **Registration is first-wins.** The same type can be registered from several assemblies (for example, a DTO assembly and a consuming assembly that both generate a formatter for it). Generated output for the same type is deterministic, so whichever registers first is equivalent.
- **Built-ins** (§5.7) are registered from a `[ModuleInitializer]` in the NexNet assembly. Generic built-ins (`ListFormatter<T>`, `ArrayFormatter<T>`, …) are **closed by generated code**, for example `Register(new ListFormatter<Foo>(Foo_Formatter.Instance))`. Nothing calls `MakeGenericType`, so this stays AOT-safe.

### 5.5 Primitive-array ext codec
```csharp
internal enum PrimitiveKind : byte
{
    SByte = 1, Int16 = 2, UInt16 = 3, Int32 = 4, UInt32 = 5,
    Int64 = 6, UInt64 = 7, Single = 8, Double = 9, Char = 10, Half = 11,
}
```

**Writing** `ReadOnlySpan<T>` where `T` is one of the kinds above:
1. `byteLength = 1 + span.Length * sizeof(T)`.
2. Write the smallest ext header for `byteLength` with code 78: fixext1/2/4/8/16 when `byteLength` matches exactly, otherwise ext8/16/32.
3. Write the kind byte.
4. Reserve `span.Length * sizeof(T)` bytes. On a little-endian machine, `MemoryMarshal.AsBytes(span).CopyTo(dest)`. On a big-endian machine, copy and then `BinaryPrimitives.ReverseEndianness` over the destination (floats are reinterpreted as same-size integers with `MemoryMarshal.Cast`).

For very large arrays, write in chunks through `Reserve`/`Flush` so no single huge span is needed.

**Reading:**
1. Read the ext header, require type code 78, and check `length <= Remaining` (as for all lengths).
2. Read the kind byte and require it to match `T`; otherwise throw.
3. Check that `(length - 1) % sizeof(T) == 0`.
4. Allocate `count = (length - 1) / sizeof(T)` elements (or, when filling an existing instance, reuse it if the count matches).
5. Copy into `MemoryMarshal.AsBytes(result.AsSpan())`, then swap bytes on big-endian machines.

**What uses it:** `T[]`, `List<T>` (writes with `CollectionsMarshal.AsSpan`; reads with `CollectionsMarshal.SetCount` and then copies into `AsSpan`), `Memory<T>`/`ReadOnlyMemory<T>`, and `ImmutableArray<T>`, for each primitive `T` above. `byte[]` and `Memory<byte>` stay as standard `bin`. `bool[]` stays as a normal array.

The ext is used **always**, not above some size threshold. That keeps the wire representation of a given type deterministic, which `TypeHasher` and the golden vectors rely on. An empty array is ext with length 1, containing only the kind byte. A null array is nil.

### 5.6 Exceptions
`NexusSerializationException : Exception` has static factories (`UnexpectedCode`, `InvalidMessage`, `TrailingBytes`, `MissingFormatter`, `DepthExceeded`, `LengthExceedsRemaining`, `InvalidUtf8`, `UnknownUnionTag`, `UnknownUnionType`, `ArgumentCountMismatch`). Exception messages never include payload bytes, so untrusted data cannot leak into logs.

### 5.7 Built-in types and how they are written

| .NET type | Wire representation |
|---|---|
| `bool` | `c2`/`c3` |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | Compact integer (§5.1) |
| `float` / `double` | `ca` / `cb` (never shrunk) |
| `Half` | float32 (widened) |
| `char` | uint16 integer |
| `string` | str (UTF-8), nil for null |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>` | bin |
| Arrays, lists, memory and immutable arrays of a primitive kind | Ext 78 (§5.5) |
| `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `IEnumerable<T>` (deserialized as `List<T>`/`T[]`), `HashSet<T>`, `Queue<T>`, `Stack<T>` | array |
| `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` | map (with a randomized comparer when untrusted, §1.8) |
| `Nullable<T>` | nil or the `T` value |
| Enums | The underlying integer, compact |
| `DateTime` | int64 `ToBinary()` (keeps `Kind`) |
| `DateTimeOffset` | array(2): `[int64 UtcTicks, int16 offsetMinutes]` |
| `TimeSpan` | int64 ticks |
| `DateOnly` / `TimeOnly` | int32 `DayNumber` / int64 ticks |
| `Guid` | bin16, RFC 4122 big-endian byte order (`TryWriteBytes(span, bigEndian: true, out _)`) |
| `decimal` | bin16: `decimal.GetBits` as four little-endian int32 values (lo, mid, hi, flags) |
| `Uri` | str (`OriginalString`) |
| `Version` | str |
| `BigInteger` | bin, little-endian two's complement (`TryWriteBytes`) |
| `ValueTuple<T1..T7>`, `Tuple<...>` | array(N) |
| `KeyValuePair<K,V>` | array(2) |

Each entry has a golden vector, and where the representation matches MessagePack-CSharp, a cross-check against the official package.

**Exit criteria for Phase 2:** writer and reader unit tests, golden vectors, cross-checks against MessagePack-CSharp, `TrySkip` property tests, and the first fuzz harness (§10.4) running with no crashes for 30 minutes.

---

## 6. Phase 3: Attributes and generated formatters

### 6.1 Attributes
```csharp
namespace NexNet.Serialization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class NexusObjectAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class NexusKeyAttribute(int key) : Attribute { public int Key { get; } = key; }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class NexusIgnoreAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Constructor)]
public sealed class NexusConstructorAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class NexusUnionAttribute<T>(ushort tag) : Attribute { public ushort Tag { get; } = tag; }

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NexusSerializableAttribute<T> : Attribute { }

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NexusFormatterAttribute<TFormatter, T> : Attribute
    where TFormatter : NexusFormatter<T>, new() { }
```

### 6.2 How objects are encoded
A `[NexusObject]` class or struct is written as a **MessagePack array of length `maxKey + 1`**. Element *i* holds the member with `[NexusKey(i)]`. Unused keys in between are written as nil. A null reference is nil.

When reading:
- Elements beyond the reader's highest known key are skipped. This tolerates older readers, although `TypeHasher` normally rejects mismatched schemas at the handshake.
- A shorter array leaves the trailing members at their defaults.

Integer keys only. String-keyed maps are out of scope, because they are bigger and slower and NexNet controls both ends.

### 6.3 Constructing objects
The generator picks one construction strategy per type:

1. **A `[NexusConstructor]` constructor, or the only public constructor with parameters.** Read every keyed member into a local first, then call `new T(arg1, arg2, ...)`. Constructor parameters are matched to members by name, case-insensitively. Any remaining settable or `init` members are set in an object initializer, `new T(a, b) { C = c }`. `required` members are always included in the initializer. Types built this way cannot reuse a pooled instance and always create a new one.
2. **A parameterless constructor.** `value ??= new T();`, then assign every member, using defaults for members missing from the payload. This reuses the instance.
3. **A struct with no constructor.** Fill in `value` directly.

**Members that aren't public** carry `[NexusKey]` and use `[UnsafeAccessor]` (.NET 8+, AOT-safe). For fields that is `UnsafeAccessorKind.Field`; for properties with a non-public setter it is `UnsafeAccessorKind.Method` with name `set_X`. Generated code in another assembly needs a public or `InternalsVisibleTo`-visible type; otherwise the generator reports a diagnostic.

### 6.4 Example generated formatter
Source:

```csharp
[NexusObject]
public partial class Person
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Name { get; set; }
    [NexusKey(3)] public List<string>? Tags { get; set; }
    [NexusKey(4)] public double[]? Scores { get; set; }
    [NexusIgnore] public object? Cache { get; set; }
}
```

Generated code (abridged):

```csharp
file sealed class Person_NexusFormatter : global::NexNet.Serialization.NexusFormatter<global::Ns.Person?>
{
    public static readonly Person_NexusFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, global::Ns.Person? value)
    {
        if (value is null) { writer.WriteNil(); return; }
        writer.WriteArrayHeader(5);
        writer.Write(value.Id);
        writer.Write(value.Name);
        writer.WriteNil();                                                   // key 2 unused
        ListFormatter_String.Instance.Serialize(ref writer, value.Tags);    // generated closed generic
        PrimitiveArrayFormatter<double>.Instance.Serialize(ref writer, value.Scores);
    }

    public override void Deserialize(ref MsgPackReader reader, ref global::Ns.Person? value)
    {
        if (reader.TryReadNil()) { value = null; return; }
        var count = reader.ReadArrayHeader();
        reader.Enter();

        int id = default; string? name = default; List<string>? tags = default; double[]? scores = default;
        for (var i = 0; i < count; i++)
        {
            switch (i)
            {
                case 0: id = reader.ReadInt32(); break;
                case 1: name = reader.ReadString(); break;
                case 3: ListFormatter_String.Instance.Deserialize(ref reader, ref tags); break;
                case 4: PrimitiveArrayFormatter<double>.Instance.Deserialize(ref reader, ref scores); break;
                default: reader.Skip(); break;
            }
        }

        value ??= new global::Ns.Person();
        value.Id = id; value.Name = name; value.Tags = tags; value.Scores = scores;
        reader.Exit();
    }
}
```

Primitives and strings call the writer and reader methods directly, with no formatter object. Generated code always uses fully qualified `global::` names. The formatter itself is declared `internal` (not `file`) when another generated file needs to reference it, for example the module-initializer registry.

### 6.5 Unions
`[NexusUnion<TSub>(tag)]` goes on an abstract class or interface that is also `[NexusObject]`. A union value is written as **array(2): `[tag (uint16), payload]`**, and nil for null. This matches MessagePack-CSharp's union layout.

- **Serialize** is a generated `switch` on the runtime type. More-derived types come first: the generator sorts cases by inheritance depth, descending. A type not covered by any case throws `UnknownUnionType`.
- **Deserialize** reads the header, requires 2 elements, reads the tag, and calls the matching subtype formatter. An unknown tag throws `UnknownUnionTag`. Strictness is right for RPC: an unknown tag means either a schema mismatch (which `TypeHasher` should already have caught) or a hostile peer.

### 6.6 TypeHasher
`TypeHasher` gets a walk mode taken from the backend:
- **MessagePack mode:** a user type is walked if it is `[NexusObject]`. Its members are ordered by `NexusKey`, and the **key number is hashed** along with each member's type name and nullability, because the key decides the position on the wire. Union cases are hashed as `(tag, subtype)`. A type covered by `[NexusFormatter<TF, T>]` hashes `T`'s name together with the formatter's fully qualified name. Built-in types keep hashing by name only, as `[CLR]` types do today.
- **MemoryPack mode:** today's behavior, unchanged.

The walk-string output (`generateWalkString`) gets an `[NexusObject]` marker. The existing determinism regression test (#73) is extended to cover both modes.

### 6.7 Generator structure
New or changed generator files:

| File | Role |
|---|---|
| `Extraction/SerializableTypeExtractor.cs` | Uses `ForAttributeWithMetadataName("NexNet.Serialization.NexusObjectAttribute")` for types declared in the compilation. It also collects the closure of types reachable from nexus methods, collections and channel parameters, plus `[assembly: NexusSerializable<T>]` and `[assembly: NexusFormatter<TF,T>]` declarations. |
| `Models/SerializableTypeData.cs` | Equatable records for incremental caching: members (name, key, type FQN, accessor kind, init-only, required, needs `UnsafeAccessor`), construction strategy, union cases, generic parameters. |
| `Validation/SerializableTypeValidator.cs` | The diagnostics in §6.9. |
| `Emission/FormatterEmitter.cs` | Object, struct, union, enum and generic-type formatters. |
| `Emission/RegistryEmitter.cs` | One `[ModuleInitializer]` per assembly that registers every generated formatter and every closed generic built-in instance it needs. |
| `Emission/MethodEmitter.cs` | Branches on the backend for argument, return and result code (§6.8). |
| `Analyzers/ChannelTypeAnalyzer.cs` | A `DiagnosticAnalyzer` (separate from the generator) for channel call sites (§7.3). |
| `TypeHasher.cs` | The walk mode (§6.6). |

**Generic user types:** `[NexusObject] class Envelope<T>` produces `Envelope_NexusFormatter<T> : NexusFormatter<Envelope<T>>`. It takes a `NexusFormatter<T>` in its constructor (a virtual call per `T` member). Each closed type found, such as `Envelope<Person>`, is registered by the module initializer as `new Envelope_NexusFormatter<Person>(Person_NexusFormatter.Instance)`.

**Types from other assemblies:** when a reachable `[NexusObject]` type comes from a referenced assembly that did not generate formatters (for example, a DTO project without NexNet's generator), the consuming assembly generates them, as long as the members are accessible. Registration is first-wins, so duplicates are harmless.

### 6.8 Arguments, return values and results
**Proxy side** (replaces the `ValueTuple` plus `MemoryPackSerializer.Serialize` at `MethodEmitter.cs:250-270`). For `ValueTask<int> Add(int a, Person p, CancellationToken ct)`:

```csharp
var __buffer = __proxyInvoker.RentArgumentBuffer();       // pooled PooledArrayBufferWriter
var __writer = new global::NexNet.Serialization.MsgPackWriter(__buffer);
__writer.WriteArrayHeader(2);
__writer.Write(a);
global::Ns.Person_NexusFormatter.Instance.Serialize(ref __writer, p);
__writer.Flush();
return __proxyInvoker.ProxyInvokeAndWaitForResultCore(
    MethodId, __buffer, global::NexNet.Serialization.Int32Formatter.Instance, ct);
```

`IProxyInvoker` methods take the pooled `PooledArrayBufferWriter` in place of `Memory<byte>`, and return it to the pool after the message has been written to every target session. The broadcast paths in `ProxyInvocationBase` (All, Group, …) return it once, after the last send. This removes today's `byte[]` allocation per call. Pipe and channel parameters are still sent as their initial ID byte (`ProxyGetDuplexPipeInitialId`), written as a compact integer.

The logging line stops reading `__proxyInvocationArguments.ItemN` and logs the parameter variables directly.

**Server side** (replaces `DeserializeArguments<ValueTuple<...>>` at `MethodEmitter.cs:43-54`):

```csharp
var __reader = new global::NexNet.Serialization.MsgPackReader(message.Arguments, this.SessionContext.SerializerOptions);
if (__reader.ReadArrayHeader() != 2)
    throw global::NexNet.Serialization.NexusSerializationException.ArgumentCountMismatch(MethodId, 2);
var a = __reader.ReadInt32();
global::Ns.Person? p = default;
global::Ns.Person_NexusFormatter.Instance.Deserialize(ref __reader, ref p);
```

The argument count must match exactly. Invocation authorization still runs before deserialization, as it does today.

**Return value** (replaces `MethodEmitter.cs:90`):

```csharp
if (returnBuffer != null)
{
    var __rw = new global::NexNet.Serialization.MsgPackWriter(returnBuffer);
    __rw.Write(result);           // or Formatter.Instance.Serialize(ref __rw, result)
    __rw.Flush();
}
```

**Result on the proxy:** `InvocationResultMessage.TryGetResult<T>()` becomes `TryGetResult<T>(NexusFormatter<T> formatter, NexusSerializerOptions options, out T? result)`. The generated proxy passes the formatter instance, so no registry lookup is needed. Under the MemoryPack backend the current code stays behind `#if NEXNET_MEMORYPACK`.

### 6.9 New diagnostics (NEXNET028 onward)

| ID | Severity | Condition |
|---|---|---|
| NEXNET028 | Error | A type used by a nexus method, collection or channel parameter has no formatter: it is not built-in, not `[NexusObject]`, and not covered by `[NexusFormatter]`. |
| NEXNET029 | Error | Duplicate `[NexusKey]` value in a type. |
| NEXNET030 | Error | A public instance member of a `[NexusObject]` type has neither `[NexusKey]` nor `[NexusIgnore]`. |
| NEXNET031 | Error | A keyed member cannot be assigned: no setter, no `init`, no matching constructor parameter, and `UnsafeAccessor` not applicable. |
| NEXNET032 | Error | No usable constructor, or more than one `[NexusConstructor]`. |
| NEXNET033 | Error | Duplicate union tag, or a union subtype that doesn't derive from or implement the base. |
| NEXNET034 | Error | `[NexusUnion]` on a type that is neither abstract nor an interface, or that lacks `[NexusObject]`. |
| NEXNET035 | Error | A `[NexusFormatter<TF,T>]` whose formatter type is invalid (no public parameterless constructor, or wrong `T`). |
| NEXNET036 | Error | A `[NexusObject]` type that generated code cannot access (private nested, or internal across assemblies without `InternalsVisibleTo`). |
| NEXNET037 | Warning | A key gap above 16 (wastes nil bytes on the wire). |
| NEXNET038 | Error | A channel or pipe call site whose `T` has no formatter (from the analyzer, §7.3). |

Each diagnostic gets a test in `NexNet.Generator.Tests`, and an entry in `llm-usage.md` and the DocFX diagnostics page.

**Exit criteria for Phase 3:** generator tests pass, including tests that compile the emitted formatters; the integration suite passes in **both** builds.

---

## 7. Phase 4: Channels and pipes

### 7.1 Channel writer
`NexusChannelWriter<T>` looks up `NexusFormatterRegistry.Get<T>()` **once, in its constructor**, so a missing formatter fails immediately at channel creation rather than at the first write. `Write`/`WriteEnumerable` (`NexusChannelWriter.cs:102-116`) become:

```csharp
var writer = new MsgPackWriter(nexusPipeWriter);
foreach (var item in items)
    _formatter.Serialize(ref writer, item);
writer.Flush();
```

### 7.2 Channel reader: detect incomplete items without exceptions
Today's `NexusChannelReader.Read` (`NexusChannelReader.cs:~85-125`) calls `ReadValue<T>()` and catches the exception when an item is incomplete. Exceptions are expensive, and this one fires every time a frame boundary splits an item. The new loop **probes with `TrySkip` first** and only deserializes once an item is known to be complete:

```csharp
var reader = new MsgPackReader(buffer, _options);
long consumed = 0;
while (true)
{
    var probe = reader;                         // snapshot (struct copy)
    if (!probe.TrySkip())
        break;                                  // incomplete item: wait for more data

    T item = default!;
    _formatter.Deserialize(ref reader, ref item);
    if (reader.Consumed != probe.Consumed)      // formatter must consume exactly one value
        throw NexusSerializationException.InvalidMessage(MessageType.DuplexPipeWrite);

    list.Add(converter is null ? Unsafe.As<T, TTo>(ref item) : converter(item));
    consumed = reader.Consumed;
}

// Mark everything examined so the pipe waits for more bytes instead of spinning.
pipeReader.AdvanceTo(consumed, buffer.Length);

if (buffer.Length - consumed > _options.MaxBufferedItemSize)
    throw NexusSerializationException.LengthExceedsRemaining(...); // a single item is too large
```

Each byte is read twice: once by the cheap header scan in `TrySkip`, and once by the decode. MessagePack-CSharp v4 makes the same trade for its async reads. If benchmarks show the double pass hurting, the fallback is a 4-byte length prefix per item. That costs 4 bytes per item but needs no probing. This is listed under §11.

Check `NexusPipeReader.AdvanceTo`'s `examined` semantics. The current code passes `examined = consumed` when an item is incomplete; the intent is "wait for more data".

### 7.3 Analyzer for runtime-only `T`
`ChannelTypeAnalyzer` registers on `InvocationExpression`. It handles `INexusClient.CreateChannel<T>`, `ISessionContext.CreateChannel<T>`, and `NexusDuplexPipeExtensions.GetChannel<T>`/`GetChannelReader<T>`/`GetChannelWriter<T>`. For each call with a concrete `T` it checks, in order:
1. built-in;
2. `[NexusObject]`;
3. covered by `[assembly: NexusFormatter<_, T>]`;
4. for closed BCL generics, declared with `[assembly: NexusSerializable<T>]`, or its element types are all formattable and the closed type appears in a nexus signature.

If none apply, it reports NEXNET038. When `T` is an open generic parameter, nothing is reported; the runtime `MissingFormatter` exception covers that case.

### 7.4 Removing unmanaged channels (breaking change)
Delete:
- `Pipes/INexusDuplexUnmanagedChannel.cs`
- `Pipes/NexusDuplexUnmanagedChannel.cs`
- `Pipes/NexusChannelReaderUnmanaged.cs`
- `Pipes/NexusChannelWriterUnmanaged.cs`

Remove these APIs:
- `INexusClient.CreateUnmanagedChannel<T>` and its implementation in `NexusClient`
- `ISessionContext.CreateUnmanagedChannel<T>` and its implementation in `SessionContext`
- the `GetUnmanagedChannel*` extensions in `NexusDuplexPipeExtensions`
- the unmanaged branches in `NexusChannelReader`/`Writer`

In the generator, remove `IsDuplexUnmanagedChannel` from `MethodParameterData`, `NexusDataExtractor` and `MethodEmitter`. Add a diagnostic if an interface still declares `INexusDuplexUnmanagedChannel<T>`; it will be an unknown type anyway.

Update tests and samples:
- Delete `NexusChannelReaderUnmanagedTests`, `NexusChannelReaderWriterUnmanagedTests` and `NexusChannelWriterUnmanagedTests`.
- Change the unmanaged cases in `*_ChanneReaderIAsyncEnumerable` and `GeneratorChannelTests` to `INexusDuplexChannel<T>`.
- Update `NexNetDemo` channel samples, the ASP samples, and the benchmarks in `InvocationBenchmarks.cs`/`Nexuses.cs`.

**Migration note for users:** for bulk numeric streams, use `INexusDuplexChannel<T[]>` and write batches. Each batch is sent as one ext 78 block (§5.5), which is about as fast as the old raw copy.

---

## 8. Phase 5: Options and security

```csharp
public enum SerializerSecurity { Untrusted = 0, Trusted = 1 }

public sealed record NexusSerializerOptions
{
    public static readonly NexusSerializerOptions Untrusted = new();
    public static readonly NexusSerializerOptions Trusted = new() { Security = SerializerSecurity.Trusted, StrictUtf8 = false };

    public SerializerSecurity Security { get; init; } = SerializerSecurity.Untrusted;
    public int MaxDepth { get; init; } = 64;
    public bool StrictUtf8 { get; init; } = true;
    public int MaxBufferedItemSize { get; init; } = 16 * 1024 * 1024;   // channel item cap
    public bool HashFloodingResistantCollections => Security == SerializerSecurity.Untrusted;
}
```

- `ConfigBase` gets `SerializerOptions` (default `Untrusted`). The session passes it to `PoolManager` (for message pools), to channel readers, to collection handlers, and to generated invokers through `SessionContext.SerializerOptions`.
- These checks are **always on**: length checks before allocation, the non-recursive skip, rejecting `0xc1`, exact argument and message counts, and rejecting trailing bytes. Each costs a comparison.
- **Untrusted only:** strict UTF-8, the randomized comparer for dictionaries and hash sets with non-string keys (`RandomizedEqualityComparer<T>` built on `HashCode.Combine(value)`), and the depth limit. Trusted mode uses `MaxDepth = int.MaxValue`.
- Benchmarks measure both settings (§11).

---

## 9. Phase 6: Collections

- `NexusListServer`, `NexusListClient`, `NexusListRelay` and `NexusListRelayServer` (the `MemoryPackSerializer.Serialize(item)` call sites listed in the research) write the item into a pooled `PooledArrayBufferWriter` using `NexusFormatterCache<T>`, which the generator fills because `T` appears in the nexus interface. The resulting `Memory<byte>` becomes `message.Value`. `NexusListClient.cs:87` (`Deserialize<T[]>`) reads through the registry; primitive `T` gives ext 78 automatically.
- `NexusCollectionValueMessage.DeserializeValue<TValue>()` takes the formatter and options.
- The collection message union `INexusCollectionListMessage` gets a **hand-written** internal formatter that keeps tags 0–8. Each case body is `[flags, ...fields]`, and value fields are embedded values (§4.3), not `bin`. The broadcast pipes (`NexusBroadcastServer`/`Client`/`Session`) already use `NexusChannelReader/Writer<TUnion>`. They pick up the channel changes from §7, with the union formatter registered by NexNet's module initializer.
- Remove `[MemoryPackable]`, `[MemoryPackUnion]`, `[MemoryPackOrder]` and `[MemoryPoolFormatter]` from `NexusCollectionListMessages.cs`, `NexusCollectionMessage.cs` and `NexusCollectionValueMessage.cs`. Replace `OnDeserializedCore` with the pooled-buffer flag set by the hand-written deserializer.

**Exit criteria:** the collection test suites (`Collections/**`, including `FuzzTests` and the relay tests) pass in both builds.

---

## 10. Testing

### 10.1 `NexNet.Serialization.Tests` (new)
References `NexNet` (through `InternalsVisibleTo`), **`MessagePack` 3.1.x as a test-only oracle**, NUnit, and CsCheck for property tests.

- **Golden vectors:** for each writer method and boundary value (0, 127, 128, 255, 256, 65,535, 65,536, `int.MinValue`, −32, −33, …), the exact bytes. Each must equal MessagePack-CSharp's output for the same value. This verifies canonical encoding, including the unsigned-family rule for positive values.
- **Reading non-canonical input:** feed every integer form (for example, 5 encoded as `d3 00..05`) and check that each decodes correctly.
- **Two-way cross-checks:** NexNet writes, MessagePack-CSharp reads (`MessagePackSerializer.Deserialize<object>` or typed equivalents with `[MessagePackObject]` mirror DTOs in the test project), and the reverse. Ext 78 uses a small test-only MessagePack-CSharp formatter that understands the kind byte.
- **Property tests:** random values round-trip for every built-in type. Random byte strings never crash `TrySkip` and never make it consume more than the input. Generated test DTOs round-trip.
- **Limit tests:** depth exceeded, an array header claiming more elements than the remaining bytes, strict UTF-8, union tags, trailing bytes, argument count mismatch.
- **Big-endian simulation:** big-endian hosts can't run in CI, so the ext codec's swap path is exercised by calling its internal `SwapInPlace` directly and comparing with known vectors.

### 10.2 StreamStruct extensions (in the StreamStruct repo)
Requirements for the StreamStruct maintainers (us):

1. **Explicit-endian integer types:** `ushort_le`, `ushort_be`, `uint_le`, `uint_be` and so on. Pin down the byte order of the existing `ushort`/`uint` and document it.
2. **MessagePack field types.** Writing produces canonical encoding; reading accepts every valid form:
   - `mp_nil`, `mp_bool`, `mp_int` (signed, any width), `mp_uint`, `mp_f32`, `mp_f64`, `mp_str`, `mp_bin`
   - `mp_array` and `mp_map` (read the count into a field usable as a repeat count)
   - `mp_ext:<code>` (type code plus data)
   - `mp_any` (skip one value and capture its raw bytes)
3. **Forced-encoding variants** for negative and exact-byte tests, for example `mp_uint16!` (always `cd`) and `mp_int32!` (always `d2`).
4. **A length-prefixed group:** a way to describe a body as nested fields and have StreamStruct compute the `ushort_le` length prefix, so tests can write the whole frame declaratively. Raw byte fields stay available for malformed-frame tests.

`RawTcpClient` is rewritten to use these:
- `SendClientGreetingMessage` describes the greeting body with `mp_*` fields.
- `AssertReceiveMessageAsync<TMessage>` reads the frame and decodes the body with NexNet's internal message `Deserialize`. This is acceptable here because Phase 1's golden vectors and cross-checks already validate the messages independently.
- `SendMessageAsync` serializes through `IMessageBase.Serialize`.

Integration tests that build `InvocationMessage.Arguments` by hand (`NexusClientTests_SendInvocation.cs:39-184`, `:208`) switch to a test helper that encodes arguments with `MsgPackWriter`.

### 10.3 Integration and generator tests
- CI runs the integration suite **twice**: `-p:NexNetSerializer=MessagePack` and `-p:NexNetSerializer=MemoryPack`.
- Test DTOs (`Pipes/ComplexMessage.cs`, `Security/*`, `TestInterfaces/*`, the samples) carry **both** attribute sets for the length of the experiment.
- `TypeHasherTests`/`TypeHasherV2Tests`/`VersioningTests` run in both walk modes. `HashLock` values are separate for each mode during the experiment, and the MessagePack values become the only ones after Phase 9.
- The AOT sample projects (CI #74) are published with the MessagePack backend. The goal is **zero** trim/AOT warnings; the upstream MemoryPack warnings go away.

### 10.4 `NexNet.Fuzz` (new)
SharpFuzz harnesses, each taking raw bytes:
1. `MsgPackReader.TrySkip` plus a generic value walk.
2. Each protocol message's `Deserialize` run through `MessagePool` (strict count and trailing-byte checks).
3. The session receive loop (`ProcessMessages`) over an in-memory pipe, starting after a valid protocol header.
4. The channel reader over random chunk boundaries.
5. Generated formatters for a representative DTO graph (union, generics, collections).

Run in CI nightly (libFuzzer or AFL on Linux) with a checked-in corpus seeded from the golden vectors. Every crash becomes a regression test in `NexNet.Serialization.Tests`.

---

## 11. Phase 7: Benchmarks and the go/no-go gate

### 11.1 What is compared
| Build | Description |
|---|---|
| `master` | The current release (baseline) |
| Branch, MemoryPack | New framing and protocol messages; MemoryPack payloads |
| Branch, MessagePack (compact) | The candidate |
| Branch, MessagePack (fixed-width) | Measures the cost of compact integers |
| Untrusted vs Trusted | Measures the cost of the hardening |

### 11.2 Benchmarks (NexNetBenchmarks)
- **Existing:** `InvocationBenchmarks` and `MessagePoolBenchmarks`, run per build.
- **New `SerializerBenchmarks`:** primitives, strings (ASCII and multilingual, short and long), a small POCO (5 members), a nested POCO graph, `int[]`/`double[]` of 16, 1K and 64K elements (ext 78 vs MemoryPack), `List<Person>` with 100 items, and a union. Serialize and deserialize separately. MemoryPack runs as the reference in the same process.
- **New `ChannelThroughputBenchmarks`:** items per second for `INexusDuplexChannel<Person>`, `INexusDuplexChannel<int[]>` and `INexusDuplexChannel<int>`. Includes a **fragmented** case where pipe chunks are forced small, so `TrySkip` probing and incomplete-item handling actually run.
- **New `CollectionBenchmarks`:** list insert and replace broadcast fan-out to 10 clients.
- **Record:** mean, allocated bytes (`MemoryDiagnoser`), and **bytes on the wire** per operation.

### 11.3 Proposed gate (adjust before running)
The change proceeds to Phase 9 if all of these hold:
1. Invocation round-trip (`InvocationBenchmarks`) is within **5%** of branch-MemoryPack, with allocations no higher.
2. POCO serialize plus deserialize is within **25%** of MemoryPack. This is the expected cost of the MessagePack format.
3. Primitive-array benchmarks (ext 78) are within **15%** of MemoryPack.
4. Channel throughput is within **15%**, and the fragmented case is **better** than branch-MemoryPack.
5. Fuzzing (§10.4) has been clean for 24 hours.

If the gate fails, write up the findings with the numbers. The serializer-independent work (Phases 0 and 1, the unmanaged-channel removal and the security options) can still ship on its own.

**Follow-ups if a gate item is close:** an "assume ASCII first" string-write variant, a fast path in the reader for contiguous spans that bypasses `SequenceReader`, and length-prefixed channel items instead of `TrySkip` probing (§7.2).

---

## 12. Phase 8: Remove MemoryPack (after a go decision)

- Delete the backend switch: the `NexNetSerializer` property, `NEXNET_MEMORYPACK`, the generator's backend branch, `TypeHasher`'s MemoryPack mode, and the `PayloadFormat.MemoryPack` value (keep code `1` reserved in the spec).
- Remove the `MemoryPack` package references from all projects, and the MemoryPack attributes from tests and samples.
- Remove the `[MemoryPackable]` references in docs (`NexusDuplexPipeExtensions.cs:80`, `:97` XML docs, and others).

## 13. Phase 9: Documentation and migration

- `llm-usage.md` and `llm-dev.md`: replace "MemoryPack serialization" with NexNet's MessagePack, add the attributes, the formatter extension point, the security options and the new diagnostics, and remove unmanaged channels.
- DocFX: a serialization guide, `wire-protocol.md`, and a migration guide covering:
  - attribute mapping: `[MemoryPackable]` → `[NexusObject]`, `[MemoryPackOrder(n)]` → `[NexusKey(n)]`, `[MemoryPackIgnore]` → `[NexusIgnore]`, `[MemoryPackUnion(tag, typeof(T))]` → `[NexusUnion<T>(tag)]`, `[MemoryPackConstructor]` → `[NexusConstructor]`;
  - the unmanaged-channel removal and its replacement;
  - protocol v2 (old and new peers refuse each other);
  - new `HashLock` values for versioned interfaces.
- `THIRD-PARTY-NOTICES.md`: the MessagePack-CSharp MIT notice, if any v4 code is copied (§15).

---

## 14. Open questions

1. **Can DTO projects skip referencing NexNet?** Add an attributes-only shared project that DTO assemblies import as `internal` (§2.2)?
2. **The gate thresholds** in §11.3 need agreement before benchmarks run.
3. **`DateTimeOffset`/`TimeOnly`/`DateOnly`/`Half`/`BigInteger`** representations (§5.7) are proposals; confirm or adjust.
4. **Ext type code 78** for primitive arrays: confirm, and reserve a range (for example 78–79) for future NexNet ext types in the spec.
5. **Do the `IProxyInvoker` signature changes** (§6.8) affect `NexNet.Asp` or any public surface? A scan shows `IProxyInvoker` is internal, but confirm.
6. **Running integration tests twice in CI** doubles CI time for the experiment. Acceptable, or nightly only?

---

## 15. Reusing MessagePack-CSharp v4 code

Allowed only after a line-by-line review, preferring safe APIs (`BinaryPrimitives`, `MemoryMarshal.AsBytes`/`Cast`, `Span.CopyTo`), and with the MIT notice kept. Candidates:
- the branch layout for integer writes (compare with §5.1);
- the string-header fill-in technique (§5.1);
- the element-budget idea for length checks (§5.2).

Do **not** copy:
- code that uses `Unsafe.Add`, `Unsafe.WriteUnaligned` or raw-pointer arithmetic in buffer handling (the area .NET runtime reviewers flagged in PR #2281);
- anything from the generated-code layer.

v4 is unreleased and still changing, so any copied code is pinned to a specific commit, recorded in a comment above the copied code.

---

## 16. Change inventory

| Area | Files |
|---|---|
| New shared project | `src/NexNet.Serialization/**` (§2.2) |
| Framing | `Internals/NexusSession.cs`, `NexusSession.Sending.cs`, `NexusSession.Receiving.cs`, `Internals/ReadingHelpers.cs`, `Pipes/NexusPipeWriter.cs`, `Pipes/NexusPipeManager.cs` |
| Messages | `Messages/*.cs` (every message, `IMessageBase`, `IInvocationMessage`), `Pools/MessagePool.cs`, `Pools/PoolManager.cs` |
| Invocation | `Invocation/IProxyInvoker.cs`, `ProxyInvocationBase.cs`, `NexusBase.cs`, `ServerNexusBase.cs`, `SessionInvocationStateManager.cs`, `SessionContext.cs`, `ISessionContext.cs` |
| Channels and pipes | `Pipes/NexusChannelReader.cs`, `NexusChannelWriter.cs`, `NexusDuplexPipeExtensions.cs`, the four unmanaged files (deleted), `INexusClient.cs`, `NexusClient.cs` |
| Broadcast | `Pipes/Broadcast/NexusBroadcastClient.cs` (the argument serialization at `:87`) |
| Collections | `Collections/NexusCollectionMessage.cs`, `NexusCollectionValueMessage.cs`, `INexusCollectionMessage.cs`, `Lists/NexusCollectionListMessages.cs`, `Lists/NexusListServer.cs`, `NexusListClient.cs`, `NexusListRelay.cs`, `NexusListRelayServer.cs`, `NexusListTransformers.cs` |
| Config | `Transports/ConfigBase.cs` (`SerializerOptions`) |
| Build | `src/NexNet.props`, `NexNet/NexNet.csproj`, generator package `build/*.props`, CI workflow (two-build matrix, fuzz job) |
| Generator | `TypeHasher.cs`, `Emission/MethodEmitter.cs`, `Extraction/NexusDataExtractor.cs`, `Models/MethodParameterData.cs`, `DiagnosticDescriptors.cs`, plus the new files in §6.7 |
| Tests | New `NexNet.Serialization.Tests`, `NexNet.Fuzz`; `IntegrationTests/Security/RawTcpClient.cs` and the security tests, `NexusClientTests_SendInvocation.cs`, `NexusServerTests_SendInvocation.cs`, channel tests, collection tests, test DTOs; `Generator.Tests` (hash, versioning, channel and new diagnostic tests) |
| Benchmarks | `NexNetBenchmarks/*` plus the new benchmark classes (§11.2) |
| Samples | `Samples/NexNetDemo/**`, `Samples/NexNetSample.Asp.*`, `Samples/NexNetSample.Aot.*` |
| Docs | `docs/wire-protocol.md`, DocFX serialization and migration pages, `llm-usage.md`, `llm-dev.md`, `THIRD-PARTY-NOTICES.md` |
| External | The StreamStruct repo (§10.2) |

## 17. Suggested order of work

1. Phase 0: framing and header v2, spec skeleton, StreamStruct endianness check.
2. Phase 2 core: writer, reader, `TrySkip`, golden vectors, cross-checks, first fuzz harness (§5.1–5.4).
3. Phase 1: protocol messages on the new core, StreamStruct MessagePack types, `RawTcpClient` rewrite.
4. Phase 2 remainder: built-in formatters, ext 78 codec (§5.5–5.7).
5. Phase 3: attributes, generator formatters, arguments, returns and results, `TypeHasher` mode, diagnostics.
6. Phase 4: channels with `TrySkip` probing, analyzer, unmanaged-channel removal.
7. Phase 5: options and security plumbing.
8. Phase 6: collections.
9. Phase 7: benchmarks, then the gate decision.
10. Phases 8 and 9: remove MemoryPack, documentation and migration guide.
