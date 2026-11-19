using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>What the JavaScript emitters share for one IR: the declared types, names and the runtime module.</summary>
internal sealed class JsContext
{
    private readonly Dictionary<string, List<UnionDeclIr>> _unionsOfCase = new(StringComparer.Ordinal);

    public JsContext(JavaScriptTarget target, NexusIr ir, IReadOnlyList<TypeDeclIr> decls, string runtimeModule)
    {
        Target = target;
        Ir = ir;
        Decls = decls;
        Scope = new TypeScope(ir);
        RuntimeModule = runtimeModule;

        foreach (var union in decls.OfType<UnionDeclIr>())
        {
            foreach (var unionCase in union.Cases)
            {
                if (!_unionsOfCase.TryGetValue(unionCase.CaseDeclId, out var list))
                    _unionsOfCase[unionCase.CaseDeclId] = list = new List<UnionDeclIr>();
                list.Add(union);
            }
        }
    }

    public JavaScriptTarget Target { get; }

    public NexusIr Ir { get; }

    /// <summary>The declared types emitted for the target, ordered by id.</summary>
    public IReadOnlyList<TypeDeclIr> Decls { get; }

    public TypeScope Scope { get; }

    /// <summary>The module specifier of the runtime (<c>@dtronix/nexnet</c> unless <c>ModuleName</c> is set).</summary>
    public string RuntimeModule { get; }

    /// <summary>The JavaScript name of a declared type (also its file name without extension).</summary>
    public string Name(TypeDeclIr decl) => Target.TypeName(decl);

    /// <summary>The TypeScript type of a type reference.</summary>
    public string TsType(TypeRef type, TypeScope scope) => Target.Types.Map(type, scope).Name;

    /// <summary>The unions that list the type as a case.</summary>
    public IReadOnlyList<UnionDeclIr> UnionsOf(TypeDeclIr decl)
        => _unionsOfCase.TryGetValue(decl.Id, out var list) ? list : (IReadOnlyList<UnionDeclIr>)Array.Empty<UnionDeclIr>();

    /// <summary>
    /// The class a generated class extends: its [NexusObject] base, or the abstract-class union it is a case of. Null
    /// for interface unions and generic bases (the IR has no base type arguments): the class then declares every member.
    /// </summary>
    public TypeDeclIr? BaseClass(ObjectDeclIr obj)
    {
        var decl = Scope.Find(obj.BaseDeclId);
        switch (decl)
        {
            case ObjectDeclIr baseObj when baseObj.TypeParams.Length == 0:
                return baseObj;
            case UnionDeclIr { IsInterface: false } union when union.TypeParams.Length == 0:
                return union;
        }

        return null;
    }

    /// <summary>Every member of the type, base members included (the IR lists them all), by key.</summary>
    public static IReadOnlyList<MemberIr> Members(ObjectDeclIr obj) => obj.Members.ToArray();

    /// <summary>The members a class declares itself: those its generated base class does not.</summary>
    public IReadOnlyList<MemberIr> OwnMembers(ObjectDeclIr obj)
    {
        if (BaseClass(obj) is not ObjectDeclIr baseObj)
            return obj.Members.ToArray();
        var inherited = new HashSet<string>(baseObj.Members.Select(m => m.Name), StringComparer.Ordinal);
        return obj.Members.Where(m => !inherited.Contains(m.Name)).ToArray();
    }

    /// <summary>The type parameters of a declared type.</summary>
    public static string[] TypeParams(TypeDeclIr decl) => decl switch
    {
        ObjectDeclIr o => o.TypeParams.ToArray(),
        UnionDeclIr u => u.TypeParams.ToArray(),
        _ => Array.Empty<string>(),
    };

    /// <summary>True for types C# treats as values: structs and enums (their formatters refuse null).</summary>
    public static bool IsValueType(TypeDeclIr decl) => decl is EnumDeclIr or ObjectDeclIr { IsStruct: true };

    /// <summary>The .NET name of a declared type for docs: <c>Ns.Outer.Inner</c>.</summary>
    public static string ClrName(TypeDeclIr decl)
    {
        var name = string.Join(".", decl.ContainingTypes.Concat(new[] { decl.Name }));
        return decl.ClrNamespace.Length == 0 ? name : decl.ClrNamespace + "." + name;
    }
}
