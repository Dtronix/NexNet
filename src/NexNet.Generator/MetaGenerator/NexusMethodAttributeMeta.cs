using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal class NexusMethodAttributeMeta : AttributeMetaBase
{
    /// <summary>
    /// Ignore this method
    /// </summary>
    public bool Ignore { get; private set; }

    /// <summary>
    /// Manually specifies the ID of this method.  Used for invocations.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public ushort? MethodId { get; private set; }

    public NexusMethodAttributeMeta(ISymbol symbol)
        : base("NexusMethodAttribute", symbol)
    {
    }

    protected override void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant)
    {
        if (key == "MethodId" || constructorArgIndex == 0)
        {
            var id = (ushort)GetItem(typedConstant)!;
            if (id != 0)
                MethodId = id;
        }
        else if (key == "Ignore")
        {
            Ignore = (bool)GetItem(typedConstant)!;
        }
    }
}
