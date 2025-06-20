using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal class NexusVersionAttributeMeta : AttributeMetaBase
{
    public string? Version { get; private set; }

    public NexusVersionAttributeMeta(ISymbol symbol)
        : base("NexusVersionAttribute", symbol)
    {
    }

    protected override void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant)
    {
        if (key == "Version" || constructorArgIndex == 0)
        {
            var id = (string?)GetItem(typedConstant);
            if (id != null)
                Version = id;
        }
    }

    public override string ToString() => $"Version: {Version}";
}
