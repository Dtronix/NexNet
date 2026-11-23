namespace NexNet.Inspector.Core.Schema;

// The numeric values are those of the generator IR's MessagePack form (docs/internals/nexnet-ir.md §5.1). A value this
// reader does not know (a newer schema) is kept as is; the decoder treats it as unknown.

/// <summary>How a method completes.</summary>
public enum SchemaReturnKind : byte
{
    /// <summary>Fire and forget: no result is sent back.</summary>
    Void = 0,

    /// <summary><c>ValueTask</c>: completes when the remote method completes, without a value.</summary>
    Task = 1,

    /// <summary><c>ValueTask&lt;T&gt;</c>: returns a value of <see cref="SchemaMethod.ReturnType"/>.</summary>
    TaskOf = 2,
}

/// <summary>The pipe or channel parameter of a method.</summary>
public enum SchemaPipeKind : byte
{
    /// <summary>No pipe.</summary>
    None = 0,

    /// <summary>An <c>INexusDuplexPipe</c>: raw bytes.</summary>
    DuplexPipe = 1,

    /// <summary>An <c>INexusDuplexChannel&lt;T&gt;</c>: length-prefixed items of <see cref="SchemaMethod.PipeType"/>.</summary>
    Channel = 2,
}

/// <summary>What a method parameter is.</summary>
public enum SchemaParamKind : byte
{
    /// <summary>A serialized value.</summary>
    Value = 0,

    /// <summary>A cancellation token: not sent.</summary>
    CancellationToken = 1,

    /// <summary>A raw duplex pipe, sent as the opener's pipe ID.</summary>
    DuplexPipe = 2,

    /// <summary>A typed channel, sent as the opener's pipe ID.</summary>
    DuplexChannel = 3,
}

/// <summary>Kinds of synchronized collection.</summary>
public enum SchemaCollectionKind : byte
{
    /// <summary>Not set.</summary>
    Unset = 0,

    /// <summary>An <c>INexusList&lt;T&gt;</c>.</summary>
    List = 1,
}

/// <summary>Synchronization modes of a collection.</summary>
public enum SchemaCollectionMode : byte
{
    /// <summary>Not set.</summary>
    Unset = 0,

    /// <summary>The server streams changes; clients only read.</summary>
    ServerToClient = 1,

    /// <summary>Clients send change requests too.</summary>
    BiDirectional = 2,

    /// <summary>The server mirrors another server's collection.</summary>
    Relay = 3,
}

/// <summary>Built-in leaf types, named after their .NET type.</summary>
public enum SchemaPrimKind : byte
{
    /// <summary>Not a primitive.</summary>
    None = 0,

    /// <summary><c>bool</c>.</summary>
    Bool = 1,

    /// <summary><c>sbyte</c>.</summary>
    SByte = 2,

    /// <summary><c>byte</c>.</summary>
    Byte = 3,

    /// <summary><c>short</c>.</summary>
    Int16 = 4,

    /// <summary><c>ushort</c>.</summary>
    UInt16 = 5,

    /// <summary><c>int</c>.</summary>
    Int32 = 6,

    /// <summary><c>uint</c>.</summary>
    UInt32 = 7,

    /// <summary><c>long</c>.</summary>
    Int64 = 8,

    /// <summary><c>ulong</c>.</summary>
    UInt64 = 9,

    /// <summary><c>float</c>.</summary>
    Single = 10,

    /// <summary><c>double</c>.</summary>
    Double = 11,

    /// <summary><c>Half</c>.</summary>
    Half = 12,

    /// <summary><c>char</c>.</summary>
    Char = 13,

    /// <summary><c>decimal</c>.</summary>
    Decimal = 14,

    /// <summary><c>string</c>.</summary>
    String = 15,

    /// <summary><c>DateTime</c>.</summary>
    DateTime = 16,

    /// <summary><c>DateTimeOffset</c>.</summary>
    DateTimeOffset = 17,

    /// <summary><c>TimeSpan</c>.</summary>
    TimeSpan = 18,

    /// <summary><c>DateOnly</c>.</summary>
    DateOnly = 19,

    /// <summary><c>TimeOnly</c>.</summary>
    TimeOnly = 20,

    /// <summary><c>Guid</c>.</summary>
    Guid = 21,

    /// <summary><c>BigInteger</c>.</summary>
    BigInteger = 22,

    /// <summary><c>Uri</c>.</summary>
    Uri = 23,

    /// <summary><c>Version</c>.</summary>
    Version = 24,
}

/// <summary>Kinds of type reference (the IR's <c>TypeRefKind</c>).</summary>
public enum SchemaTypeKind : byte
{
    /// <summary>A built-in leaf: <see cref="SchemaTypeRef.Prim"/>.</summary>
    Prim = 0,

    /// <summary>Bytes written as MessagePack bin.</summary>
    Bin = 1,

    /// <summary>An ext 78 primitive array of <see cref="SchemaTypeRef.Prim"/>.</summary>
    PrimArray = 2,

    /// <summary>An element-wise sequence of <c>Args[0]</c>.</summary>
    Seq = 3,

    /// <summary>A set of <c>Args[0]</c>.</summary>
    Set = 4,

    /// <summary>A queue of <c>Args[0]</c>, front first.</summary>
    Queue = 5,

    /// <summary>A stack of <c>Args[0]</c>, top first.</summary>
    Stack = 6,

    /// <summary>A map from <c>Args[0]</c> to <c>Args[1]</c>.</summary>
    Map = 7,

    /// <summary>A tuple of <c>Args</c>, written as an array.</summary>
    Tuple = 8,

    /// <summary>Nil or <c>Args[0]</c>.</summary>
    Nullable = 9,

    /// <summary>A declared type (<see cref="SchemaTypeRef.DeclId"/>) with <c>Args</c> as its type arguments.</summary>
    Decl = 10,

    /// <summary>A type parameter of the enclosing generic declaration (<see cref="SchemaTypeRef.Ordinal"/>).</summary>
    TypeParam = 11,

    /// <summary>A type with no language-neutral form (a user formatter type or <c>object</c>).</summary>
    External = 12,
}

/// <summary>Kinds of member initializer literal.</summary>
public enum SchemaLiteralKind : byte
{
    /// <summary><c>null</c>.</summary>
    Null = 0,

    /// <summary>A boolean.</summary>
    Bool = 1,

    /// <summary>An integer.</summary>
    Integer = 2,

    /// <summary>A real number.</summary>
    Real = 3,

    /// <summary>A decimal.</summary>
    Decimal = 4,

    /// <summary>A string.</summary>
    String = 5,

    /// <summary>A character.</summary>
    Char = 6,

    /// <summary>An enum member; the value is the member name.</summary>
    Enum = 7,
}

/// <summary>Kinds of declared type.</summary>
public enum SchemaDeclKind : byte
{
    /// <summary>A <c>[NexusObject]</c> class or struct.</summary>
    Object = 0,

    /// <summary>A <c>[NexusObject]</c> union.</summary>
    Union = 1,

    /// <summary>An enum.</summary>
    Enum = 2,
}
