using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

public class ReferenceSymbols
{
    public Compilation Compilation { get; }

    public INamedTypeSymbol NexNetHubAttribute { get; }
    public INamedTypeSymbol NexNetIgnoreAttribute { get; }

    public ReferenceSymbols(Compilation compilation)
    {
        Compilation = compilation;

        // NexNetHubAttribute
        NexNetHubAttribute = GetTypeByMetadataName(NexNetHubGenerator.NexNetHubAttributeFullName).ConstructUnboundGenericType();
        NexNetIgnoreAttribute = GetTypeByMetadataName("NexNet.NexNetIgnoreAttribute");
    }

    INamedTypeSymbol GetTypeByMetadataName(string metadataName)
    {
        var symbol = Compilation.GetTypeByMetadataName(metadataName);
        if (symbol == null)
        {
            throw new InvalidOperationException($"Type {metadataName} is not found in compilation.");
        }
        return symbol;
    }
}
