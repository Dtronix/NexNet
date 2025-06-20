using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal class NexusCollectionAttributeMeta : AttributeMetaBase
{
    /// <summary>
    /// Ignore this method
    /// </summary>
    public bool Ignore { get; private set; }

    /// <summary>
    /// Manually specifies the ID of this method.  Used for invocations.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public ushort? Id { get; private set; }

    /// <summary>
    /// Manually specifies the ID of this method.  Used for invocations.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public NexusCollectionMode Mode { get; private set; } = NexusCollectionMode.Unset;

    public NexusCollectionAttributeMeta(ISymbol symbol)
        : base("NexusCollectionAttribute", symbol)
    {
    }

    protected override void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant)
    {
        if (key == "Mode" || constructorArgIndex == 0)
        {
            var mode = (NexusCollectionMode)(int)GetItem(typedConstant);
            if (mode != NexusCollectionMode.Unset)
                Mode = mode;
        }
        else if (key == "Id" || constructorArgIndex == 1)
        {
            var id = (ushort)GetItem(typedConstant);
            if (id != 0)
                Id = id;
        }
    }
}
