using System.Text;
using Microsoft.CodeAnalysis;
using NexNet.Generator.MemoryPack;

namespace NexNet.Generator.MetaGenerator;

internal partial class MethodMeta
{
    private int? _nexusHash;
    private static readonly XxHash32 _hash = new XxHash32();
    public IMethodSymbol Symbol { get; }
    public MemoryPackReferences MemoryPackReferences { get; }
    public string Name { get; }
    public bool IsStatic { get; }
    public bool IsReturnVoid { get; }
    public string? ReturnType { get; }
    public string? ReturnTypeSource { get; }
    public bool IsAsync { get; }
    public int ReturnArity { get; }
    public MethodParameterMeta? CancellationTokenParameter { get; }
    public MethodParameterMeta? DuplexPipeParameter { get; }
    public bool UtilizesPipes { get; }

    public bool MultiplePipeParameters { get; }
    public bool MultipleCancellationTokenParameter { get; }

    public int SerializedParameters { get; }

    public MethodParameterMeta[] Parameters { get; }
    public ushort Id { get; set; }
    public NexusMethodAttributeMeta NexusMethodAttribute { get; }

    public MethodMeta(IMethodSymbol symbol, MemoryPackReferences memoryPackReferences)
    {
        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;
        this.Symbol = symbol;
        this.MemoryPackReferences = memoryPackReferences;
        this.Name = symbol.Name;
        this.IsStatic = symbol.IsStatic;
        this.IsAsync = returnSymbol!.OriginalDefinition.Name == "ValueTask";

        var serializedParameters = 0;
        var paramsLength = symbol.Parameters.Length;
        Parameters = new MethodParameterMeta[paramsLength];

        var seralizedParamId = 1;
        var pipeCount = 0;
        for (var i = 0; i < symbol.Parameters.Length; i++)
        {
            var param = Parameters[i] = new MethodParameterMeta(symbol.Parameters[i], i, MemoryPackReferences);

            if (param.SerializedType != null)
                param.SerializedId = seralizedParamId++;

            if (param.IsCancellationToken)
            {
                if (CancellationTokenParameter != null)
                    MultipleCancellationTokenParameter = true;

                CancellationTokenParameter = param;
            }
            else if (param.UtilizesDuplexPipe)
            {
                DuplexPipeParameter = param;
                UtilizesPipes = true;

                if (pipeCount++ > 0)
                    MultiplePipeParameters = true;
            }

            if (param.SerializedType != null)
                serializedParameters++;
        }

        this.SerializedParameters = serializedParameters;
        this.ReturnArity = returnSymbol.Arity;
        this.IsReturnVoid = returnSymbol.Name == "Void";

        this.NexusMethodAttribute = new NexusMethodAttributeMeta(symbol);

        if (ReturnArity > 0)
        {
            this.ReturnType = SymbolUtilities.GetFullSymbolType(returnSymbol, true);
            this.ReturnTypeSource = returnSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }



    public int GetNexusHash()
    {
        if (_nexusHash != null)
            return _nexusHash.Value;
        
        var hash = new HashCode();

        //ReturnType + Name + Params + cancellationToken
        if (ReturnType != null)
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(ReturnType)));

        hash.Add(Id);
        // Add the name of the method to the hash if we do not have a manually specified ID.
        if (NexusMethodAttribute.MethodId == null)
        {
            // Take the name of the method into consideration.
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(Name)));
        }

        foreach (var param in Parameters)
        {
            // If we have a memorypack type we need to hash the members ov the object.
            //TODO: Undo this 
            //hash.Add(param.MemoryPackType?.GetNexusHash() 
            //         ?? (int)_hash.ComputeHash(Encoding.UTF8.GetBytes(param.ParamType)));
            
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
