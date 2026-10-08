# Serialization

NexNet serializes method arguments, return values, channel items and synchronized collection values with its own [MessagePack](https://msgpack.org) serializer in the `NexNet.Serialization` namespace. It is part of the `NexNet` package; there is no external serializer dependency.

The source generator emits a formatter for each of your serializable types at compile time. At runtime formatters are looked up from a static registry, so serialization uses no reflection and no runtime code generation, and it works with trimming and Native AOT.

## Marking Types

Annotate your types with `[NexusObject]` and give every serialized member a key with `[NexusKey(n)]`:

```csharp
using NexNet.Serialization;

[NexusObject]
public class ChatMessage
{
    [NexusKey(0)] public string User { get; set; } = "";
    [NexusKey(1)] public string Text { get; set; } = "";
    [NexusKey(2)] public DateTimeOffset SentAt { get; set; }

    [NexusIgnore] public bool IsLocalEcho { get; set; }
}
```

| Attribute | Applies to | Purpose |
|-----------|------------|---------|
| `[NexusObject]` | Classes, structs, records, abstract classes, interfaces | Generates a formatter for the type |
| `[NexusKey(n)]` | Properties and fields | Position `n` (0 or greater) of the member on the wire |
| `[NexusIgnore]` | Properties and fields | Excludes the member |
| `[NexusConstructor]` | Constructors | Selects the constructor used when deserializing |
| `[NexusUnion<T>(tag)]` | Abstract classes and interfaces | Declares a union case `T` with a `ushort` tag |

The rules:

- Every public instance property and field of a `[NexusObject]` type must have either `[NexusKey]` or `[NexusIgnore]` (NEXNET030).
- Non-public members can carry `[NexusKey]` too; the generated formatter reaches them through generated accessors. This is not supported on generic types (NEXNET031).
- Members inherited from base classes are included. Keys must be unique across the whole type (NEXNET029).
- Keep keys dense. An object is written as an array of `highest key + 1` elements, and every unused key costs a nil byte. More than 16 unused positions produce warning NEXNET037.
- The type must be accessible to the generated code: `public`, or `internal` (with `InternalsVisibleTo` across assemblies) (NEXNET036).

Types that appear in a nexus method, a synchronized collection or a channel parameter are found automatically, including the types they contain (lists of your types, dictionaries, tuples and so on).

### Constructors, records and init-only members

The generator picks the constructor in this order:

1. the constructor marked `[NexusConstructor]`;
2. an accessible parameterless constructor (or the default constructor of a struct);
3. the only accessible constructor, if there is exactly one.

Otherwise it reports NEXNET032. Constructor parameters are matched to keyed members by name, ignoring case. Members that are not set by the constructor are assigned through setters, `init` accessors or `required` members. A keyed member that cannot be assigned at all is reported as NEXNET031.

Positional records work with `property:`-targeted keys:

```csharp
[NexusObject]
public record Point([property: NexusKey(0)] int X, [property: NexusKey(1)] int Y);
```

### Unions

Mark an abstract class or interface with `[NexusObject]` and one `[NexusUnion<T>(tag)]` per case. A union value is written as `[tag, value]`.

```csharp
[NexusObject]
[NexusUnion<Circle>(0)]
[NexusUnion<Rectangle>(1)]
public abstract class Shape { }

[NexusObject]
public class Circle : Shape { [NexusKey(0)] public double Radius { get; set; } }

[NexusObject]
public class Rectangle : Shape
{
    [NexusKey(0)] public double Width { get; set; }
    [NexusKey(1)] public double Height { get; set; }
}
```

Tags must be unique and every case type must derive from the union type (NEXNET033). An abstract or interface `[NexusObject]` type without any cases is reported as NEXNET034. Reading an unknown tag fails.

## Evolving Types

Readers accept arrays that are shorter or longer than the keys they know: elements beyond the reader's highest key are skipped, and members missing from a shorter array keep their default values. This gives a simple compatibility rule:

- **Add** members with new, previously unused keys.
- **Never reuse or renumber** a key, and do not change the type of a keyed member.
- To retire a member, remove it and leave its key unused.

The structure of `[NexusObject]` types used by method parameters, return values (`ValueTask<T>`) and `[NexusCollection]` items is also part of the nexus hash that peers compare when they connect, and of the `HashLock` of versioned interfaces: keys, member types, class vs struct, union tags and enum values. Names are not: renaming a type, a member or an enum member keeps the hash. See [Versioning](versioning.md#how-serialized-types-are-hashed) for the full rules and what they mean for released versions.

## Built-in Types

These types are supported without any attribute:

| .NET type | Encoding |
|-----------|----------|
| `bool` | bool |
| `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char` | integer, smallest form |
| `float`, `double`, `Half` | float32 / float64 (`Half` as float32) |
| `string` | UTF-8 string, nil for null |
| `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>` | binary |
| Arrays, `List<T>`, `Memory<T>` and `ReadOnlyMemory<T>` of `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `char`, `Half` | one compact block of little-endian values (MessagePack extension type 78) |
| Other `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`, `HashSet<T>`, `Queue<T>`, `Stack<T>` | array |
| `Dictionary<TKey, TValue>`, `IDictionary<TKey, TValue>`, `IReadOnlyDictionary<TKey, TValue>` | map |
| `Nullable<T>` | nil or `T` |
| Enums | underlying integer |
| `DateTime` | 64-bit integer that keeps `Kind` |
| `DateTimeOffset` | `[UTC ticks, offset minutes]` |
| `TimeSpan`, `TimeOnly` | ticks |
| `DateOnly` | day number |
| `Guid`, `decimal`, `BigInteger` | binary |
| `Uri`, `Version` | string |
| `ValueTuple<...>` and `Tuple<...>` (1 to 7 elements), `KeyValuePair<TKey, TValue>` | array |

The exact byte layouts are in the [wire protocol specification](../internals/protocol-specification.md#5-payload-encoding). A type that is neither built in, `[NexusObject]`, nor covered by a custom formatter is reported as NEXNET028.

## Types Used Only at Runtime

Some types never appear in a nexus signature, for example an item type used only with `CreateChannel<T>()` or `GetChannelReader<T>()`. Non-generic `[NexusObject]` types are always registered. For anything else, such as a closed generic, declare it at assembly level so the generator emits its formatter:

```csharp
[assembly: NexusSerializable<List<ChatMessage>>]
[assembly: NexusSerializable<Dictionary<string, ChatMessage>>]
```

The analyzer reports NEXNET038 when one of the channel methods is called with a type that has no formatter. If a formatter is still missing at runtime, the lookup throws a `NexusSerializationException` that names the type.

## Custom Formatters

For a type you cannot annotate, such as one from a third-party library, write a formatter and register it at assembly level. A formatter derives from `NexusFormatter<T>`, must have a public parameterless constructor, and writes and reads exactly one MessagePack value:

```csharp
using NexNet.Serialization;

[assembly: NexusFormatter<GeoPointFormatter, Vendor.GeoPoint>]

public sealed class GeoPointFormatter : NexusFormatter<Vendor.GeoPoint>
{
    public override void Serialize(ref MsgPackWriter writer, Vendor.GeoPoint? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(2);
        writer.Write(value.Latitude);
        writer.Write(value.Longitude);
    }

    public override void Deserialize(ref MsgPackReader reader, ref Vendor.GeoPoint? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        reader.ReadArrayHeader(2); // Throws unless the array has exactly two elements.
        var latitude = reader.ReadDouble();
        var longitude = reader.ReadDouble();
        value = new Vendor.GeoPoint(latitude, longitude);
    }
}
```

The registered type can then be used in nexus methods, as a member of `[NexusObject]` types and in channels. Formatters must be stateless and thread-safe. Throw `NexusSerializationException` for invalid input.

To serialize a value outside of a session, use `NexusSerializer.Serialize<T>(value)` and `NexusSerializer.Deserialize<T>(bytes, options)`.

## Options and Limits

`ConfigBase.SerializerOptions`, available on every client and server configuration, controls how data received from the peer is read:

| Option | `NexusSerializerOptions.Untrusted` (default) | `NexusSerializerOptions.Trusted` |
|--------|------------------------------|----------------------------|
| `MaxDepth` (container nesting) | 64 | unlimited |
| `StrictUtf8` | invalid UTF-8 is rejected | invalid UTF-8 is replaced |
| Dictionaries and sets with non-string keys | randomized hashing (hash flooding resistant) | default comparers |
| `MaxBufferedItemSize` (one incomplete channel item) | 16 MiB | 16 MiB |

Keep the default for any peer you do not fully control. Individual values can be changed with a `with` expression:

```csharp
config.SerializerOptions = NexusSerializerOptions.Untrusted with
{
    MaxBufferedItemSize = 64 * 1024 * 1024
};
```

Independent of these options, the reader always checks lengths against the received data before allocating, skips values without recursion, and requires every protocol message to have its exact shape with no trailing bytes. A malformed protocol message disconnects the session with a protocol error.

Serialized method arguments are limited to 65,526 bytes per invocation. Use [Duplex Pipes](duplex-pipes.md) or [Channels](channels.md) for larger data.

## Native AOT and Trimming

- All formatters are generated at compile time into one `NexNet.Formatters.g.cs` file per assembly and registered by a module initializer. Each type gets exactly one formatter per assembly.
- Private members are reached through generated `UnsafeAccessor` methods instead of reflection.
- The NexNet packages are marked AOT compatible (`IsAotCompatible`), and CI publishes a Native AOT sample and fails on any trimming or AOT warning.
- Types created only at runtime must be declared with `[assembly: NexusSerializable<T>]`. The generator cannot discover them, and nothing is generated at runtime.

## Diagnostics

| ID | Severity | Description |
|----|----------|-------------|
| NEXNET028 | Error | A type used by a nexus has no formatter |
| NEXNET029 | Error | Duplicate or negative `[NexusKey]` |
| NEXNET030 | Error | Public member without `[NexusKey]` or `[NexusIgnore]` |
| NEXNET031 | Error | Keyed member cannot be assigned during deserialization |
| NEXNET032 | Error | No usable constructor |
| NEXNET033 | Error | Union has a duplicate tag or a case that does not derive from it |
| NEXNET034 | Error | Abstract or interface `[NexusObject]` type has no `[NexusUnion<T>]` cases |
| NEXNET036 | Error | `[NexusObject]` type is not accessible to generated code |
| NEXNET037 | Warning | Large gap in `[NexusKey]` values |
| NEXNET038 | Error | Channel item type has no formatter |

## See Also

- [Channels](channels.md) — Streaming serialized items
- [Versioning](versioning.md) — How serialized types affect `HashLock`
- [Wire Protocol](../internals/protocol-specification.md) — Byte-level encoding
