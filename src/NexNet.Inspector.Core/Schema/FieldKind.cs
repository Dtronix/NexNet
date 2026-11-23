namespace NexNet.Inspector.Core.Schema;

/// <summary>What a <see cref="DecodedField"/> is.</summary>
public enum FieldKind : byte
{
    /// <summary>Bytes that were not decoded: no schema, an unknown method, or what follows a decoding failure.</summary>
    Raw = 0,

    /// <summary>One whole MessagePack value the schema does not describe (or describes as something else); skipped.</summary>
    Unknown = 1,

    /// <summary>Nil.</summary>
    Nil = 2,

    /// <summary>A boolean: <see cref="DecodedScalar.Boolean"/>.</summary>
    Boolean = 3,

    /// <summary>A signed integer (sbyte to long): <see cref="DecodedScalar.Int64"/>.</summary>
    Integer = 4,

    /// <summary>An unsigned integer (byte to ulong): <see cref="DecodedScalar.UInt64"/>.</summary>
    UnsignedInteger = 5,

    /// <summary>A <c>float</c>: <see cref="DecodedScalar.Single"/>.</summary>
    Single = 6,

    /// <summary>A <c>double</c>: <see cref="DecodedScalar.Double"/>.</summary>
    Double = 7,

    /// <summary>A <c>Half</c>: <see cref="DecodedScalar.Half"/>.</summary>
    Half = 8,

    /// <summary>A <c>char</c>: <see cref="DecodedScalar.Char"/>.</summary>
    Char = 9,

    /// <summary>A string; its UTF-8 bytes are the field's content.</summary>
    String = 10,

    /// <summary>Bytes (bin); they are the field's content.</summary>
    Binary = 11,

    /// <summary>A <c>decimal</c>: <see cref="DecodedScalar.Int64"/> holds lo and mid, <see cref="DecodedScalar.Extra"/> hi and flags.</summary>
    Decimal = 12,

    /// <summary>A <c>DateTime</c>: <see cref="DecodedScalar.Int64"/> is its <c>ToBinary</c> value.</summary>
    DateTime = 13,

    /// <summary>A <c>DateTimeOffset</c>: <see cref="DecodedScalar.Int64"/> UTC ticks, <see cref="DecodedScalar.Extra"/> offset minutes.</summary>
    DateTimeOffset = 14,

    /// <summary>A <c>TimeSpan</c>: ticks.</summary>
    TimeSpan = 15,

    /// <summary>A <c>DateOnly</c>: the day number.</summary>
    DateOnly = 16,

    /// <summary>A <c>TimeOnly</c>: ticks since midnight.</summary>
    TimeOnly = 17,

    /// <summary>A <c>Guid</c>; its 16 big-endian bytes are the field's content.</summary>
    Guid = 18,

    /// <summary>A <c>BigInteger</c>; its little-endian two's complement bytes are the field's content.</summary>
    BigInteger = 19,

    /// <summary>A <c>Uri</c>; its UTF-8 bytes are the field's content.</summary>
    Uri = 20,

    /// <summary>A <c>Version</c>; its UTF-8 bytes are the field's content.</summary>
    Version = 21,

    /// <summary>An enum value: the underlying integer in <see cref="DecodedScalar.Int64"/>; <see cref="DecodedField.Decl"/> names it.</summary>
    Enum = 22,

    /// <summary>A <c>[NexusObject]</c> object; its children are its members.</summary>
    Object = 23,

    /// <summary>A union: the tag in <see cref="DecodedScalar.Int64"/>; its children are the tag and the case value.</summary>
    Union = 24,

    /// <summary>The tag of a union or collection message.</summary>
    UnionTag = 25,

    /// <summary>An array, list, set, queue or stack; its children are the elements.</summary>
    Sequence = 26,

    /// <summary>
    /// An ext 78 primitive array: <see cref="DecodedScalar.Int64"/> is the element count, <see cref="DecodedScalar.Extra"/>
    /// the element kind byte; the elements are the content (read one with <see cref="DecodedValue.TryGetElement"/>).
    /// </summary>
    PrimitiveArray = 27,

    /// <summary>A map; its children are its entries.</summary>
    Map = 28,

    /// <summary>One map entry; its children are the key and the value.</summary>
    MapEntry = 29,

    /// <summary>A tuple or key/value pair; its children are the elements.</summary>
    Tuple = 30,

    /// <summary>A value of an external type (user formatter); skipped whole.</summary>
    External = 31,

    /// <summary>The arguments of an invocation; its children are the serialized parameters.</summary>
    Arguments = 32,

    /// <summary>A pipe or channel argument: the opener's local pipe ID in <see cref="DecodedScalar.Int64"/>.</summary>
    PipeId = 33,

    /// <summary>The detail of an Exception result: code, type and message.</summary>
    ExceptionDetail = 34,

    /// <summary>A collection message (spec §4.11): the tag in <see cref="DecodedScalar.Int64"/>; children tag and body.</summary>
    CollectionMessage = 35,

    /// <summary>The body of a collection message; children flags, version, indices and values.</summary>
    CollectionBody = 36,
}

/// <summary>Why a field is raw or unknown, or what else went wrong at it.</summary>
public enum DecodeProblem : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>No schema was given.</summary>
    NoSchema = 1,

    /// <summary>The schema has no method or collection with this ID.</summary>
    UnknownMethod = 2,

    /// <summary>The schema has no type for this value (a missing declaration, an unresolved type parameter, an unknown pipe).</summary>
    UnknownType = 3,

    /// <summary>The value does not have the wire form of its type.</summary>
    TypeMismatch = 4,

    /// <summary>The bytes end inside the value.</summary>
    Truncated = 5,

    /// <summary>The bytes are not valid MessagePack (the reserved code 0xc1).</summary>
    Malformed = 6,

    /// <summary>The field or depth limit of <see cref="SchemaDecoderOptions"/> was reached; the value was skipped.</summary>
    LimitReached = 7,

    /// <summary>Bytes follow the decoded value.</summary>
    TrailingBytes = 8,

    /// <summary>The bytes are no longer (or not yet) available.</summary>
    BytesUnavailable = 9,

    /// <summary>A union or collection message tag the schema does not know.</summary>
    UnknownTag = 10,
}
