using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>
/// The typed-JSON codecs of one generated class: one <c>TypedCodec&lt;T&gt;</c> field per distinct C# type, the
/// <c>SchemaSet</c> of the declared types, and a module initializer registering the codecs in <c>TypedCodecs</c>.
/// </summary>
internal sealed class CodecTable
{
    private const string Rt = "global::NexNet.Interop.";

    private readonly string _owner;
    private readonly List<(string Type, string Schema)> _codecs = new();
    private readonly Dictionary<string, int> _byType = new(StringComparer.Ordinal);

    /// <param name="owner">The generated class the nested <c>__Schemas</c> class lives in.</param>
    public CodecTable(string owner) => _owner = owner;

    /// <summary>The expression of the codec of C# type <paramref name="csType"/> with shape <paramref name="type"/>.</summary>
    public string Codec(string csType, TypeRef type)
    {
        if (!_byType.TryGetValue(csType, out var index))
        {
            index = _codecs.Count;
            _codecs.Add((csType, Schema(type)));
            _byType[csType] = index;
        }

        return _owner + ".__Schemas.Codec" + CSharpNames.Int(index);
    }

    /// <summary>Writes the nested <c>__Schemas</c> class.</summary>
    public void Write(CodeWriter w, EquatableArray<TypeDeclIr> decls)
    {
        using (w.Block("internal static class __Schemas"))
        {
            var declList = decls.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            if (declList.Length == 0)
            {
                w.Line("internal static readonly " + Rt + "SchemaSet Set = " + Rt + "SchemaSet.Empty;");
            }
            else
            {
                w.Line("internal static readonly " + Rt + "SchemaSet Set = new " + Rt + "SchemaSet(");
                w.Indent();
                var byId = declList.ToDictionary(d => d.Id, StringComparer.Ordinal);
                for (var i = 0; i < declList.Length; i++)
                    w.Line(Decl(declList[i], byId) + (i == declList.Length - 1 ? ");" : ","));
                w.Unindent();
            }

            for (var i = 0; i < _codecs.Count; i++)
            {
                w.Line();
                w.Line("internal static readonly " + Rt + "TypedCodec<" + _codecs[i].Type + "> Codec" + CSharpNames.Int(i) + " =");
                w.Indent().Line("new " + Rt + "TypedCodec<" + _codecs[i].Type + ">(" + _codecs[i].Schema + ", Set);").Unindent();
            }

            w.Line();
            w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
            using (w.Block("internal static void Register()"))
            {
                for (var i = 0; i < _codecs.Count; i++)
                    w.Line(Rt + "TypedCodecs.Register(Codec" + CSharpNames.Int(i) + ");");
            }
        }
    }

    /// <summary>The <c>TypeSchema</c> expression of an IR type reference.</summary>
    internal static string Schema(TypeRef type)
    {
        const string S = Rt + "TypeSchema.";
        string Arg(int i) => i < type.Args.Length ? Schema(type.Args[i]) : throw new InvalidOperationException("Malformed type reference " + type.Kind + ".");
        string Args() => string.Join(", ", type.Args.Select(Schema));
        string Prim() => Rt + "SchemaPrim." + type.Prim;

        return type.Kind switch
        {
            TypeRefKind.Prim => S + "Primitive(" + Prim() + ")",
            TypeRefKind.Bin => S + "Bin()",
            TypeRefKind.PrimArray => S + "PrimArray(" + Prim() + ")",
            TypeRefKind.Seq => S + "Seq(" + Arg(0) + (type.Clr == null ? "" : ", " + CSharpNames.Literal(type.Clr)) + ")",
            TypeRefKind.Set => S + "Set(" + Arg(0) + ")",
            TypeRefKind.Queue => S + "Queue(" + Arg(0) + ")",
            TypeRefKind.Stack => S + "Stack(" + Arg(0) + ")",
            TypeRefKind.Map => S + "Map(" + Arg(0) + ", " + Arg(1) + ")",
            TypeRefKind.Tuple => S + "Tuple(" + Args() + ")",
            TypeRefKind.Nullable => S + "Nullable(" + Arg(0) + ")",
            TypeRefKind.Decl => S + "Decl(" + CSharpNames.Literal(type.DeclId ?? "") + (type.Args.Length == 0 ? "" : ", " + Args()) + ")",
            TypeRefKind.TypeParam => S + "TypeParam(" + CSharpNames.Int(type.Ordinal) + ")",
            TypeRefKind.External => S + "External(" + CSharpNames.Literal(type.Clr ?? "") + ")",
            _ => throw new InvalidOperationException("Unknown type reference kind " + type.Kind + "."),
        };
    }

    private static string Decl(TypeDeclIr decl, Dictionary<string, TypeDeclIr> byId)
    {
        var id = CSharpNames.Literal(decl.Id);
        switch (decl)
        {
            case ObjectDeclIr obj:
            {
                // Members of the base types are part of the same array.
                var members = new List<MemberIr>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var current = obj; current != null && seen.Add(current.Id); current = current.BaseDeclId != null && byId.TryGetValue(current.BaseDeclId, out var b) ? b as ObjectDeclIr : null)
                    members.AddRange(current.Members);

                var list = string.Join(", ", members
                    .OrderBy(m => m.Key)
                    .Select(m => "new(" + CSharpNames.Int(m.Key) + ", " + CSharpNames.Literal(m.Name) + ", " + Schema(m.Type) + ")"));
                return "new " + Rt + "ObjectSchema(" + id + ", " + CSharpNames.Int(obj.TypeParams.Length) + ", " + CSharpNames.Int(obj.ArrayLength)
                       + ", new " + Rt + "MemberSchema[] { " + list + " })";
            }
            case UnionDeclIr union:
            {
                var cases = string.Join(", ", union.Cases.Select(c => "new(" + c.Tag.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + CSharpNames.Literal(c.CaseDeclId) + ")"));
                return "new " + Rt + "UnionSchema(" + id + ", " + CSharpNames.Int(union.TypeParams.Length) + ", new " + Rt + "UnionCaseSchema[] { " + cases + " })";
            }
            case EnumDeclIr enumDecl:
                return "new " + Rt + "EnumSchema(" + id + ", " + Rt + "SchemaPrim." + enumDecl.Underlying + ")";
            default:
                throw new InvalidOperationException("Unknown declaration " + decl.GetType().Name + ".");
        }
    }
}

/// <summary>Shared text of the generated files.</summary>
internal static class ScaffoldFile
{
    /// <summary>Writes the file header and opens the namespace; returns the scope to dispose at the end of the file.</summary>
    public static IDisposable? Begin(CodeWriter w, string ns)
    {
        w.Line("// <auto-generated/>");
        w.Line("// NexNet C# test scaffolding ([assembly: NexusTestScaffolding<TServer, TClient>]).");
        w.Line("#nullable enable");
        w.Line("#pragma warning disable CS0612, CS0618, CS1591, CA2255");
        w.Line();
        return ns.Length == 0 ? null : w.Block("namespace " + ns);
    }
}
