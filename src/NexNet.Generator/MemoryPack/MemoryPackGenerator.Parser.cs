using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator.MemoryPack;

internal enum CollectionKind
{
    None, Collection, Set, Dictionary
}

internal enum MemberKind
{
    MemoryPackable, // IMemoryPackable<> or [MemoryPackable]
    Unmanaged,
    Nullable,
    UnmanagedNullable,
    KnownType,
    String,
    Array,
    UnmanagedArray,
    MemoryPackableArray, // T[] where T: IMemoryPackable<T>
    MemoryPackableList, // List<T> where T: IMemoryPackable<T>
    MemoryPackableCollection, // GenerateType.Collection
    MemoryPackableNoGenerate, // GenerateType.NoGenerate
    MemoryPackableUnion,
    Enum,

    // from attribute
    AllowSerialize,
    MemoryPackUnion,

    Object, // others allow
    RefLike, // not allowed
    NonSerializable, // not allowed
    Blank, // blank marker
    CustomFormatter, // used [MemoryPackCustomFormatterAttribtue]
}

internal class TypeMeta
{
    private readonly ReferenceSymbols _reference;
    private static readonly XxHash32 _hash = new XxHash32();
    private int? _nexusHash = null;
    public INamedTypeSymbol Symbol { get; }
    public GenerateType GenerateType { get; }
    //public SerializeLayout SerializeLayout { get; }
    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public MemberMeta[] Members { get; }
    public bool IsValueType { get; }
    public bool IsUnmanagedType { get; }
    public bool IsUnion { get; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    public (ushort Tag, TypeMeta Type)[] UnionTags { get; }

    public TypeMeta(INamedTypeSymbol symbol, ReferenceSymbols reference)
    {
        _reference = reference;
        this.Symbol = symbol;

        symbol.TryGetMemoryPackableType(reference, out var generateType, out _);
        this.GenerateType = generateType;
        //this.SerializeLayout = serializeLayout;

        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        this.Members = symbol.GetAllMembers() // iterate includes parent type
            .Where(x => x is (IFieldSymbol or IPropertySymbol) and { IsStatic: false, IsImplicitlyDeclared: false, CanBeReferencedByName: true })
            .Reverse()
            .DistinctBy(x => x.Name) // remove duplicate name(new)
            .Reverse()
            .Where(x =>
            {
                var include = x.ContainsAttribute(reference.MemoryPackIncludeAttribute);
                var ignore = x.ContainsAttribute(reference.MemoryPackIgnoreAttribute);
                if (ignore) return false;
                if (include) return true;
                return x.DeclaredAccessibility is Accessibility.Public;
            })
            .Where(x =>
            {
                if (x is IPropertySymbol p)
                {
                    // set only can't be serializable member
                    if (p.GetMethod == null && p.SetMethod != null)
                    {
                        return false;
                    }
                    if (p.IsIndexer) return false;
                }
                return true;
            })
            .Select((x, i) => new MemberMeta(x, reference, i))
            .OrderBy(x => x.Order)
            .ToArray();

        this.IsValueType = symbol.IsValueType;
        this.IsUnmanagedType = symbol.IsUnmanagedType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsUnion = symbol.ContainsAttribute(reference.MemoryPackUnionAttribute);
        this.IsRecord = symbol.IsRecord;

        if (IsUnion)
        {
            this.UnionTags = symbol.GetAttributes()
                .Where(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, reference.MemoryPackUnionAttribute))
                .Where(x => x.ConstructorArguments.Length == 2)
                .Select(x => ((ushort)x.ConstructorArguments[0].Value!, new TypeMeta((INamedTypeSymbol)x.ConstructorArguments[1].Value!, reference)))
                .ToArray();
        }
        else
        {
            this.UnionTags = [];
        }
    }

    public static (CollectionKind, INamedTypeSymbol?) ParseCollectionKind(INamedTypeSymbol? symbol, ReferenceSymbols reference)
    {
        if (symbol == null) goto NONE;

        INamedTypeSymbol? dictionary = default;
        INamedTypeSymbol? set = default;
        INamedTypeSymbol? collection = default;
        foreach (var item in symbol.AllInterfaces)
        {
            if (item.EqualsUnconstructedGenericType(reference.KnownTypes.System_Collections_Generic_IDictionary_T))
            {
                dictionary = item;
            }
            else if (item.EqualsUnconstructedGenericType(reference.KnownTypes.System_Collections_Generic_ISet_T))
            {
                set = item;
            }
            else if (item.EqualsUnconstructedGenericType(reference.KnownTypes.System_Collections_Generic_ICollection_T))
            {
                collection = item;
            }
        }

        if (dictionary != null)
        {
            return (CollectionKind.Dictionary, dictionary);
        }
        if (set != null)
        {
            return (CollectionKind.Set, set);
        }
        if (collection != null)
        {
            return (CollectionKind.Collection, collection);
        }
        NONE:
        return (CollectionKind.None, null);
    }
    
    public override string ToString()
    {
        return this.TypeName;
    }

    public int GetNexusHash()
    {
        if(_nexusHash != null)
            return _nexusHash.Value;
        
        var hash = new HashCode();
        if (IsUnmanagedType)
        {
            hash.Add(10000);
        }
        else
        {
            hash.Add((int)GenerateType);
            if (GenerateType == GenerateType.Collection)
            {
                var kind = ParseCollectionKind(Symbol, _reference);
                hash.Add((int)kind.Item1);
            }
        }

        hash.Add(IsValueType ? 1 : 0);
        hash.Add(IsRecord ? 1 : 0);
        hash.Add(IsInterfaceOrAbstract ? 1 : 0);
        foreach (var item in Members)
        {
            // Order + Type.
            // We ignore the name as the name could be different, but produce the same binary data.
            hash.Add(item.Order);
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(item.MemberType.ToString())));
        }

        foreach (var union in UnionTags)
        {
            hash.Add(union.Tag);
            hash.Add(union.Type.GetNexusHash());
        }

        return hash.ToHashCode();
    }

}

internal class MemberMeta
{
    public ISymbol Symbol { get; }
    //public string Name { get; }
    public ITypeSymbol MemberType { get; }
    //public INamedTypeSymbol? CustomFormatter { get; }
    //public string? CustomFormatterName { get; }
    //public bool IsField { get; }
    //public bool IsProperty { get; }
    //public bool IsSettable { get; }
    //public bool IsAssignable { get; }
    public int Order { get; }
    //public bool HasExplicitOrder { get; }
    //public MemberKind Kind { get; }
    //public bool SuppressDefaultInitialization { get; }

    MemberMeta(int order)
    {
        this.Symbol = null!;
        //this.Name = null!;
        this.MemberType = null!;
        this.Order = order;
        //this.Kind = MemberKind.Blank;
    }

    public MemberMeta(ISymbol symbol, ReferenceSymbols references, int sequentialOrder)
    {
        this.Symbol = symbol;
        //this.Name = symbol.Name;
        this.Order = sequentialOrder;
        //this.SuppressDefaultInitialization = symbol.ContainsAttribute(references.SkipOverwriteDefaultAttribute);
        var orderAttr = symbol.GetAttribute(references.MemoryPackOrderAttribute);
        if (orderAttr != null)
        {
            this.Order = (int)(orderAttr.ConstructorArguments[0].Value ?? sequentialOrder);
            //this.HasExplicitOrder = true;

        }
        else
        {
            //this.HasExplicitOrder = false;
        }

        if (symbol is IFieldSymbol f)
        {
            //IsProperty = false;
            //IsField = true;
            //IsSettable = !f.IsReadOnly; // readonly field can not set.
            //IsAssignable = IsSettable && !f.IsRequired
            MemberType = f.Type;
        }
        else if (symbol is IPropertySymbol p)
        {
            //IsProperty = true;
            //IsField = false;
            //IsSettable = !p.IsReadOnly;
            //IsAssignable = IsSettable && !p.IsRequired && (p.SetMethod != null && !p.SetMethod.IsInitOnly);
            MemberType = p.Type;
        }
        else
        {
            throw new Exception("member is not field or property.");
        }
        /*
        if (references.MemoryPackCustomFormatterAttribute != null)
        {
            var genericFormatter = false;
            var customFormatterAttr = symbol.GetImplAttribute(references.MemoryPackCustomFormatterAttribute);
            if (customFormatterAttr == null && references.MemoryPackCustomFormatter2Attribute != null)
            {
                customFormatterAttr = symbol.GetImplAttribute(references.MemoryPackCustomFormatter2Attribute);
                genericFormatter = true;
            }

            if (customFormatterAttr != null)
            {
                CustomFormatter = customFormatterAttr.AttributeClass!;
                //Kind = MemberKind.CustomFormatter;

                string formatterName;
                if (genericFormatter)
                {
                    formatterName = CustomFormatter.GetAllBaseTypes().First(x => x.EqualsUnconstructedGenericType(references.MemoryPackCustomFormatter2Attribute!))
                        .TypeArguments[0].FullyQualifiedToString();
                }
                else
                {
                    formatterName = $"IMemoryPackFormatter<{MemberType.FullyQualifiedToString()}>";
                }
                //CustomFormatterName = formatterName;
                return;
            }
        }
        */
        //Kind = ParseMemberKind(symbol, MemberType, references);
    }

    public static MemberMeta CreateEmpty(int order)
    {
        return new MemberMeta(order);
    }

    public Location GetLocation(TypeDeclarationSyntax fallback)
    {
        var location = Symbol.Locations.FirstOrDefault() ?? fallback.Identifier.GetLocation();
        return location;
    }
    /*
    static MemberKind ParseMemberKind(ISymbol? memberSymbol, ITypeSymbol memberType, ReferenceSymbols references)
    {
        if (memberType.SpecialType is SpecialType.System_Object or SpecialType.System_Array or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate || memberType.TypeKind == TypeKind.Delegate)
        {
            return MemberKind.NonSerializable; // object, Array, delegate is not allowed
        }
        else if (memberType.TypeKind == TypeKind.Enum)
        {
            return MemberKind.Enum;
        }
        else if (memberType.IsUnmanagedType)
        {
            if (memberType is INamedTypeSymbol unmanagedNts)
            {
                if (unmanagedNts.IsRefLikeType)
                {
                    return MemberKind.RefLike;
                }
                if (unmanagedNts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
                {
                    // unamanged nullable<T> can not pass to where T:unmanaged constraint
                    if (unmanagedNts.TypeArguments[0].IsUnmanagedType)
                    {
                        return MemberKind.UnmanagedNullable;
                    }
                    else
                    {
                        return MemberKind.Nullable;
                    }
                }
            }

            return MemberKind.Unmanaged;
        }
        else if (memberType.SpecialType == SpecialType.System_String)
        {
            return MemberKind.String;
        }
        else if (memberType.AllInterfaces.Any(x => x.EqualsUnconstructedGenericType(references.IMemoryPackable)))
        {
            return MemberKind.MemoryPackable;
        }
        else if (memberType.TryGetMemoryPackableType(references, out var generateType, out _))
        {
            switch (generateType)
            {
                case GenerateType.Object:
                case GenerateType.VersionTolerant:
                case GenerateType.CircularReference:
                    return MemberKind.MemoryPackable;
                case GenerateType.Collection:
                    return MemberKind.MemoryPackableCollection;
                case GenerateType.Union:
                    return MemberKind.MemoryPackableUnion;
                case GenerateType.NoGenerate:
                default:
                    return MemberKind.MemoryPackableNoGenerate;
            }
        }
        else if (memberType.IsWillImplementMemoryPackUnion(references))
        {
            return MemberKind.MemoryPackUnion;
        }
        else if (memberType.TypeKind == TypeKind.Array)
        {
            if (memberType is IArrayTypeSymbol array)
            {
                if (array.IsSZArray)
                {
                    var elemType = array.ElementType;
                    if (elemType.IsUnmanagedType)
                    {
                        if (elemType is INamedTypeSymbol unmanagedNts && unmanagedNts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
                        {
                            // T?[] can not use Write/ReadUnmanagedArray
                            return MemberKind.Array;
                        }
                        else
                        {
                            return MemberKind.UnmanagedArray;
                        }
                    }
                    else
                    {
                        if (elemType.TryGetMemoryPackableType(references, out var elemGenerateType, out _) && elemGenerateType is GenerateType.Object or GenerateType.VersionTolerant or GenerateType.CircularReference)
                        {
                            return MemberKind.MemoryPackableArray;
                        }

                        return MemberKind.Array;
                    }
                }
                else
                {
                    // allows 2, 3, 4
                    if (array.Rank <= 4)
                    {
                        return MemberKind.Object;
                    }
                }
            }

            return MemberKind.NonSerializable;
        }
        else if (memberType.TypeKind == TypeKind.TypeParameter) // T
        {
            return MemberKind.Object;
        }
        else
        {
            // or non unmanaged type
            if (memberType is INamedTypeSymbol nts)
            {
                if (nts.IsRefLikeType)
                {
                    return MemberKind.RefLike;
                }
                if (nts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
                {
                    return MemberKind.Nullable;
                }

                if (nts.EqualsUnconstructedGenericType(references.KnownTypes.System_Collections_Generic_List_T))
                {
                    if (nts.TypeArguments[0].TryGetMemoryPackableType(references, out var elemGenerateType, out _) && elemGenerateType is GenerateType.Object or GenerateType.VersionTolerant or GenerateType.CircularReference)
                    {
                        return MemberKind.MemoryPackableList;
                    }
                    return MemberKind.KnownType;
                }
            }

            if (references.KnownTypes.Contains(memberType))
            {
                return MemberKind.KnownType;
            }

            if (memberSymbol != null)
            {
                if (memberSymbol.ContainsAttribute(references.MemoryPackAllowSerializeAttribute))
                {
                    return MemberKind.AllowSerialize;
                }
            }

            return MemberKind.NonSerializable; // maybe can't serialize, diagnostics target
        }
    }*/
}
