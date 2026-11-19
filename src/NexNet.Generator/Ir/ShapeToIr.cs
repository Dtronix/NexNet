using Microsoft.CodeAnalysis;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// Projects <see cref="TypeShape"/>s into <see cref="TypeRef"/>s and collects a <see cref="TypeDeclIr"/> for every
/// [NexusObject] type and enum reached. One instance per producer, sharing the producer's <see cref="ShapeBuilder"/>, so
/// the IR describes exactly what code generation and hashing see.
/// </summary>
internal sealed class ShapeToIr
{
    private readonly ShapeBuilder _shapes;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, TypeDeclIr> _decls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

    public ShapeToIr(ShapeBuilder shapes, CancellationToken cancellationToken)
    {
        _shapes = shapes;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The reference for a declared type; nullable annotations are read from <paramref name="declared"/>.</summary>
    public TypeRef Convert(ITypeSymbol declared) => Convert(_shapes.Get(declared), declared);

    /// <summary>
    /// The declarations reached so far, ordered by id (ordinal), with <see cref="ObjectDeclIr.IsRecursive"/> and
    /// <see cref="UnionDeclIr.IsRecursive"/> computed over them.
    /// </summary>
    public EquatableArray<TypeDeclIr> GetDecls()
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var decl in _decls.Values)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            switch (decl)
            {
                case ObjectDeclIr obj:
                    foreach (var m in obj.Members)
                        CollectDeclIds(m.Type, targets);
                    break;
                case UnionDeclIr union:
                    foreach (var c in union.Cases)
                        targets.Add(c.CaseDeclId);
                    break;
            }

            edges[decl.Id] = targets;
        }

        var result = new List<TypeDeclIr>(_decls.Count);
        foreach (var decl in _decls.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            result.Add(decl switch
            {
                ObjectDeclIr obj => obj with { IsRecursive = Reaches(obj.Id, edges) },
                UnionDeclIr union => union with { IsRecursive = Reaches(union.Id, edges) },
                _ => decl,
            });
        }

        return new EquatableArray<TypeDeclIr>(result.ToArray());
    }

    private static void CollectDeclIds(TypeRef type, HashSet<string> ids)
    {
        if (type.Kind == TypeRefKind.Decl && type.DeclId != null)
            ids.Add(type.DeclId);
        foreach (var arg in type.Args)
            CollectDeclIds(arg, ids);
    }

    // True when the declaration can reach itself through member and case types.
    private static bool Reaches(string id, Dictionary<string, HashSet<string>> edges)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        if (edges.TryGetValue(id, out var start))
        {
            foreach (var t in start)
                stack.Push(t);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == id)
                return true;
            if (!visited.Add(current) || !edges.TryGetValue(current, out var next))
                continue;
            foreach (var t in next)
                stack.Push(t);
        }

        return false;
    }

    private TypeRef Convert(TypeShape shape, ITypeSymbol? declared)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var refNullable = declared != null && ShapeBuilder.IsRefNullable(declared);
        var result = ConvertCore(shape, declared);
        return refNullable ? result with { RefNullable = true } : result;
    }

    private TypeRef ConvertCore(TypeShape shape, ITypeSymbol? declared)
    {
        switch (shape)
        {
            case TypeParameterShape p:
                return new TypeRef(TypeRefKind.TypeParam) { Ordinal = p.Ordinal };

            case NamedShape { Kind: NamedKind.UserFormatter } user:
                return External(user.MetadataName, ConvertArgs(user, declared));

            case NamedShape { Kind: NamedKind.Special } special:
            {
                var prim = PrimOf(special.Type);
                return prim == PrimKind.None
                    ? External(special.MetadataName, EquatableArray<TypeRef>.Empty)
                    : new TypeRef(TypeRefKind.Prim) { Prim = prim };
            }

            case EnumShape e:
                EnsureDecl(((INamedTypeSymbol)e.Type).OriginalDefinition);
                return new TypeRef(TypeRefKind.Decl) { DeclId = DeclId(e.Type) };

            case ArrayShape array:
            {
                if (array.Rank != 1)
                    return External(array.Type.ToDisplayString(), EquatableArray<TypeRef>.Empty);

                var element = array.Element.Type;
                if (element.SpecialType == SpecialType.System_Byte)
                    return new TypeRef(TypeRefKind.Bin) { Clr = "Array" };

                var elementPrim = PrimArrayElement(element);
                if (elementPrim != PrimKind.None)
                    return new TypeRef(TypeRefKind.PrimArray) { Prim = elementPrim, Clr = "Array" };

                var declaredElement = (declared as IArrayTypeSymbol)?.ElementType;
                return new TypeRef(TypeRefKind.Seq) { Clr = "Array", Args = One(Convert(array.Element, declaredElement)) };
            }

            case NullableShape nullable:
            {
                var declaredInner = (declared as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
                return new TypeRef(TypeRefKind.Nullable) { Args = One(Convert(nullable.Inner, declaredInner)) };
            }

            case NamedShape { Kind: NamedKind.BuiltInGeneric } generic:
                return ConvertBuiltInGeneric(generic, declared);

            case NamedShape leaf:
            {
                var prim = PrimOf(leaf.Type);
                return prim == PrimKind.None
                    ? External(leaf.MetadataName, ConvertArgs(leaf, declared))
                    : new TypeRef(TypeRefKind.Prim) { Prim = prim };
            }

            case ObjectShape or UnionShape:
            {
                var named = (INamedTypeSymbol)shape.Type;
                EnsureDecl(named.OriginalDefinition);
                var args = EquatableArray<TypeRef>.Empty;
                if (named.IsGenericType)
                {
                    var declaredArgs = (declared as INamedTypeSymbol)?.TypeArguments;
                    var list = new TypeRef[named.TypeArguments.Length];
                    for (var i = 0; i < list.Length; i++)
                        list[i] = Convert(_shapes.Get(named.TypeArguments[i]), declaredArgs?[i] ?? named.TypeArguments[i]);
                    args = new EquatableArray<TypeRef>(list);
                }

                return new TypeRef(TypeRefKind.Decl) { DeclId = DeclId(named), Args = args };
            }
        }

        return External(shape.Type.ToDisplayString(), EquatableArray<TypeRef>.Empty);
    }

    private TypeRef ConvertBuiltInGeneric(NamedShape shape, ITypeSymbol? declared)
    {
        var named = (INamedTypeSymbol)shape.Type;
        var definition = shape.MetadataName;
        var clr = ClrContainerName(definition);
        var first = named.TypeArguments[0];

        switch (definition)
        {
            case "System.Collections.Generic.List`1":
            {
                var prim = PrimArrayElement(first);
                if (prim != PrimKind.None)
                    return new TypeRef(TypeRefKind.PrimArray) { Prim = prim, Clr = clr };
                return new TypeRef(TypeRefKind.Seq) { Clr = clr, Args = ConvertArgs(shape, declared) };
            }

            case "System.Collections.Generic.IList`1":
            case "System.Collections.Generic.IReadOnlyList`1":
            case "System.Collections.Generic.ICollection`1":
            case "System.Collections.Generic.IReadOnlyCollection`1":
            case "System.Collections.Generic.IEnumerable`1":
                return new TypeRef(TypeRefKind.Seq) { Clr = clr, Args = ConvertArgs(shape, declared) };

            case "System.Collections.Generic.HashSet`1":
                return new TypeRef(TypeRefKind.Set) { Clr = clr, Args = ConvertArgs(shape, declared) };

            case "System.Collections.Generic.Queue`1":
                return new TypeRef(TypeRefKind.Queue) { Clr = clr, Args = ConvertArgs(shape, declared) };

            case "System.Collections.Generic.Stack`1":
                return new TypeRef(TypeRefKind.Stack) { Clr = clr, Args = ConvertArgs(shape, declared) };

            case "System.Collections.Generic.Dictionary`2":
            case "System.Collections.Generic.IDictionary`2":
            case "System.Collections.Generic.IReadOnlyDictionary`2":
                return new TypeRef(TypeRefKind.Map) { Clr = clr, Args = ConvertArgs(shape, declared) };

            case "System.Collections.Generic.KeyValuePair`2":
                return new TypeRef(TypeRefKind.Tuple) { Clr = "KeyValuePair", Args = ConvertArgs(shape, declared) };

            case "System.Memory`1":
            case "System.ReadOnlyMemory`1":
            case "System.ArraySegment`1":
            case "System.Buffers.ReadOnlySequence`1":
            {
                if (first.SpecialType == SpecialType.System_Byte)
                    return new TypeRef(TypeRefKind.Bin) { Clr = clr };

                var prim = PrimArrayElement(first);
                if (prim != PrimKind.None && clr is "Memory" or "ReadOnlyMemory")
                    return new TypeRef(TypeRefKind.PrimArray) { Prim = prim, Clr = clr };

                return External(definition, ConvertArgs(shape, declared));
            }
        }

        if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
            return new TypeRef(TypeRefKind.Tuple) { Clr = "ValueTuple", Args = ConvertArgs(shape, declared) };

        if (definition.StartsWith("System.Tuple`", StringComparison.Ordinal))
            return new TypeRef(TypeRefKind.Tuple) { Clr = "Tuple", Args = ConvertArgs(shape, declared) };

        return External(definition, ConvertArgs(shape, declared));
    }

    private EquatableArray<TypeRef> ConvertArgs(NamedShape shape, ITypeSymbol? declared)
    {
        if (shape.Arguments.Length == 0)
            return EquatableArray<TypeRef>.Empty;

        var declaredArgs = (declared as INamedTypeSymbol)?.TypeArguments;
        var result = new TypeRef[shape.Arguments.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var declaredArg = declaredArgs is { } d && i < d.Length ? d[i] : null;
            result[i] = Convert(shape.Arguments[i], declaredArg);
        }

        return new EquatableArray<TypeRef>(result);
    }

    private static TypeRef External(string clrName, EquatableArray<TypeRef> args)
        => new(TypeRefKind.External) { Clr = clrName, Args = args };

    private static EquatableArray<TypeRef> One(TypeRef type) => new(new[] { type });

    // "System.Collections.Generic.IReadOnlyList`1" -> "IReadOnlyList"
    private static string ClrContainerName(string metadataName)
    {
        var name = metadataName.Substring(metadataName.LastIndexOf('.') + 1);
        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    internal static string DeclId(ITypeSymbol type) => SerializationBuilder.MetadataFullName(type.OriginalDefinition);

    /// <summary>The leaf kind of a built-in type, or <see cref="PrimKind.None"/>.</summary>
    internal static PrimKind PrimOf(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return PrimKind.Bool;
            case SpecialType.System_SByte: return PrimKind.SByte;
            case SpecialType.System_Byte: return PrimKind.Byte;
            case SpecialType.System_Int16: return PrimKind.Int16;
            case SpecialType.System_UInt16: return PrimKind.UInt16;
            case SpecialType.System_Int32: return PrimKind.Int32;
            case SpecialType.System_UInt32: return PrimKind.UInt32;
            case SpecialType.System_Int64: return PrimKind.Int64;
            case SpecialType.System_UInt64: return PrimKind.UInt64;
            case SpecialType.System_Single: return PrimKind.Single;
            case SpecialType.System_Double: return PrimKind.Double;
            case SpecialType.System_Char: return PrimKind.Char;
            case SpecialType.System_Decimal: return PrimKind.Decimal;
            case SpecialType.System_String: return PrimKind.String;
            case SpecialType.System_DateTime: return PrimKind.DateTime;
        }

        return SerializationBuilder.MetadataFullName(type) switch
        {
            "System.Half" => PrimKind.Half,
            "System.DateTimeOffset" => PrimKind.DateTimeOffset,
            "System.TimeSpan" => PrimKind.TimeSpan,
            "System.DateOnly" => PrimKind.DateOnly,
            "System.TimeOnly" => PrimKind.TimeOnly,
            "System.Guid" => PrimKind.Guid,
            "System.Numerics.BigInteger" => PrimKind.BigInteger,
            "System.Uri" => PrimKind.Uri,
            "System.Version" => PrimKind.Version,
            _ => PrimKind.None,
        };
    }

    /// <summary>The element kind when <paramref name="type"/> is written as an ext 78 primitive array element.</summary>
    private static PrimKind PrimArrayElement(ITypeSymbol type)
    {
        var prim = PrimOf(type);
        return prim switch
        {
            PrimKind.SByte or PrimKind.Int16 or PrimKind.UInt16 or PrimKind.Int32 or PrimKind.UInt32 or PrimKind.Int64
                or PrimKind.UInt64 or PrimKind.Single or PrimKind.Double or PrimKind.Char or PrimKind.Half => prim,
            _ => PrimKind.None,
        };
    }

    /// <summary>Adds the declaration of a [NexusObject] type or enum definition (and everything it reaches).</summary>
    internal void EnsureDecl(INamedTypeSymbol definition)
    {
        var id = DeclId(definition);
        if (_decls.ContainsKey(id) || !_inProgress.Add(id))
            return;

        var decl = CreateDecl(definition, id);
        if (decl != null)
            _decls[id] = decl;
        _inProgress.Remove(id);
    }

    private TypeDeclIr? CreateDecl(INamedTypeSymbol definition, string id)
    {
        var ns = definition.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";
        var containing = new List<string>();
        for (var t = definition.ContainingType; t != null; t = t.ContainingType)
            containing.Insert(0, t.Name);
        var containingTypes = new EquatableArray<string>(containing.ToArray());
        var docs = IrDocs.FromSymbol(definition, _cancellationToken);
        var typeParams = new EquatableArray<string>(definition.TypeParameters.Select(p => p.Name).ToArray());

        switch (_shapes.Get(definition))
        {
            case EnumShape e:
                return new EnumDeclIr(id, id, ns, definition.Name, containingTypes, docs,
                    PrimOf(definition.EnumUnderlyingType ?? definition),
                    e.IsFlags,
                    new EquatableArray<EnumMemberIr>(e.Members.Select(m => new EnumMemberIr(m.Name, m.Value)).ToArray()));

            case UnionShape union:
            {
                var cases = new List<UnionCaseIr>();
                foreach (var (tag, caseShape) in union.Cases)
                {
                    if (caseShape.Type is not INamedTypeSymbol caseType)
                        continue;
                    EnsureDecl(caseType.OriginalDefinition);
                    cases.Add(new UnionCaseIr(tag, DeclId(caseType), InheritanceDepth(caseType)));
                }

                return new UnionDeclIr(id, id, ns, definition.Name, containingTypes, docs,
                    definition.TypeKind == TypeKind.Interface, typeParams, IsRecursive: false,
                    new EquatableArray<UnionCaseIr>(cases.ToArray()));
            }

            case ObjectShape obj:
            {
                string? baseDeclId = null;
                if (definition.BaseType is { } baseType && SerializationBuilder.HasAttribute(baseType, "NexusObjectAttribute"))
                {
                    EnsureDecl(baseType.OriginalDefinition);
                    baseDeclId = DeclId(baseType);
                }

                var members = new List<MemberIr>(obj.Members.Count);
                foreach (var m in obj.Members)
                {
                    var type = IrJsNumbers.MarkIf(Convert(m.Type, m.DeclaredType), IrJsNumbers.HasMember(m.Symbol))!;
                    members.Add(new MemberIr(
                        m.Name,
                        m.Key,
                        type,
                        type.RefNullable,
                        m.Symbol.DeclaredAccessibility != Accessibility.Public,
                        m.DefaultLiteral,
                        IrDocs.FromSymbol(m.Symbol, _cancellationToken)));
                }

                var arrayLength = obj.Members.Count == 0 ? 0 : obj.Members[obj.Members.Count - 1].Key + 1;
                return new ObjectDeclIr(id, id, ns, definition.Name, containingTypes, docs,
                    IsStruct: definition.IsValueType,
                    IsRecord: definition.IsRecord,
                    IsAbstract: obj.IsAbstractOrInterface,
                    typeParams,
                    baseDeclId,
                    arrayLength,
                    IsRecursive: false,
                    new EquatableArray<MemberIr>(members.ToArray()));
            }
        }

        return null;
    }

    // Same depth as the C# union formatter uses to order its write switch.
    private static int InheritanceDepth(ITypeSymbol type)
    {
        var depth = 0;
        for (var t = type.BaseType; t != null; t = t.BaseType)
            depth++;
        return depth;
    }
}
