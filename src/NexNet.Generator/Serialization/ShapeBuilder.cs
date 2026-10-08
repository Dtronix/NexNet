using Microsoft.CodeAnalysis;
using NexNet.Generator.Models;

namespace NexNet.Generator.Serialization;

/// <summary>
/// Turns type symbols into <see cref="TypeShape"/>s: the single definition of how a type is serialized. One instance
/// per producer; code generation and hashing both read the shapes it builds, so they always agree.
/// </summary>
internal sealed class ShapeBuilder
{
    private readonly Compilation _compilation;
    private readonly Dictionary<ITypeSymbol, TypeShape> _cache = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, string> _userFormatters = new(SymbolEqualityComparer.Default);

    public ShapeBuilder(Compilation compilation)
    {
        _compilation = compilation;
        CollectUserFormatters();
    }

    /// <summary>
    /// Types with a user formatter registered by <c>[assembly: NexusFormatter&lt;TFormatter, T&gt;]</c>, mapped to the
    /// formatter's type name.
    /// </summary>
    public IReadOnlyDictionary<ITypeSymbol, string> UserFormatters => _userFormatters;

    /// <summary>
    /// Returns the shape of <paramref name="type"/>. Nullable reference annotations are ignored at every level, so
    /// <c>Foo</c> and <c>Foo?</c> return the same instance.
    /// </summary>
    public TypeShape Get(ITypeSymbol type)
    {
        var normalized = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (_cache.TryGetValue(normalized, out var shape))
            return shape;

        shape = Create(normalized);
        _cache[normalized] = shape; // before populating: members may point back here
        Populate(shape);
        return shape;
    }

    private void CollectUserFormatters()
    {
        foreach (var attr in _compilation.Assembly.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is not { IsGenericType: true })
                continue;

            if (cls.Name == "NexusFormatterAttribute" && cls.TypeArguments.Length == 2
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization")
            {
                _userFormatters[cls.TypeArguments[1]] = SerializationBuilder.TypeName(cls.TypeArguments[0]);
            }
        }
    }

    private TypeShape Create(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol typeParameter)
            return new TypeParameterShape(typeParameter);

        if (_userFormatters.ContainsKey(type))
            return new NamedShape(type, NamedKind.UserFormatter, SerializationBuilder.MetadataFullName(type.OriginalDefinition));

        if (IsSpecial(type.SpecialType))
            return new NamedShape(type, NamedKind.Special, SerializationBuilder.MetadataFullName(type));

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
            return CreateEnum(enumType);

        if (type is IArrayTypeSymbol array)
            return new ArrayShape(array);

        if (type is not INamedTypeSymbol named)
            return new NamedShape(type, NamedKind.Unsupported, SerializationBuilder.MetadataFullName(type));

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return new NullableShape(named);

        if (SerializationBuilder.HasAttribute(named, "NexusObjectAttribute"))
        {
            return HasUnionCases(named)
                ? new UnionShape(named)
                : new ObjectShape(named);
        }

        var metadataName = SerializationBuilder.MetadataFullName(named.OriginalDefinition);
        if (named.IsGenericType && SerializationBuilder.BuiltInGenericDefinitions.Contains(metadataName))
            return new NamedShape(named, NamedKind.BuiltInGeneric, metadataName);

        return new NamedShape(named, IsSystemNamespace(named) ? NamedKind.Clr : NamedKind.Unsupported, metadataName);
    }

    private void Populate(TypeShape shape)
    {
        switch (shape)
        {
            case ArrayShape array:
                array.Element = Get(((IArrayTypeSymbol)array.Type).ElementType);
                break;

            case NullableShape nullable:
                nullable.Inner = Get(((INamedTypeSymbol)nullable.Type).TypeArguments[0]);
                break;

            case NamedShape { Type: INamedTypeSymbol { IsGenericType: true } named } namedShape:
                namedShape.Arguments = named.TypeArguments.Select(Get).ToArray();
                break;

            case UnionShape union:
                foreach (var (tag, caseType) in GetUnionCases((INamedTypeSymbol)union.Type))
                    union.Cases.Add((tag, Get(caseType)));
                break;

            case ObjectShape obj:
                PopulateMembers(obj);
                break;
        }
    }

    private static bool IsSpecial(SpecialType specialType)
    {
        switch (specialType)
        {
            case SpecialType.System_Object:
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }

        return false;
    }

    private static EnumShape CreateEnum(INamedTypeSymbol type)
    {
        var values = new SortedSet<long>();
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: { } value })
                values.Add(value is ulong u ? unchecked((long)u) : Convert.ToInt64(value));
        }

        var underlying = type.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32;
        return new EnumShape(type, underlying, values.ToArray());
    }

    private static bool IsSystemNamespace(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
            return false;

        while (ns.ContainingNamespace is { IsGlobalNamespace: false })
            ns = ns.ContainingNamespace;

        return ns.Name == "System";
    }

    private static bool HasUnionCases(INamedTypeSymbol type) => GetUnionCases(type).Count > 0;

    private static List<(ushort Tag, ITypeSymbol Type)> GetUnionCases(INamedTypeSymbol type)
    {
        var cases = new List<(ushort, ITypeSymbol)>();
        foreach (var attr in type.OriginalDefinition.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is { IsGenericType: true, Name: "NexusUnionAttribute" } && cls.TypeArguments.Length == 1
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization"
                && attr.ConstructorArguments.Length > 0)
            {
                var tagValue = attr.ConstructorArguments[0].Value;
                var tag = tagValue is ushort us ? us : Convert.ToUInt16(tagValue);
                cases.Add((tag, cls.TypeArguments[0]));
            }
        }

        return cases;
    }

    private bool IsAccessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);

    private void PopulateMembers(ObjectShape shape)
    {
        var type = (INamedTypeSymbol)shape.Type;
        var problems = shape.Problems;
        var result = shape.Members;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (INamedTypeSymbol? current = type; current != null && current.SpecialType != SpecialType.System_Object
             && current.SpecialType != SpecialType.System_ValueType; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic)
                    continue;

                ITypeSymbol? memberType;
                bool isField;
                if (member is IPropertySymbol prop && !prop.IsIndexer)
                {
                    memberType = prop.Type;
                    isField = false;
                }
                else if (member is IFieldSymbol { AssociatedSymbol: null } field)
                {
                    memberType = field.Type;
                    isField = true;
                }
                else
                {
                    continue;
                }

                if (!seenNames.Add(member.Name))
                    continue; // Overridden/hidden members are handled once.

                var keyAttr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "NexusKeyAttribute"
                    && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization");
                var ignored = SerializationBuilder.HasAttribute(member, "NexusIgnoreAttribute");

                if (keyAttr == null)
                {
                    if (!ignored && !member.IsImplicitlyDeclared && member.DeclaredAccessibility == Accessibility.Public && IsSerializableShape(member))
                        problems.Add(new SerializationDiagnostic("NEXNET030", SerializationBuilder.TypeName(type), member.Name, LocationData.FromSymbol(member)));
                    continue;
                }

                if (ignored)
                    continue;

                var km = new MemberShape
                {
                    Symbol = member,
                    Name = member.Name,
                    DeclaredType = memberType,
                    Key = keyAttr.ConstructorArguments.Length > 0 && keyAttr.ConstructorArguments[0].Value is int k ? k : 0,
                    IsField = isField,
                };

                if (member is IPropertySymbol p)
                {
                    km.CanGet = p.GetMethod != null && IsAccessible(p.GetMethod);
                    km.IsInitOnly = p.SetMethod?.IsInitOnly == true;
                    km.CanSet = p.SetMethod != null && IsAccessible(p.SetMethod);
                    km.IsRequired = p.IsRequired;
                    km.BackingField = p.ContainingType.GetMembers().OfType<IFieldSymbol>()
                        .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p));
                }
                else
                {
                    var f = (IFieldSymbol)member;
                    var accessible = IsAccessible(f);
                    km.CanGet = accessible;
                    km.CanSet = accessible && !f.IsReadOnly && !f.IsConst;
                    km.IsRequired = f.IsRequired;
                }

                result.Add(km);
            }
        }

        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        for (var i = 0; i < result.Count; i++)
            result[i].LocalName = "__m" + i;

        // Member shapes last: this object is already in the cache, so a member that points back here terminates.
        foreach (var m in result)
            m.Type = Get(m.DeclaredType);
    }

    private static bool IsSerializableShape(ISymbol member)
    {
        // Computed get-only properties are not data and do not need a key.
        if (member is IPropertySymbol p)
        {
            if (p.SetMethod != null)
                return true;
            return p.ContainingType.GetMembers().OfType<IFieldSymbol>()
                .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p));
        }

        return member is IFieldSymbol { IsConst: false };
    }
}
