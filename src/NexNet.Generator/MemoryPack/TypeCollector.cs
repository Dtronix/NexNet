using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MemoryPack;

internal class TypeCollector
{
    private readonly MemoryPackReferences _references;
    HashSet<ITypeSymbol> types = new(SymbolEqualityComparer.Default);
    

    public TypeCollector(MemoryPackReferences references)
    {
        _references = references;
    }

    public void Visit(ISymbol symbol)
    {
        if (symbol is ITypeSymbol typeSymbol)
        {
            // 7~20 is primitive
            if ((int)typeSymbol.SpecialType is >= 7 and <= 20)
            {
                return;
            }

            if (!types.Add(typeSymbol))
            {
                return;
            }

            if (typeSymbol is IArrayTypeSymbol array)
            {
                Visit(array.ElementType);
            }
            else if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            { 
                if (namedTypeSymbol.IsGenericType)
                {
                    foreach (var item in namedTypeSymbol.TypeArguments)
                    {
                        Visit(item);
                    }
                }
            }
        }
    }

    public IEnumerable<ITypeSymbol> GetEnums()
    {
        foreach (var typeSymbol in types)
        {
            if (typeSymbol.TypeKind == TypeKind.Enum)
            {
                yield return typeSymbol;
            }
        }
    }

    public IEnumerable<ITypeSymbol> GetMemoryPackableTypes(MemoryPackReferences reference)
    {
        foreach (var typeSymbol in types)
        {
            if (typeSymbol.ContainsAttribute(reference.MemoryPackableAttribute))
            {
                yield return typeSymbol;
            }
        }
    }

    public IEnumerable<ITypeSymbol> GetTypes()
    {
        return types.OfType<ITypeSymbol>();
    }
}
