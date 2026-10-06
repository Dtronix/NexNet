using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Serialization;

/// <summary>
/// How one type is serialized. Built once per producer by <see cref="ShapeBuilder"/> and read by both code generation
/// (<see cref="SerializationBuilder"/>) and hashing (<see cref="ShapeHasher"/>). Holds symbols, so it never leaves the
/// transform phase. Shapes reference other shapes, so the shape graph can contain cycles.
/// </summary>
internal abstract class TypeShape
{
    protected TypeShape(ITypeSymbol type) => Type = type;

    /// <summary>The type, with its top-level nullable annotation removed.</summary>
    public ITypeSymbol Type { get; }
}

/// <summary>A [NexusObject] class or struct, written as an array of MaxKey + 1 elements.</summary>
internal sealed class ObjectShape : TypeShape
{
    public ObjectShape(INamedTypeSymbol type) : base(type) { }

    public bool IsValueType => Type.IsValueType;

    public bool IsAbstractOrInterface => Type.IsAbstract || Type.TypeKind == TypeKind.Interface;

    /// <summary>Keyed, non-ignored members in key order. Filled after construction (cycles).</summary>
    public List<MemberShape> Members { get; } = new();

    /// <summary>Problems found while reading members (NEXNET030 and similar); reported only by code generation.</summary>
    public List<SerializationDiagnostic> Problems { get; } = new();
}

/// <summary>One keyed member of an <see cref="ObjectShape"/>.</summary>
internal sealed class MemberShape
{
    public ISymbol Symbol = null!;
    public string Name = null!;
    public int Key;
    public TypeShape Type = null!;

    /// <summary>The member type with its annotation, for generated local declarations.</summary>
    public ITypeSymbol DeclaredType = null!;

    public bool IsField;
    public bool CanGet;
    public bool CanSet;
    public bool IsInitOnly;
    public bool IsRequired;
    public IFieldSymbol? BackingField;
    public string LocalName = null!;
}

/// <summary>A [NexusObject] abstract class or interface with [NexusUnion&lt;T&gt;(tag)] cases, written as [tag, value].</summary>
internal sealed class UnionShape : TypeShape
{
    public UnionShape(INamedTypeSymbol type) : base(type) { }

    /// <summary>Cases in declaration order (code generation keeps it); hashing sorts by tag. Filled after construction (cycles).</summary>
    public List<(ushort Tag, TypeShape Case)> Cases { get; } = new();
}

/// <summary>An enum, written as its underlying integer.</summary>
internal sealed class EnumShape : TypeShape
{
    public EnumShape(INamedTypeSymbol type, SpecialType underlying, long[] values) : base(type)
    {
        Underlying = underlying;
        Values = values;
    }

    public SpecialType Underlying { get; }

    /// <summary>Distinct constant values, ascending. Member names are not part of the shape.</summary>
    public long[] Values { get; }
}

/// <summary>A <see cref="Nullable{T}"/> value type.</summary>
internal sealed class NullableShape : TypeShape
{
    public NullableShape(ITypeSymbol type) : base(type) { }

    public TypeShape Inner { get; internal set; } = null!;
}

internal sealed class ArrayShape : TypeShape
{
    public ArrayShape(IArrayTypeSymbol type) : base(type) { }

    public int Rank => ((IArrayTypeSymbol)Type).Rank;

    public TypeShape Element { get; internal set; } = null!;
}

internal enum NamedKind : byte
{
    Special,
    Clr,
    BuiltInGeneric,
    UserFormatter,
    Unsupported,
}

/// <summary>Built-in, CLR, custom-formatter or unsupported type, identified by its .NET identity.</summary>
internal sealed class NamedShape : TypeShape
{
    public NamedShape(ITypeSymbol type, NamedKind kind, string metadataName) : base(type)
    {
        Kind = kind;
        MetadataName = metadataName;
    }

    public NamedKind Kind { get; }

    /// <summary>Full metadata name of the definition, e.g. <c>System.Collections.Generic.List`1</c>.</summary>
    public string MetadataName { get; }

    public TypeShape[] Arguments { get; internal set; } = Array.Empty<TypeShape>();
}

/// <summary>A type parameter of an open generic definition (code generation of generic formatter classes only).</summary>
internal sealed class TypeParameterShape : TypeShape
{
    public TypeParameterShape(ITypeParameterSymbol type) : base(type) { }

    public int Ordinal => ((ITypeParameterSymbol)Type).Ordinal;
}
