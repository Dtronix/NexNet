using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using NexNet.Generator.MemoryPack;

namespace NexNet.Generator.MetaGenerator;

internal partial class InvocationInterfaceMeta
{
    private readonly HashSet<ushort> _usedIds = new HashSet<ushort>();
    private int? _hashCode;
    public INamedTypeSymbol Symbol { get; }
    public NexusAttributeMeta NexusAttribute { get; }
    public ReferenceSymbols MemoryPackReference { get; }
    public InvocationInterfaceMeta RootInterface { get; }
    
    /// <summary>
    /// Null on every interface except the root.
    /// </summary>
    public FrozenDictionary<IMethodSymbol, MethodMeta>? MethodTable { get; private set; }
    
    /// <summary>
    /// Null on every interface except the root.
    /// </summary>
    public FrozenDictionary<IPropertySymbol, CollectionMeta>? CollectionsTable { get; private set;}

    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public bool IsValueType { get; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    
    /// <summary>
    /// Collections are only the methods this interface directly specifies.
    /// </summary>
    public MethodMeta[] Methods { get; } = Array.Empty<MethodMeta>();

    /// <summary>
    /// AllCollections are all the methods this interface directly and indirectly implements.
    /// </summary>
    public MethodMeta[]? AllMethods { get; }

    public NexusVersionAttributeMeta VersionAttribute { get; }
    
    /// <summary>
    /// Collections are only the collection objects this interface directly specifies.
    /// </summary>
    public CollectionMeta[] Collections { get; }

    /// <summary>
    /// AllCollections are all the collection objects this interface directly and indirectly implements.
    /// </summary>
    public CollectionMeta[]? AllCollections { get; }

    public string ProxyImplName { get; }
    public string ProxyImplNameWithNamespace { get; }
    public string Namespace { get; }
    public string NamespaceName { get; }

    public InvocationInterfaceMeta[] Interfaces { get; private set; } = Array.Empty<InvocationInterfaceMeta>();
    
    public InvocationInterfaceMeta[] Versions { get; private set; } = Array.Empty<InvocationInterfaceMeta>();

    //public bool AlreadyGeneratedHash { get; }

    public InvocationInterfaceMeta(INamedTypeSymbol? symbol,
        NexusAttributeMeta attribute,
        InvocationInterfaceMeta? rootInterface,
        ReferenceSymbols memoryPackReference)
    {
        if (symbol == null)
            throw new ArgumentNullException(nameof(symbol));

        this.Symbol = symbol;
        this.NexusAttribute = attribute;
        this.MemoryPackReference = memoryPackReference;
        this.RootInterface = rootInterface ?? this;
        this.Namespace = symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.NamespaceName = symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "");
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;

        // If the root interface is null, then this is the root interface.

        var methods = EnumMethods(Symbol.GetMembers(), rootInterface, memoryPackReference).ToList();

        foreach (var interf in this.Symbol.AllInterfaces)
            methods.AddRange(EnumMethods(interf.GetMembers(), rootInterface, memoryPackReference));

        this.AllMethods = methods.ToArray();

        var collections = EnumCollections(Symbol.GetMembers(), rootInterface).ToList();
        this.Collections = collections.ToArray();

        foreach (var interf in this.Symbol.AllInterfaces)
            collections.AddRange(EnumCollections(interf.GetMembers(), rootInterface));

        this.AllCollections = collections.ToArray();

        if (rootInterface == null)
        {
            this.MethodTable = this.AllMethods.ToFrozenDictionary(meta => meta.Symbol, meta => meta);
            this.CollectionsTable = this.Collections.ToFrozenDictionary(meta => meta.Symbol, meta => meta);
        }

        VersionAttribute = new NexusVersionAttributeMeta(symbol);

        this.ProxyImplName = attribute.IsClient ? $"ServerProxy" : "ClientProxy";
        this.ProxyImplNameWithNamespace = $"{Namespace}.{ProxyImplName}";

        static IEnumerable<MethodMeta> EnumMethods(
            IEnumerable<ISymbol> symbols,
            InvocationInterfaceMeta? rootInterface,
            ReferenceSymbols memoryPackReference)
        {
            return symbols.OfType<IMethodSymbol>()
                .Where(x => x.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet))
                .Select(x => rootInterface == null ? new MethodMeta(x, memoryPackReference) : rootInterface.MethodTable![x])
                .Where(x => x.NexusMethodAttribute is { Ignore: false }); // Bypass ignored items.
        }

        static IEnumerable<CollectionMeta> EnumCollections(IEnumerable<ISymbol> symbols,
            InvocationInterfaceMeta? rootInterface)
        {
            return symbols.OfType<IPropertySymbol>()
                .Select(x => rootInterface == null ? new CollectionMeta(x) : rootInterface.CollectionsTable![x])
                .Where(x => x.NexusCollectionAttribute is { Ignore: false }); // Bypass ignored items.
        }
    }

    public void BuildVersions()
    {
        var interfaceMap = new Dictionary<INamedTypeSymbol, InvocationInterfaceMeta>(SymbolEqualityComparer.Default);
        var baseInterfaces = new List<InvocationInterfaceMeta>();
        var versions = new List<InvocationInterfaceMeta>();
        

        
        foreach (var interfaceSymbol in Symbol.AllInterfaces)
        {
            interfaceMap.Add(interfaceSymbol, new InvocationInterfaceMeta(interfaceSymbol, NexusAttribute, RootInterface, MemoryPackReference));
        }

        foreach (var interfaceMeta in interfaceMap)
        {
            baseInterfaces.Clear();
            var value = interfaceMeta.Value;
            if (!value.VersionAttribute.AttributeExists)
                continue;

            foreach (var interfaceSymbol in value.Symbol.AllInterfaces)
            {
                baseInterfaces.Add(interfaceMap[interfaceSymbol]);
            }

            value.Interfaces = baseInterfaces.ToArray();
            versions.Add(value);
        }
            
        // Add this interface if it has a version attribute
        if(VersionAttribute.AttributeExists)
            versions.Add(this);
        
        Versions = versions.ToArray();

        Interfaces = interfaceMap.Values.ToArray();
    }

    public void BuildMethodIds()
    {
        // Get all pre-defined method ids first.
        foreach (var methodMeta in AllMethods!)
        {
            if (methodMeta.NexusMethodAttribute.MethodId != null)
            {
                // Assign defined ids.
                methodMeta.Id = methodMeta.NexusMethodAttribute.MethodId.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _usedIds.Add(methodMeta.NexusMethodAttribute.MethodId.Value);
            }
        }
        
        foreach (var collectionMeta in AllCollections!)
        {
            if (collectionMeta.NexusCollectionAttribute.Id != null)
            {
                // Assign defined ids.
                collectionMeta.Id = collectionMeta.NexusCollectionAttribute.Id.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _usedIds.Add(collectionMeta.NexusCollectionAttribute.Id.Value);
            }
        }

        // Automatic id assignment.
        ushort id = 0;
        foreach (var methodMeta in AllMethods!)
        {
            if (methodMeta.Id == 0)
            {
                // Make sure we get an ID which is not used.
                while (_usedIds.Contains(id))
                    id++;

                methodMeta.Id = id++;
            }
        }
        
        // Automatic id assignment.
        foreach (var collectionMeta in AllCollections!)
        {
            if (collectionMeta.Id == 0)
            {
                // Make sure we get an ID which is not used.
                while (_usedIds.Contains(id))
                    id++;

                collectionMeta.Id = id++;
            }
        }
    }
    /*
    /// <summary>
    /// This will enumerate over all the methods in this interface and all implemented interfaces.
    /// </summary>
    private IEnumerable<MethodMeta> MethodEnumerator()
    {
        foreach (var m in Methods)
            yield return m;
        
        foreach (var child in Interfaces)
        {
            foreach (var m in child.Methods)
                yield return m;
        }
    }
    
    /// <summary>
    /// This will enumerate over all the methods in this interface and all implemented interfaces.
    /// </summary>
    private IEnumerable<CollectionMeta> CollectionEnumerator()
    {
        foreach (var m in Collections)
            yield return m;
        
        foreach (var child in Interfaces)
        {
            foreach (var m in child.Collections)
                yield return m;
        }
    }
    */
    public int GetNexusHash()
    {
        if (_hashCode != null)
            return _hashCode.Value;
        
        var hashCode = new HashCode();
        foreach (var meta in AllMethods!)
        {
            hashCode.Add(meta.GetNexusHash());
        }
        foreach (var meta in AllCollections!)
        {
            hashCode.Add(meta.GetNexusHash());
        }
        
        return (_hashCode = hashCode.ToHashCode()).Value;
    }
}
