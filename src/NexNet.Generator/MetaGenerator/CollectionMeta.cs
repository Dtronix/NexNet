using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal partial class CollectionMeta
{
    private int? _nexusHash;
    private static readonly XxHash32 _hash = new XxHash32();
    public IPropertySymbol Symbol { get; }
    public string Name { get; }
    public bool IsStatic { get; }
    public string? ReturnTypeArity { get; }
    public string? ReturnTypeSource { get; }
    public int ReturnArity { get; }
    public CollectionTypeValues CollectionType  { get; }
    public string CollectionTypeShortString { get; set; }
    public string CollectionTypeFullString { get; }
    public string CollectionModeFullTypeString { get; }
    public NexusMethodAttributeMeta NexusMethodAttribute { get; }
    
    /// <summary>
    /// Assigned after parsing.
    /// </summary>
    public ushort Id { get; set; }
    public NexusCollectionAttributeMeta NexusCollectionAttribute { get; }

    public enum CollectionTypeValues
    {
        Unset,
        List,
    }

    public CollectionMeta(IPropertySymbol symbol)
    {       
        var returnSymbol = symbol.Type as INamedTypeSymbol;
        this.NexusCollectionAttribute = new NexusCollectionAttributeMeta(symbol);
        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.IsStatic = symbol.IsStatic;
        
        CollectionType = returnSymbol!.OriginalDefinition.Name switch
        {
            "INexusList" => CollectionTypeValues.List,
            _ => CollectionTypeValues.Unset
        };
        CollectionTypeFullString = CollectionType switch
        {
            CollectionTypeValues.List => "global::NexNet.Collections.Lists.INexusList",
            _ => "INVALID",
        };
        
        CollectionTypeShortString = CollectionType switch
        {
            CollectionTypeValues.List => "INexusList",
            _ => "INVALID",
        };
        
        this.ReturnArity = returnSymbol.Arity;

        CollectionModeFullTypeString = NexusCollectionAttribute.Mode switch
        {
            NexusCollectionMode.ServerToClient => "global::NexNet.Collections.NexusCollectionMode.ServerToClient",
            NexusCollectionMode.BiDirectional => "global::NexNet.Collections.NexusCollectionMode.BiDirectional",
            NexusCollectionMode.Relay => "global::NexNet.Collections.NexusCollectionMode.Relay",
            _ => "INVALID",
        };
        
        this.NexusMethodAttribute = new NexusMethodAttributeMeta(symbol);

        if (ReturnArity > 0)
        {
            this.ReturnTypeArity = SymbolUtilities.GetArityFullSymbolType(returnSymbol, 0);
            this.ReturnTypeSource = returnSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }

    public int GetNexusHash()
    {
        if (_nexusHash != null)
            return _nexusHash.Value;
        
        var hash = new HashCode();

        //ReturnType + Name
        if (ReturnTypeArity != null)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(ReturnTypeArity)));
        }

        // Add the name of the method to the hash if we do not have a manually specified ID.
        if (NexusCollectionAttribute.Id == null)
        {
            // Take the name of the method into consideration.
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(Name)));
        }
        else
        {
            hash.Add(NexusCollectionAttribute.Id.Value);
        }
        
        _nexusHash = hash.ToHashCode();
        return _nexusHash.Value;
    }

    public Location GetLocation(Location fallback)
    {
        var location = Symbol.Locations.FirstOrDefault();
        if (location is null || location.IsInMetadata)
        {
            location = fallback;
        }

        return location;
    }
}
