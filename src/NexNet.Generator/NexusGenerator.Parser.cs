using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using static NexNet.Generator.NexusGenerator;

namespace NexNet.Generator;

internal partial class InvocationInterfaceMeta
{
    private readonly HashSet<ushort> _usedIds = new HashSet<ushort>();

    public INamedTypeSymbol Symbol { get; }
    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public bool IsValueType { get; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    public MethodMeta[] Methods { get; }
    
    public CollectionMeta[] Collections { get; set; }
    public string ProxyImplName { get; }
    public string ProxyImplNameWithNamespace { get; }
    public string Namespace { get; }
    public string NamespaceName { get; }

    //public bool AlreadyGeneratedHash { get; }

    public InvocationInterfaceMeta(INamedTypeSymbol? symbol, bool isServer)
    {
        if (symbol == null)
            throw new ArgumentNullException(nameof(symbol));

        this.Symbol = symbol;
        this.Namespace = symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.NamespaceName = symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;
        this.Methods = Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(x => x.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet))
            .Select(x => new MethodMeta(x))
            .Where(x => x.NexusMethodAttribute is { Ignore: false }) // Bypass ignored items.
            .ToArray();
        
        this.Collections = Symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(x => new CollectionMeta(x))
            .Where(x => x.NexusCollectionAttribute is { Ignore: false }) // Bypass ignored items.
            .ToArray();


        // Get all pre-defined method ids first.
        foreach (var methodMeta in this.Methods)
        {
            if (methodMeta.NexusMethodAttribute.MethodId != null)
            {
                // Assign defined ids.
                methodMeta.Id = methodMeta.NexusMethodAttribute.MethodId.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _usedIds.Add(methodMeta.NexusMethodAttribute.MethodId.Value);
            }
        }
        
        foreach (var collectionMeta in this.Collections)
        {
            if (collectionMeta.NexusCollectionAttribute.Id != null)
            {
                // Assign defined ids.
                collectionMeta.Id = collectionMeta.NexusCollectionAttribute.Id.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _usedIds.Add(collectionMeta.NexusCollectionAttribute.Id.Value);
            }
        }

        // Automatic id assignment.
        ushort id = 0;
        foreach (var methodMeta in this.Methods)
        {
            if (methodMeta.Id == 0)
            {
                // Make sure we get an ID which is not used.
                while (_usedIds.Contains(id))
                    id++;

                methodMeta.Id = id++;
            }
        }
        
        // Automatic id assignment.
        foreach (var collectionMeta in this.Collections)
        {
            if (collectionMeta.Id == 0)
            {
                // Make sure we get an ID which is not used.
                while (_usedIds.Contains(id))
                    id++;

                collectionMeta.Id = id++;
            }
        }

        this.ProxyImplName = !isServer ? $"ServerProxy" : "ClientProxy";
        //this.ProxyImplNameWithNamespace = $"{Namespace}.{ProxyImplName}";
        this.ProxyImplNameWithNamespace = $"{Namespace}.{ProxyImplName}";
        //this.AlreadyGeneratedHash = false;
    }

    public bool Validate(TypeDeclarationSyntax syntax, GeneratorContext context)
    {
        var noError = true;
        
        // ALl Members
        if (Methods.Length >= ushort.MaxValue) // MemoryPackCode.Reserved1
        {
            //context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MembersCountOver250, syntax.Identifier.GetLocation(), Symbol.Name, Members.Length));
            //noError = false;
        }

        return noError;
    }

    public int GetHash()
    {
        var hash = 0;
        foreach (var meta in Methods)
        {
            hash += meta.GetHash();
        }
        foreach (var meta in Collections)
        {
            hash += meta.GetHash();
        }

        return hash;
    }
}

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
            IsClient = (int)GetItem(typedConstant) == 0;
            IsServer = (int)GetItem(typedConstant) == 1;
        }
    }
}

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
            var id = (ushort)GetItem(typedConstant);
            if (id != 0)
                MethodId = id;
        }
        else if (key == "Ignore")
        {
            Ignore = (bool)GetItem(typedConstant);
        }
    }
}

internal enum NexusCollectionMode 
{
    Unset,
    ServerToClient,
    BiDrirectional
}


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

internal partial class NexusMeta
{
    public INamedTypeSymbol Symbol { get; set; }
    public string TypeName { get; }
    //public string ClassName { get; }
    public bool IsValueType { get; set; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }

    public string Namespace { get; }

    public NexusAttributeMeta NexusAttribute { get; set; }

    public InvocationInterfaceMeta NexusInterface { get; }
    public InvocationInterfaceMeta ProxyInterface { get; }
    public MethodMeta[] Methods { get; }

    public NexusMeta(INamedTypeSymbol symbol)
    {
        
        this.Symbol = symbol;
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.Namespace = Symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.NexusAttribute = new NexusAttributeMeta(symbol);

        var nexusAttributeData = symbol.GetAttributes().First(att => att.AttributeClass!.Name == "NexusAttribute");
        NexusInterface = new InvocationInterfaceMeta(
            nexusAttributeData.AttributeClass!.TypeArguments[0] as INamedTypeSymbol, NexusAttribute.IsServer);
        ProxyInterface = new InvocationInterfaceMeta(
            nexusAttributeData.AttributeClass!.TypeArguments[1] as INamedTypeSymbol, NexusAttribute.IsServer);

        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;
        this.Methods = Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(x => new MethodMeta(x))
            .ToArray();
    }


    public bool Validate(TypeDeclarationSyntax syntax, GeneratorContext context)
    {
        var nexusLocation = syntax.Identifier.GetLocation();
        bool failed = false;
        if (Symbol.IsGenericType)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NexusMustNotBeGeneric, nexusLocation, Symbol.Name));
            failed = true;
        }

        var invokeMethodCoreExists = Methods.FirstOrDefault(m => m.Name == "InvokeMethodCore");
        if (invokeMethodCoreExists != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvokeMethodCoreReservedMethodName, nexusLocation, invokeMethodCoreExists.Name));
            failed = true;
        }

        if (IsInterfaceOrAbstract)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustNotBeAbstractOrInterface, nexusLocation, Symbol.Name));
            failed = true;
        }

        var nexusSet = new HashSet<ushort>();
        foreach (var method in this.NexusInterface.Methods)
        {
            // Validate nexus method ids.
            if (nexusSet.Contains(method.Id))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicatedMethodId, method.GetLocation(nexusLocation), method.Name));
                failed = true;
            }

            nexusSet.Add(method.Id);

            // Confirm the cancellation token parameter is the last parameter.
            if (method.CancellationTokenParameter != null)
            {
                if (!method.Parameters.Last().IsCancellationToken)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCancellationToken,
                        method.GetLocation(nexusLocation), method.Name));
                    failed = true;
                }
            }

            // Confirm there is only one cancellation token parameter.
            if (method.MultipleCancellationTokenParameter)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TooManyCancellationTokens,
                    method.GetLocation(nexusLocation), method.Name));
                failed = true;
            }

            // Confirm there is only one pipe parameter.
            if (method.MultiplePipeParameters)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TooManyPipes,
                    method.GetLocation(nexusLocation), method.Name));
                failed = true;
            }

            // Null return types.
            if (method.IsReturnVoid)
            {
                if (method.CancellationTokenParameter != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CancellationTokenOnVoid,
                        method.GetLocation(nexusLocation), method.Name));
                    failed = true;
                }
            }

            // Duplex pipe parameters.
            if (method.UtilizesPipes)
            {
                // Validates the return type of the Nexus Interface method and returns a valueless
                // response if the method is async and has only one type argument or if the method has no
                // return type at all.
                if (method.IsReturnVoid
                    || (method.IsAsync && method.ReturnArity == 1))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.PipeOnVoidOrReturnTask,
                        method.GetLocation(nexusLocation), method.Name));
                    failed = true;
                }

                // This code block checks if a method has both a duplex pipe parameter and a cancellation token parameter.
                // This ensures that the method doesn't consume both a pipe and a cancellation token at the same time.
                if (method.CancellationTokenParameter != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.PipeOnMethodWithCancellationToken,
                        method.GetLocation(nexusLocation), method.Name));
                    failed = true;
                }
            }

            // Validate the return values.
            if (!method.IsReturnVoid &&
                !(method.IsAsync && method.ReturnArity <= 1))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidReturnValue, method.GetLocation(nexusLocation), method.Name)); 
                failed = true;
            }
        }

        // Validate collections
        

        foreach (var collection in this.NexusInterface.Collections)
        {
            if (!NexusAttribute.IsServer)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CollectionCanNotBeOnClient,
                    collection.GetLocation(nexusLocation), collection.Name));
                failed = true;
            }
            else
            {
                // Validate nexus method ids.
                if (nexusSet.Contains(collection.Id))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicatedMethodId,
                        collection.GetLocation(nexusLocation), collection.Name));
                    failed = true;
                }

                nexusSet.Add(collection.Id);

                if (collection.CollectionType == CollectionMeta.CollectionTypeValues.Unset)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CollectionUnknownType,
                        collection.GetLocation(nexusLocation), collection.Name));
                    failed = true;
                }

                if (collection.NexusCollectionAttribute.Mode == NexusCollectionMode.Unset
                    || !Enum.IsDefined(typeof(NexusCollectionMode), collection.NexusCollectionAttribute.Mode))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CollectionUnknownMode,
                        collection.GetLocation(nexusLocation), collection.Name));
                    failed = true;
                }

                if (!collection.NexusCollectionAttribute.AttributeExists
                    && collection.CollectionType != CollectionMeta.CollectionTypeValues.Unset)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CollectionAttributeMissing,
                        collection.GetLocation(nexusLocation), collection.Name));
                    failed = true;
                }
            }
        }

        return !failed;
    }
}

internal class MethodParameterMeta
{
    public IParameterSymbol Symbol { get; }
    public string Name { get; }

    public int Index { get; }

    public string ParamType { get; }
    public string? SerializedType { get; }

    public string? SerializedValue { get; }
    //public string? DeserializedParameterValue { get; }

    public bool IsParamsArray { get; }

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

    public MethodParameterMeta(IParameterSymbol symbol, int index)
    {
        this.Index = index;
        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.IsArrayType = symbol.Type.TypeKind == TypeKind.Array;
        this.ParamTypeSource = symbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.ParamType = SymbolUtilities.GetFullSymbolType(symbol.Type, false);
        this.IsParamsArray = symbol.IsParams;
        this.IsCancellationToken = symbol.Type.Name == "CancellationToken";
        this.IsDuplexPipe = ParamType == "global::NexNet.Pipes.INexusDuplexPipe";
        this.IsDuplexUnmanagedChannel = ParamType.StartsWith("global::NexNet.Pipes.INexusDuplexUnmanagedChannel<");
        this.IsDuplexChannel = ParamType.StartsWith("global::NexNet.Pipes.INexusDuplexChannel<");
        this.UtilizesDuplexPipe = IsDuplexPipe | IsDuplexUnmanagedChannel | IsDuplexChannel;
        if (IsDuplexPipe)
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

internal partial class MethodMeta
{
    private static readonly XxHash32 _hash = new XxHash32();
    public IMethodSymbol Symbol { get; }
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

    public MethodMeta(IMethodSymbol symbol)
    {
        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;
        this.Symbol = symbol;
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
            var param = Parameters[i] = new MethodParameterMeta(symbol.Parameters[i], i);

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



    public int GetHash()
    {
        var hash = new HashCode();

        //ReturnType + Name + Params + cancellationToken
        if (ReturnType != null)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(ReturnType)));
        }

        hash.Add(Id);

        // Add the name of the method to the hash if we do not have a manually specified ID.
        if (NexusMethodAttribute.MethodId == null)
        {
            // Take the name of the method into consideration.
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(Name)));
        }

        foreach (var param in Parameters)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(param.ParamType)));
        }

        return hash.ToHashCode();
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

internal partial class CollectionMeta
{
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
            NexusCollectionMode.BiDrirectional => "global::NexNet.Collections.NexusCollectionMode.BiDrirectional",
            _ => "INVALID",
        };

        if (ReturnArity > 0)
        {
            this.ReturnTypeArity = SymbolUtilities.GetArityFullSymbolType(returnSymbol, 0);
            this.ReturnTypeSource = returnSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }


    public int GetHash()
    {
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
        
        return hash.ToHashCode();
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

