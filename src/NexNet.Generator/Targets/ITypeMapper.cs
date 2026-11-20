using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets;

/// <summary>Maps IR type references to one target language (the normative table in docs/articles/multi-language-clients.md, "Type mapping").</summary>
internal interface ITypeMapper
{
    /// <summary>
    /// The target type of <paramref name="type"/> in a declaration position (parameter, return, member), or an
    /// unsupported result when the type, or any type it contains, cannot be represented identically (NEXNET040).
    /// </summary>
    TargetType Map(TypeRef type, TypeScope scope);
}

/// <summary>The result of mapping one type reference.</summary>
/// <param name="Name">The type as written in the target language; empty when unsupported.</param>
/// <param name="ValueEquality">
/// True when two equal values of the type compare equal as set elements or map keys in the target (NEXNET046).
/// </param>
/// <param name="Unsupported">Why the type cannot be represented; null when it can.</param>
/// <param name="NativeFormatter">The user's native formatter for an external type, if one is declared.</param>
internal sealed record TargetType(string Name, bool ValueEquality, string? Unsupported = null, string? NativeFormatter = null)
{
    public bool IsSupported => Unsupported == null;

    public static TargetType NotSupported(string reason) => new("", false, reason);
}

/// <summary>What a type mapping needs from its surroundings: the IR's declared types and the enclosing type parameters.</summary>
internal sealed class TypeScope
{
    private readonly Dictionary<string, TypeDeclIr> _decls;

    public TypeScope(NexusIr ir)
        : this(ir.Types.ToDictionary(t => t.Id, StringComparer.Ordinal), Array.Empty<string>())
    {
    }

    private TypeScope(Dictionary<string, TypeDeclIr> decls, IReadOnlyList<string> typeParams)
    {
        _decls = decls;
        TypeParams = typeParams;
    }

    /// <summary>Type parameter names of the enclosing generic declaration, by ordinal.</summary>
    public IReadOnlyList<string> TypeParams { get; }

    /// <summary>The declared type with the id, or null.</summary>
    public TypeDeclIr? Find(string? id) => id != null && _decls.TryGetValue(id, out var decl) ? decl : null;

    /// <summary>The same scope inside a generic declaration.</summary>
    public TypeScope WithTypeParams(IReadOnlyList<string> typeParams) => new(_decls, typeParams);

    /// <summary>The name of the type parameter at the ordinal, or <c>T&lt;ordinal&gt;</c> when out of scope.</summary>
    public string TypeParam(int ordinal)
        => ordinal >= 0 && ordinal < TypeParams.Count ? TypeParams[ordinal] : "T" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
