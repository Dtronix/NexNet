# Golden vectors

Language-neutral test vectors for the NexNet wire format: MessagePack payload values, protocol message bodies and
frames. Every NexNet runtime (C#, Java, JavaScript, and later Rust and C++) replays them, so all runtimes read and
write the same bytes. This file is the **golden format contract** (format version 1). Changing it needs a contract
amendment card that updates every consumer.

The files are generated. Do not edit them by hand. Regenerate them with:

```
dotnet run --project src/NexNet.GoldenVectors -- --out tests/golden
```

The generator (`src/NexNet.GoldenVectors`) checks every vector against the C# implementation as it writes it. A
vector the C# code does not produce, accept or reject as stated stops the generation. The same command also rewrites
the fuzz seed corpus `src/NexNet.Fuzz/Corpus/golden.hex` (the bytes of every vector up to 4 KiB), so the vectors are
the corpus's only source; `--no-corpus` skips it and `--corpus <file>` writes it elsewhere. The output is
deterministic: no time, randomness, culture, time zone or path goes into it.

`GoldenReplayTests` in `src/NexNet.Serialization.Tests` replays every vector in C#. It also regenerates the files
into a temporary directory and fails if they differ from the checked-in files, so a stale vector fails the C# gate.

## Files

| File | Content |
|---|---|
| `primitives.jsonl` | nil, bool, every integer width at every MessagePack size boundary (including above 2^53 and above `Long.MAX_VALUE`), `char`, float32/float64/`Half` (including -0, NaN, infinities, the float32-vs-float64 choice), strings (fixstr to str32, non-ASCII, invalid UTF-8), bin, `DateTime` of every kind, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Guid` byte order, `decimal` flags, `BigInteger`, `Uri`, `Version` |
| `containers.jsonl` | arrays, lists, sets, queues, stacks, maps, tuples; array and map header boundaries; the `maxElements` limit |
| `ext78.jsonl` | ext 78 primitive arrays of every element kind and every ext header size |
| `objects.jsonl` | the interop type zoo objects, generics, the user-formatter type, enums; peers of other versions; the `maxDepth` limit |
| `unions.jsonl` | unions over an abstract class and an interface |
| `messages.jsonl` | protocol message bodies (protocol specification §4), including the `ClientGreeting` golden |
| `frames.jsonl` | the connection preamble and frames (§1, §2) |
| `reject.jsonl` | every `reject` vector: input a reader must refuse |
| `schemas.jsonl` | the declared types the vectors name: zoo objects, unions and enums, externals and message bodies |

Each `.jsonl` file holds one JSON object per line, UTF-8, `\n` line ends (a reader must also accept `\r\n`). Ids are
unique across all files.

## Vector lines

```json
{"id":"u64.18446744073709551615","type":"ulong","mode":"both","note":"above Long.MAX_VALUE","value":"18446744073709551615","hex":"cfffffffffffffffff"}
```

| Field | Presence | Meaning |
|---|---|---|
| `id` | always | Unique id: lower-case letters, digits, `.`, `-`, `_` |
| `type` | always | A type expression (below), `frame` or `preamble` |
| `mode` | always | `both`, `decode` or `reject` |
| `options` | optional | Serializer options for decoding (below) |
| `error` | `reject` only, optional | The kind of error (below) |
| `note` | optional | A human-readable description; never needed to replay the vector |
| `value` | `both` and `decode` | The value, in typed JSON (below) |
| `hex` | always | The bytes, lower-case hex; may be empty |

Readers must refuse a line with any other field, so a new field always comes with a format change.

### Modes

- **`both`**: `hex` is the canonical encoding of `value`. Encode `value` and compare the bytes with `hex`. Decode
  `hex` (with `options`) and compare the result with `value`.
- **`decode`**: `hex` is valid but not canonical: a non-minimal integer form, a str read as bin, an older or newer
  peer's object, invalid UTF-8 under lenient options… Decode `hex` and compare the result with `value`. `value` is
  canonical: encoding it gives the canonical bytes, which differ from `hex`.
- **`reject`**: decoding `hex` (with `options`) as `type` must fail.

"Decode" always means decoding **exactly one value from all of `hex`**: bytes left after the value fail the decode
(error kind `trailing`).

**NaN.** Float values are bit patterns, and the vectors keep NaN bits (`f32:0xFFC00000` is .NET's `float.NaN`,
`f32:0x7FC00000` Java's). A runtime whose native float cannot hold a NaN's sign and payload (a JavaScript `number`)
compares any NaN with any NaN, and may skip the encode comparison of a NaN value whose bits it cannot produce. Every
other value, -0 included, compares bit for bit.

### Options

`options` is an object with any of these members; a missing member keeps its default.

| Member | Default | Meaning (protocol specification §11, `NexusSerializerOptions`) |
|---|---|---|
| `security` | `"untrusted"` | `"untrusted"` or `"trusted"`. Trusted changes the defaults: no depth limit, lenient UTF-8 |
| `maxDepth` | 64 (untrusted) | Most nesting in one value. Each array or map of a sequence, set, map, tuple, `KeyValuePair`, object or union is one level; the pair of a `DateTimeOffset`, message bodies, ext 78 and bin are not. `maxDepth` levels decode, one more fails |
| `strictUtf8` | `true` (untrusted) | `false`: invalid UTF-8 in a str decodes with each maximal invalid subpart replaced by U+FFFD |
| `maxElements` | 0 (no limit) | Most array elements plus map keys and values, counted over the whole value |

### Error kinds

The `error` of a reject vector names why the input is refused. A runtime must reject the input; reporting the same
kind is recommended but optional, and `length` and `truncated` may be reported as each other.

| Kind | Meaning |
|---|---|
| `truncated` | the input ends inside the value |
| `type` | a MessagePack type that the expected type cannot be read from (nil for a non-nullable type, a float for an integer, a wrong ext type or element kind…) |
| `reserved` | the never-used code `0xc1` |
| `range` | an integer outside the target type (a negative value for an unsigned type, a union tag above 65535…) |
| `count` | an array with the wrong number of elements for a fixed shape (tuples, unions, `DateTimeOffset`, message bodies) |
| `size` | a wrong byte length for a fixed-size bin (`Guid`, `decimal`) or ext 78 data (no kind byte, a partial element) |
| `length` | a declared str/bin/ext length or array/map count larger than the remaining input |
| `elements` | more elements than `maxElements` |
| `depth` | more nesting than `maxDepth` |
| `utf8` | invalid UTF-8 with `strictUtf8` |
| `value` | a well-formed but invalid value: a date or time out of range, a `decimal` scale above 28 or a reserved bit, an unknown union tag, a malformed `Version` |
| `trailing` | bytes after the value |

## Types

`type` is a type expression. Spaces are not allowed.

| Expression | Wire form |
|---|---|
| `bool`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `half`, `char`, `decimal`, `string`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Guid`, `BigInteger`, `Uri`, `Version` | the built-in types (protocol specification §10.4, §10.6) |
| `T[]`, `List<T>`, `Memory<T>`, `ReadOnlyMemory<T>` | ext 78 when `T` is `sbyte short ushort int uint long ulong float double char half`; bin for `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>` (but `List<byte>` is an array); otherwise an array |
| `ReadOnlySequence<byte>`, `ArraySegment<byte>` | bin |
| `IList<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`, `ICollection<T>`, `IReadOnlyCollection<T>` | an array, even of primitives |
| `HashSet<T>`, `Queue<T>` (front first), `Stack<T>` (top first) | an array |
| `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` | a map |
| `(A,B,…)`, `Tuple<A,B,…>`, `ValueTuple<…>`, `KeyValuePair<K,V>` | an array of exactly that many elements |
| `T?` | nil or `T` (only meaningful for value types: a reference type always admits nil) |
| `Name`, `Name<A,B>` | a declared type of `schemas.jsonl` with its type arguments |
| `$0`, `$1`… | a type parameter of the enclosing declared type (`schemas.jsonl` only) |
| `raw` | one embedded MessagePack value carried as its bytes (message bodies) |

The rules are those of the generator IR (`docs/internals/nexnet-ir.md`). A sequence of a type parameter takes its
wire form once the parameter is bound: in `InteropPair<int,X>`, the member `List<$1>` is ext 78 when `$1` is `int`.

## Typed JSON

The value model, shared with the cross-language harness (`TypedJson` in `src/NexNet.Interop.Abstractions`):

| Value | JSON |
|---|---|
| nil (any type) | `null` |
| `bool` | `true` / `false` |
| integers, enums (their underlying integer) | a number when within ±(2^53 − 1), otherwise a decimal string |
| `char` | its UTF-16 code unit, a number 0–65535 |
| `float` / `double` / `half` | a bit-pattern string: `"f32:0x3FC00000"`, `"f64:0x…"`, `"f16:0x…"` (upper-case hex, 8/16/4 digits) |
| `string`, `Uri`, `Version` | a string |
| bin (`byte[]` and the other byte containers), `raw`, externals | base64 (RFC 4648, padded) of the bytes |
| arrays, sets, queues, stacks, ext 78 arrays, tuples | an array |
| maps | an array of `[key, value]` pairs, in wire order |
| objects and message bodies | an array by key or position, `null` in unused keys |
| unions | `[tag, value]` |
| `DateTime` | its `ToBinary` int64: bits 0–61 ticks, bits 62–63 the kind (§10.6) |
| `DateTimeOffset` | `[utcTicks, offsetMinutes]` |
| `TimeSpan`, `TimeOnly` | ticks |
| `DateOnly` | the day number |
| `decimal` | `[lo, mid, hi, flags]`, each a signed int32 |
| `Guid` | its lower-case `"D"` string, `00112233-4455-6677-8899-aabbccddeeff` |
| `BigInteger` | a decimal string |

Integers may also be given as strings and floats as plain numbers on input; the files always use the forms above.

A Local `DateTime` names an instant. Its value (and bytes) are the UTC ticks with the Local kind, whatever the time zone
of the machine that replays it.

## schemas.jsonl

One declared type per line. Member and field types are type expressions.

```json
{"id":"InteropItem","kind":"object","typeParams":0,"length":2,"members":[[0,"Id","int"],[1,"Name","string"]]}
{"id":"InteropShape","kind":"union","typeParams":0,"cases":[[0,"InteropCircle"],[1,"InteropRectangle"]]}
{"id":"InteropEnum","kind":"enum","underlying":"int"}
{"id":"InteropPoint","kind":"external","note":"User formatter InteropPointFormatter: [X: int, Y: int], two canonical integers."}
{"id":"ServerGreeting","kind":"message","messageType":105,"minCount":4,"maxCount":null,"fields":[["version","int"],["clientId","long"],["maxMessageSize","int"],["invocationCredits","int"],["idleTimeoutMs","uint"]]}
```

- **object**: a `[NexusObject]` class or struct, an array of `length` elements; `members` are `[key, name, type]` (§10.2).
- **union**: `[tag, value]`; `cases` are `[tag, case id]`; the cases share the union's type parameters (§10.3).
- **enum**: the underlying integer type.
- **external**: a type with a user formatter (D12). Its value is the base64 of its bytes; `note` describes its layout.
- **message**: a protocol message body (§4). `messageType` is its frame type, or `null` for a value embedded in another
  message (`RemoteErrorDetail`). A reader accepts `minCount` to `maxCount` elements; `maxCount: null` means it skips
  any elements after the known ones (`ServerGreeting`'s trailing fields). Missing trailing fields decode as 0.

Ids are the C# type names without namespace or generic arity. The zoo entries come from the generator's description of
the interop nexus types (`src/NexNet.TestNexus/Interop/InteropTypes.cs`), so they always match the generated code.

## Frames

`type: "preamble"`: the value is `[version]`, the bytes the 8-byte preamble `4e 6e 50 14 00 00 00 <version>` (§1).

`type: "frame"`: one complete frame (§2). The value is an array that starts with the message type:

| Frame | Value | Bytes |
|---|---|---|
| `Ping` (1), disconnects (20–38) | `[type]` | the type byte |
| a MessagePack body | `[type, body]`, `body` the typed JSON of the message whose `messageType` is `type` | type, body length (uint16 LE), body |
| `DuplexPipeWrite` (50) | `[50, clientPipeId, serverPipeId, "base64 data"]` | type, length, the two pipe id bytes, data |
| `PipeWindowUpdate` (51) | `[51, clientPipeId, serverPipeId, credit]` | type, length (4), the two pipe id bytes, credit (uint32 LE) |
| `MessageChunk` (114) | `[114, invocationId, kind, "base64 data"]` | type, length, invocation id (uint16 LE), kind, data |

The body length does not count the post-header. Frames are always `both`.

## Replaying in a new runtime

1. Read `schemas.jsonl`, then every vector file.
2. For each vector, map `type` to the runtime's codec: a built-in, a container of codecs, a generated formatter of a
   declared type, a message codec, the frame reader/writer. A runtime may also replay `both` and `decode` vectors
   through a schema-driven transcoder like `TypedJson` to test its reader and writer before any formatter is
   generated.
3. Apply the mode. Fail on any vector whose `type` the runtime cannot map, unless the runtime lists it as unsupported
   with a reason.
