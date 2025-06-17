using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

internal abstract class AttributeMetaBase
{
    private readonly string _attributeClassName;

    /// <summary>
    /// True if the attribute was found on the passed symbol.
    /// </summary>
    public bool AttributeExists { get; set; }

    protected AttributeMetaBase(string attributeClassName, ISymbol symbol)
    {
        _attributeClassName = attributeClassName;
        Parse(symbol);
    }

    private void Parse(ISymbol symbol)
    {
        foreach (AttributeData attributeData in symbol.GetAttributes())
        {
            if (attributeData.AttributeClass!.Name != _attributeClassName)
                continue;
            
            AttributeExists = true;
            // supports: [Attribute<INexus, IProxy>(NexusType.Client)]
            // supports: [Attribute<INexus, IProxy>(NexusType: NexusType.Client)]
            if (attributeData.ConstructorArguments.Any())
            {
                ImmutableArray<TypedConstant> items = attributeData.ConstructorArguments;

                for (var i = 0; i < items.Length; i++)
                {
                    ProcessArgument(null, i, items[i]);
                }
            }

            // argument syntax takes parameters. e.g. EventId = 0
            // supports: e.g. [NexusAttribute<INexus, IProxy>(Type = NexusType.Client)]
            if (attributeData.NamedArguments.Any())
            {
                foreach (KeyValuePair<string, TypedConstant> namedArgument in attributeData.NamedArguments)
                {
                    TypedConstant value = namedArgument.Value;
                    ProcessArgument(namedArgument.Key, null, value);
                }
            }

            ProcessComplete();

            return;
        }
    }
    
    protected abstract void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant);

    protected virtual void ProcessComplete()
    {
        
    }

    protected static object GetItem(TypedConstant arg)
    {
        if (arg.Kind == TypedConstantKind.Array)
        {
            return arg.Values;
        }
        else
        {
            return arg.Value ?? new object();
        }
    }
}
