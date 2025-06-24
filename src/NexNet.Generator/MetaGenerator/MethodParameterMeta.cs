using MemoryPack.Generator;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator.MetaGenerator;

internal class MethodParameterMeta
{
    public IParameterSymbol Symbol { get; }
    public string Name { get; }

    public int Index { get; }
    public ReferenceSymbols MemoryPackReferences { get; }

    public string ParamType { get; }
    public string? SerializedType { get; }

    public string? SerializedValue { get; }
    //public string? DeserializedParameterValue { get; }

    public bool IsParamsArray { get; }
    public bool IsMemoryPackObject { get; }
    public bool IsArrayType { get; }
    public bool IsDuplexPipe { get; }
    public bool IsDuplexUnmanagedChannel { get; }
    public bool IsDuplexChannel { get; }
    public string? ChannelType { get; }

    /// <summary>
    /// True if the parameter is a duplex pipe or duplex channel.
    /// </summary>
    public bool UtilizesDuplexPipe { get; }

    public bool IsCancellationToken { get; }


    public string ParamTypeSource { get; }
    public int SerializedId { get; set; }

    public TypeMeta? MemoryPackType { get; }

    public MethodParameterMeta(IParameterSymbol symbol, int index, ReferenceSymbols memoryPackReferences)
    {
        this.Index = index;
        this.MemoryPackReferences = memoryPackReferences;
        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.IsMemoryPackObject = symbol.Type.ContainsAttribute(memoryPackReferences.MemoryPackableAttribute);
        this.IsArrayType = symbol.Type.TypeKind == TypeKind.Array;
        this.ParamTypeSource = symbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.ParamType = SymbolUtilities.GetFullSymbolType(symbol.Type, false);
        this.IsParamsArray = symbol.IsParams;
        this.IsCancellationToken = symbol.Type.Name == "CancellationToken";
        this.IsDuplexPipe = ParamType == "global::NexNet.Pipes.INexusDuplexPipe";
        this.IsDuplexUnmanagedChannel = ParamType.StartsWith("global::NexNet.Pipes.INexusDuplexUnmanagedChannel<");
        this.IsDuplexChannel = ParamType.StartsWith("global::NexNet.Pipes.INexusDuplexChannel<");
        this.UtilizesDuplexPipe = IsDuplexPipe | IsDuplexUnmanagedChannel | IsDuplexChannel;
        if (IsMemoryPackObject)
        {
            MemoryPackType = new TypeMeta((INamedTypeSymbol)symbol.Type, MemoryPackReferences);
            SerializedType = ParamType;
            SerializedValue = Name;
        }
        else if (IsDuplexPipe)
        {
            // Duplex Pipe is serialized as a byte.
            SerializedType = "global::System.Byte";
            SerializedValue = $"__proxyInvoker.ProxyGetDuplexPipeInitialId({Name})";
        }
        else if (IsDuplexUnmanagedChannel || IsDuplexChannel)
        {
            var returnSymbol = symbol.Type as INamedTypeSymbol;
            ChannelType = SymbolUtilities.GetFullSymbolType(returnSymbol?.TypeArguments[0], false);
            // Duplex Pipe is serialized as a byte.
            SerializedType = "global::System.Byte";
            SerializedValue = $"__proxyInvoker.ProxyGetDuplexPipeInitialId({Name}.BasePipe)";
        }
        else if(IsCancellationToken)
        {
            // Type is not serialized.
            SerializedType = null;
            SerializedValue = null;
        }
        else
        {
            // Normal serialized type.
            SerializedType = ParamType;
            SerializedValue = Name;
        }

    }

}
