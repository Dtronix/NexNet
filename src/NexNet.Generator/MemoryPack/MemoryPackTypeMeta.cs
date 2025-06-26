using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MemoryPack;

internal class MemoryPackTypeMeta
{
    private readonly MemoryPackReferences _reference;
    private static readonly XxHash32 _hash = new XxHash32();
    private int? _nexusHash = null;
    public INamedTypeSymbol Symbol { get; }
    public GenerateType GenerateType { get; }
    //public SerializeLayout SerializeLayout { get; }
    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public MemoryPackMemberMeta[] Members { get; }
    public bool IsValueType { get; }
    public bool IsUnmanagedType { get; }
    public bool IsUnion { get; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    public (ushort Tag, MemoryPackTypeMeta Type)[] UnionTags { get; }

    public MemoryPackTypeMeta(INamedTypeSymbol symbol, MemoryPackReferences reference)
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
            .Select((x, i) => new MemoryPackMemberMeta(x, reference, i))
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
                .Select(x => ((ushort)x.ConstructorArguments[0].Value!, reference.GetOrCreateType((INamedTypeSymbol)x.ConstructorArguments[1].Value!)))
                .ToArray();
        }
        else
        {
            this.UnionTags = [];
        }
    }

    public static (CollectionKind, INamedTypeSymbol?) ParseCollectionKind(INamedTypeSymbol? symbol, MemoryPackReferences reference)
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
