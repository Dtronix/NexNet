# StreamStruct requirements for NexNet wire protocol v2

StreamStruct (https://github.com/DJGosnell/StreamStruct) was **not modified** during this experiment. These are the
features that would let NexNet's raw-protocol tests (`NexNet.IntegrationTests/Security/RawTcpClient.cs` and the
security tests) describe the v2 wire format declaratively, instead of building message bodies with NexNet's own
writer.

## What NexNet does today (workaround)

- Framing still uses existing StreamStruct field types: `[type:byte][body_length:ushort][body:body_length]`. This
  relies on StreamStruct's `ushort` being little-endian, which matches the v2 framing on little-endian hosts.
- Message bodies are produced and parsed with NexNet's hand-written message code (`IMessageBase.Serialize` /
  `Deserialize`) through `TestSerialization` and passed to StreamStruct as opaque `byte[]` fields. The bodies are
  validated independently by the golden vectors and the MessagePack-CSharp cross-checks in `NexNet.Serialization.Tests`.

## Requested features

### 1. Explicit-endian integers
- Add `ushort_le`, `ushort_be`, `short_le`, `short_be`, `uint_le`, `uint_be`, `int_le`, `int_be`, `ulong_le`,
  `ulong_be`, `long_le`, `long_be`.
- Document (and pin with tests) the byte order of the existing `ushort`/`uint`/... types. NexNet v2 framing is
  explicitly little-endian, so `[body_length:ushort_le]` would remove the host dependency from the tests.

### 2. MessagePack field types
On write they produce the canonical (smallest) encoding. On read they accept every valid encoding and range-check
into the target type.

| Field type | Write | Read accepts |
|---|---|---|
| `mp_nil` | `c0` | `c0` |
| `mp_bool` | `c2`/`c3` | `c2`/`c3` |
| `mp_int` | canonical signed (unsigned family for non-negative values) | all 8 integer forms + fixints |
| `mp_uint` | canonical unsigned | all integer forms (negative rejected) |
| `mp_f32` / `mp_f64` | `ca` / `cb` | `ca`, `cb`, integers |
| `mp_str` | UTF-8, smallest header | fixstr/str8/16/32, nil (→ null) |
| `mp_bin` | smallest bin header | bin8/16/32, nil |
| `mp_array` | header with the element count taken from the field value | fixarray/array16/32; the count is usable as a repeat count for following fields |
| `mp_map` | header with the pair count | fixmap/map16/32 |
| `mp_ext:<code>` | ext header + type code + data | fixext1–16, ext8/16/32 with a matching code |
| `mp_any` | raw bytes, verbatim | skips exactly one value and captures its raw bytes |

### 3. Forced-encoding variants
For exact-byte and negative tests, add variants that always use one form: `mp_uint8!` (`cc`), `mp_uint16!` (`cd`),
`mp_uint32!` (`ce`), `mp_uint64!` (`cf`), `mp_int8!` (`d0`), `mp_int16!` (`d1`), `mp_int32!` (`d2`), `mp_int64!`
(`d3`), and `mp_str8!` / `mp_str16!` / `mp_bin16!` for header widths.

### 4. Length-prefixed groups
A way to describe a body as nested fields and have StreamStruct compute the length prefix. For example:

```
[type:byte][body:ushort_le_prefixed{
    [hdr:mp_array][invocation_id:mp_uint][method_id:mp_uint][flags:mp_uint][args:mp_any]
}]
```

With this, tests could write the whole ClientGreeting / Invocation frame declaratively:

```
[type:byte][body:ushort_le_prefixed{[hdr:mp_array][version:mp_str][server_hash:mp_int][client_hash:mp_int][token:mp_bin]}]
```

Raw byte fields stay available inside groups so malformed frames can still be built.

### 5. Validation helpers
- `VerifyAsync` support for `mp_*` fields, comparing logical values rather than bytes.
- A strict mode that fails when a value was not canonically encoded. This would let tests assert NexNet's writer
  output on the wire.

## Example: the protocol header (v2)

Expressible today with existing types:

```
[magByt1:byte][magByt2:byte][magByt3:byte][magByt4:byte][payloadFormat:byte][reserved1:byte][reserved2:byte][version:byte]
```
