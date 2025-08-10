using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal class NexusAttributeMeta : AttributeMetaBase
{
    public bool IsClient { get; private set; }
    public bool IsServer { get; private set; }

    public NexusAttributeMeta(INamedTypeSymbol symbol)
        : base("NexusAttribute", symbol)
    {
    }

    protected override void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant)
    {
        if (key == "NexusType" || constructorArgIndex == 0)
        {
            IsClient = (int)GetItem(typedConstant)! == 0;
            IsServer = (int)GetItem(typedConstant)! == 1;
        }
    }
}
