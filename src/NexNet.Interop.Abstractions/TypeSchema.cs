using System;
using System.Collections.Generic;

namespace NexNet.Interop;

/// <summary>Built-in leaf types of the value model. Same names and order as the generator IR's <c>PrimKind</c>.</summary>
public enum SchemaPrim : byte
{
#pragma warning disable CS1591 // Named after the .NET type they stand for.
    None,
    Bool,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Half,
    Char,
    Decimal,
    String,
    DateTime,
    DateTimeOffset,
    TimeSpan,
    DateOnly,
    TimeOnly,
    Guid,
    BigInteger,
    Uri,
    Version,
#pragma warning restore CS1591
}

/// <summary>Shape kinds of a <see cref="TypeSchema"/>. Same names and order as the generator IR's <c>TypeRefKind</c>.</summary>
public enum TypeSchemaKind : byte
{
    /// <summary>A built-in leaf (<see cref="TypeSchema.Prim"/>).</summary>
    Prim,

    /// <summary>MessagePack bin.</summary>
    Bin,

    /// <summary>An ext 78 primitive array of <see cref="TypeSchema.Prim"/>.</summary>
    PrimArray,

    /// <summary>A sequence of <c>Args[0]</c>.</summary>
    Seq,

    /// <summary>A set of <c>Args[0]</c>.</summary>
    Set,

    /// <summary>A queue of <c>Args[0]</c>, front first.</summary>
    Queue,

    /// <summary>A stack of <c>Args[0]</c>, top first.</summary>
    Stack,

    /// <summary>A map from <c>Args[0]</c> to <c>Args[1]</c>.</summary>
    Map,

    /// <summary>A tuple of <c>Args</c>.</summary>
    Tuple,

    /// <summary>A <c>Nullable&lt;T&gt;</c> of <c>Args[0]</c>.</summary>
    Nullable,

    /// <summary>A declared type (<see cref="TypeSchema.DeclId"/>) with <c>Args</c> as type arguments.</summary>
    Decl,

    /// <summary>A type parameter of the enclosing declaration (<see cref="TypeSchema.Ordinal"/>).</summary>
    TypeParam,

    /// <summary>A type with a custom formatter: carried as its raw MessagePack bytes.</summary>
    External,
}

/// <summary>
/// The language-neutral shape of a serialized type, as the generator IR's <c>TypeRef</c> describes it. Drives the
/// typed-JSON transcoding of <see cref="TypedJson"/>. Immutable.
/// </summary>
public sealed class TypeSchema
{
    private static readonly TypeSchema[] NoArgs = Array.Empty<TypeSchema>();

    private TypeSchema(TypeSchemaKind kind, SchemaPrim prim = SchemaPrim.None, TypeSchema[]? args = null, string? declId = null, int ordinal = 0, string? clr = null)
    {
        Kind = kind;
        Prim = prim;
        ArgsArray = args ?? NoArgs;
        DeclId = declId;
        Ordinal = ordinal;
        Clr = clr;
    }

    /// <summary>The shape kind.</summary>
    public TypeSchemaKind Kind { get; }

    /// <summary><see cref="TypeSchemaKind.Prim"/>: the leaf; <see cref="TypeSchemaKind.PrimArray"/>: the element.</summary>
    public SchemaPrim Prim { get; }

    /// <summary>Element, key/value, tuple element, nullable inner or type argument schemas.</summary>
    public IReadOnlyList<TypeSchema> Args => ArgsArray;

    internal TypeSchema[] ArgsArray { get; }

    /// <summary><see cref="TypeSchemaKind.Decl"/>: the declared type id (its full metadata name).</summary>
    public string? DeclId { get; }

    /// <summary><see cref="TypeSchemaKind.TypeParam"/>: the type parameter position.</summary>
    public int Ordinal { get; }

    /// <summary><see cref="TypeSchemaKind.External"/>: the .NET type name.</summary>
    public string? Clr { get; }

#pragma warning disable CS1591 // Factories named after the kind they create.
    public static TypeSchema Primitive(SchemaPrim prim) => new(TypeSchemaKind.Prim, prim);
    public static TypeSchema Bin() => new(TypeSchemaKind.Bin);
    public static TypeSchema PrimArray(SchemaPrim element) => new(TypeSchemaKind.PrimArray, element);
    public static TypeSchema Seq(TypeSchema item) => new(TypeSchemaKind.Seq, args: new[] { item });
    public static TypeSchema Set(TypeSchema item) => new(TypeSchemaKind.Set, args: new[] { item });
    public static TypeSchema Queue(TypeSchema item) => new(TypeSchemaKind.Queue, args: new[] { item });
    public static TypeSchema Stack(TypeSchema item) => new(TypeSchemaKind.Stack, args: new[] { item });
    public static TypeSchema Map(TypeSchema key, TypeSchema value) => new(TypeSchemaKind.Map, args: new[] { key, value });
    public static TypeSchema Tuple(params TypeSchema[] items) => new(TypeSchemaKind.Tuple, args: (TypeSchema[])items.Clone());
    public static TypeSchema Nullable(TypeSchema inner) => new(TypeSchemaKind.Nullable, args: new[] { inner });
    public static TypeSchema Decl(string id, params TypeSchema[] args) => new(TypeSchemaKind.Decl, args: (TypeSchema[])args.Clone(), declId: id);
    public static TypeSchema TypeParam(int ordinal) => new(TypeSchemaKind.TypeParam, ordinal: ordinal);
    public static TypeSchema External(string clr) => new(TypeSchemaKind.External, clr: clr);
#pragma warning restore CS1591

    /// <summary>This schema with every type parameter replaced by <paramref name="typeArgs"/>.</summary>
    internal TypeSchema Bind(TypeSchema[] typeArgs)
    {
        if (Kind == TypeSchemaKind.TypeParam)
        {
            if (Ordinal < 0 || Ordinal >= typeArgs.Length)
                throw new FormatException("Type parameter " + Ordinal + " has no type argument.");
            return typeArgs[Ordinal];
        }

        if (ArgsArray.Length == 0)
            return this;

        var bound = new TypeSchema[ArgsArray.Length];
        var changed = false;
        for (var i = 0; i < bound.Length; i++)
        {
            bound[i] = ArgsArray[i].Bind(typeArgs);
            changed |= !ReferenceEquals(bound[i], ArgsArray[i]);
        }

        return changed ? new TypeSchema(Kind, Prim, bound, DeclId, Ordinal, Clr) : this;
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        TypeSchemaKind.Prim => Prim.ToString(),
        TypeSchemaKind.PrimArray => Prim + "[]",
        TypeSchemaKind.Decl => ArgsArray.Length == 0 ? DeclId! : DeclId + "<" + string.Join(", ", (object[])ArgsArray) + ">",
        TypeSchemaKind.TypeParam => "T" + Ordinal,
        TypeSchemaKind.External => Clr ?? "External",
        _ => Kind + (ArgsArray.Length == 0 ? "" : "<" + string.Join(", ", (object[])ArgsArray) + ">"),
    };
}

/// <summary>A declared type of the value model.</summary>
public abstract class DeclSchema
{
    private protected DeclSchema(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));

    /// <summary>The declared type id: its full metadata name.</summary>
    public string Id { get; }
}

/// <summary>One keyed member of an object.</summary>
/// <param name="Key">Array position.</param>
/// <param name="Name">Member name.</param>
/// <param name="Type">Member type.</param>
public readonly record struct MemberSchema(int Key, string Name, TypeSchema Type);

/// <summary>A [NexusObject] class or struct: an array of <see cref="ArrayLength"/> elements by key, nil in the gaps.</summary>
public sealed class ObjectSchema : DeclSchema
{
    private readonly Dictionary<int, MemberSchema> _byKey = new();

    /// <summary>Creates an object schema.</summary>
    /// <param name="id">Declared type id.</param>
    /// <param name="typeParameterCount">Number of type parameters.</param>
    /// <param name="arrayLength">Length of the serialized array.</param>
    /// <param name="members">Every member, including those of base types.</param>
    public ObjectSchema(string id, int typeParameterCount, int arrayLength, IReadOnlyList<MemberSchema> members)
        : base(id)
    {
        TypeParameterCount = typeParameterCount;
        ArrayLength = arrayLength;
        Members = members ?? throw new ArgumentNullException(nameof(members));
        foreach (var member in members)
            _byKey[member.Key] = member;
    }

    /// <summary>Number of type parameters.</summary>
    public int TypeParameterCount { get; }

    /// <summary>Length of the serialized array.</summary>
    public int ArrayLength { get; }

    /// <summary>Every member, including those of base types.</summary>
    public IReadOnlyList<MemberSchema> Members { get; }

    /// <summary>The member at array position <paramref name="key"/>, if any.</summary>
    public bool TryGetMember(int key, out MemberSchema member) => _byKey.TryGetValue(key, out member);
}

/// <summary>One union case.</summary>
/// <param name="Tag">Tag written on the wire.</param>
/// <param name="CaseId">Declared type id of the case.</param>
public readonly record struct UnionCaseSchema(ushort Tag, string CaseId);

/// <summary>A [NexusObject] union: <c>[tag, value]</c>.</summary>
public sealed class UnionSchema : DeclSchema
{
    private readonly Dictionary<ushort, UnionCaseSchema> _byTag = new();

    /// <summary>Creates a union schema.</summary>
    /// <param name="id">Declared type id.</param>
    /// <param name="typeParameterCount">Number of type parameters (shared with the cases).</param>
    /// <param name="cases">The cases.</param>
    public UnionSchema(string id, int typeParameterCount, IReadOnlyList<UnionCaseSchema> cases)
        : base(id)
    {
        TypeParameterCount = typeParameterCount;
        Cases = cases ?? throw new ArgumentNullException(nameof(cases));
        foreach (var unionCase in cases)
            _byTag[unionCase.Tag] = unionCase;
    }

    /// <summary>Number of type parameters.</summary>
    public int TypeParameterCount { get; }

    /// <summary>The cases.</summary>
    public IReadOnlyList<UnionCaseSchema> Cases { get; }

    /// <summary>The case with <paramref name="tag"/>, if any.</summary>
    public bool TryGetCase(ushort tag, out UnionCaseSchema unionCase) => _byTag.TryGetValue(tag, out unionCase);
}

/// <summary>An enum: its underlying integer.</summary>
public sealed class EnumSchema : DeclSchema
{
    /// <summary>Creates an enum schema.</summary>
    /// <param name="id">Declared type id.</param>
    /// <param name="underlying">The underlying integer type.</param>
    public EnumSchema(string id, SchemaPrim underlying)
        : base(id)
    {
        Underlying = underlying;
    }

    /// <summary>The underlying integer type.</summary>
    public SchemaPrim Underlying { get; }
}

/// <summary>The declared types a set of <see cref="TypeSchema"/>s refers to, by id.</summary>
public sealed class SchemaSet
{
    private readonly Dictionary<string, DeclSchema> _decls = new(StringComparer.Ordinal);

    /// <summary>A set without declared types.</summary>
    public static SchemaSet Empty { get; } = new();

    /// <summary>Creates a set. Ids must be unique.</summary>
    public SchemaSet(params DeclSchema[] decls)
    {
        foreach (var decl in decls)
            _decls.Add(decl.Id, decl);
    }

    /// <summary>Every declared type.</summary>
    public IEnumerable<DeclSchema> Decls => _decls.Values;

    /// <summary>The declared type with <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">No declared type has the id.</exception>
    public DeclSchema Find(string id)
        => _decls.TryGetValue(id, out var decl) ? decl : throw new KeyNotFoundException("No schema for the declared type '" + id + "'.");
}
