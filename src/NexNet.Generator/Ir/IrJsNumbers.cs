using Microsoft.CodeAnalysis;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// The opt-in checked-number mapping of 64-bit integers for JavaScript (D13, <c>[NexusJsNumber]</c>):
/// <see cref="TypeRef.JsNumber"/> on the <see cref="PrimKind.Int64"/>/<see cref="PrimKind.UInt64"/> leaves of a member,
/// parameter or return value with the attribute, or of the whole IR with <c>[assembly: NexusJsNumber]</c>.
/// </summary>
internal static class IrJsNumbers
{
    private const string AttributeName = "NexusJsNumberAttribute";
    private const string AttributeNamespace = "NexNet.Serialization";

    /// <summary>True when <paramref name="attributes"/> holds <c>[NexusJsNumber]</c>.</summary>
    public static bool Has(IEnumerable<AttributeData> attributes)
        => attributes.Any(a => a.AttributeClass is { Name: AttributeName } cls
                               && cls.ContainingNamespace?.ToDisplayString() == AttributeNamespace);

    /// <summary>True when the symbol carries <c>[NexusJsNumber]</c>.</summary>
    public static bool Has(ISymbol? symbol) => symbol != null && Has(symbol.GetAttributes());

    /// <summary>
    /// True when a serialized member carries <c>[NexusJsNumber]</c>, or, for a positional record property, when the
    /// primary constructor parameter it was declared by does (the attribute applies to the parameter without
    /// <c>property:</c>).
    /// </summary>
    public static bool HasMember(ISymbol member)
    {
        if (Has(member))
            return true;
        if (member is not IPropertySymbol { ContainingType: { IsRecord: true } type } property)
            return false;

        foreach (var ctor in type.InstanceConstructors)
        {
            var isPrimary = ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax);
            if (isPrimary && ctor.Parameters.Any(p => p.Name == property.Name && Has(p)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Marks every Int64/UInt64 leaf of <paramref name="type"/>, through Nullable, Seq, Set, Queue, Stack, Map, Tuple and
    /// the type arguments of a Decl. PrimArray (ext 78) and External references are left as they are.
    /// </summary>
    public static TypeRef Mark(TypeRef type)
    {
        switch (type.Kind)
        {
            case TypeRefKind.Prim when type.Prim is PrimKind.Int64 or PrimKind.UInt64:
                return type.JsNumber ? type : type with { JsNumber = true };

            case TypeRefKind.Seq:
            case TypeRefKind.Set:
            case TypeRefKind.Queue:
            case TypeRefKind.Stack:
            case TypeRefKind.Map:
            case TypeRefKind.Tuple:
            case TypeRefKind.Nullable:
            case TypeRefKind.Decl:
                return type.Args.Length == 0
                    ? type
                    : type with { Args = new EquatableArray<TypeRef>(type.Args.Select(Mark).ToArray()) };

            default:
                return type;
        }
    }

    /// <summary>Marks <paramref name="type"/> when <paramref name="optedIn"/>; null stays null.</summary>
    public static TypeRef? MarkIf(TypeRef? type, bool optedIn) => optedIn && type != null ? Mark(type) : type;

    /// <summary>
    /// Applies <c>[assembly: NexusJsNumber]</c> of <paramref name="compilation"/> to every reference of the merged IR.
    /// Returns <paramref name="ir"/> unchanged without the attribute.
    /// </summary>
    public static NexusIr Apply(NexusIr ir, Compilation compilation)
        => Has(compilation.Assembly.GetAttributes()) ? MarkAll(ir) : ir;

    /// <summary>Marks every reference of the IR (the assembly-wide opt-in).</summary>
    public static NexusIr MarkAll(NexusIr ir) => ir with
    {
        Exports = Map(ir.Exports, e => e with { ServerApi = Interface(e.ServerApi), ClientApi = Interface(e.ClientApi) }),
        Types = Map(ir.Types, d => d is ObjectDeclIr obj
            ? obj with { Members = Map(obj.Members, m => m with { Type = Mark(m.Type) }) }
            : d),
    };

    private static InterfaceIr Interface(InterfaceIr api) => api with
    {
        Methods = Map(api.Methods, m => m with
        {
            ReturnType = MarkIf(m.ReturnType, true),
            PipeType = MarkIf(m.PipeType, true),
            Params = Map(m.Params, p => p with { Type = MarkIf(p.Type, true) }),
        }),
        Collections = Map(api.Collections, c => c with { Item = MarkIf(c.Item, true) }),
    };

    private static EquatableArray<T> Map<T>(EquatableArray<T> items, Func<T, T> map)
        where T : IEquatable<T>
        => items.Length == 0 ? items : new EquatableArray<T>(items.Select(map).ToArray());
}
