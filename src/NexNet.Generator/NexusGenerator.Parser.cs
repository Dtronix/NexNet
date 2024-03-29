﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Text;
using static NexNet.Generator.NexusGenerator;

namespace NexNet.Generator;

internal partial class InvocationInterfaceMeta
{
    private readonly HashSet<ushort> _methodIds = new HashSet<ushort>();

    public INamedTypeSymbol Symbol { get; }
    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public bool IsValueType { get; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    public MethodMeta[] Methods { get; }
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
            .Select(x => new MethodMeta(x))
            .Where(x => x.NexusMethodAttribute.Ignore == false) // Bypass ignored items.
            .ToArray();


        // Get all pre-defined method ids first.
        foreach (var methodMeta in this.Methods)
        {
            if (methodMeta.NexusMethodAttribute.MethodId != null)
            {
                // Assign defined ids.
                methodMeta.Id = methodMeta.NexusMethodAttribute.MethodId.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _methodIds.Add(methodMeta.NexusMethodAttribute.MethodId.Value);
            }
        }


        // Automatic method id assignment.
        ushort id = 0;
        foreach (var methodMeta in this.Methods)
        {
            if (methodMeta.Id == 0)
            {
                // Make sure we get an ID which is not used.
                while (_methodIds.Contains(id))
                    id++;

                methodMeta.Id = id++;
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
        foreach (var methodMeta in Methods)
        {
            hash += methodMeta.GetHash();
        }

        return hash;
    }

    public override string ToString()
    {
        return this.TypeName;
    }
}

internal abstract class AttributeMetaBase
{
    private readonly string _attributeClassName;

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
        }
    }
    protected abstract void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant);



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
        if (Symbol.IsGenericType)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NexusMustNotBeGeneric, syntax.Identifier.GetLocation(), Symbol.Name));
            return false;
        }

        var invokeMethodCoreExists = Methods.FirstOrDefault(m => m.Name == "InvokeMethodCore");
        if (invokeMethodCoreExists != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvokeMethodCoreReservedMethodName, syntax.Identifier.GetLocation(), invokeMethodCoreExists.Name));
            return false;
        }

        if (IsInterfaceOrAbstract)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustNotBeAbstractOrInterface, syntax.Identifier.GetLocation(), Symbol.Name));
            return false;
        }



        var nexusSet = new HashSet<ushort>();
        foreach (var method in this.NexusInterface.Methods)
        {
            // Validate nexus method ids.
            if (nexusSet.Contains(method.Id))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicatedMethodId, method.GetLocation(syntax), method.Name));
                return false;
            }
            else
            {
                nexusSet.Add(method.Id);
            }

            // Confirm the cancellation token parameter is the last parameter.
            if (method.CancellationTokenParameter != null)
            {
                if (!method.Parameters.Last().IsCancellationToken)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCancellationToken,
                        method.GetLocation(syntax), method.Name));
                    return false;
                }
            }

            // Confirm there is only one cancellation token parameter.
            if (method.MultipleCancellationTokenParameter)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TooManyCancellationTokens,
                    method.GetLocation(syntax), method.Name));
                return false;
            }

            // Confirm there is only one pipe parameter.
            if (method.MultiplePipeParameters)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TooManyPipes,
                    method.GetLocation(syntax), method.Name));
                return false;
            }

            // Null return types.
            if (method.IsReturnVoid)
            {
                if (method.CancellationTokenParameter != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CancellationTokenOnVoid,
                        method.GetLocation(syntax), method.Name));
                    return false;
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
                        method.GetLocation(syntax), method.Name));
                    return false;
                }

                // This code block checks if a method has both a duplex pipe parameter and a cancellation token parameter.
                // This ensures that the method doesn't consume both a pipe and a cancellation token at the same time.
                if (method.CancellationTokenParameter != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.PipeOnMethodWithCancellationToken,
                        method.GetLocation(syntax), method.Name));
                    return false;
                }
            }

            // Validate the return values.
            if (!method.IsReturnVoid &&
                !(method.IsAsync && method.ReturnArity <= 1))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidReturnValue, method.GetLocation(syntax), method.Name)); 
                return false;
            }
        }

        // Validate method nexuses

        return true;
    }

    public override string ToString()
    {
        return this.TypeName;
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

    public override string ToString()
    {
        var sb = SymbolUtilities.GetStringBuilder();

        if (IsReturnVoid)
        {
            sb.Append("void");
        }
        else if (IsAsync)
        {
            sb.Append("ValueTask");

            if (this.ReturnArity > 0)
            {
                sb.Append("<").Append(this.ReturnTypeSource).Append(">");
            }
        }

        sb.Append(" ");
        sb.Append(this.Name).Append("(");

        var paramsLength = this.Parameters.Length;
        if (paramsLength > 0)
        {
            for (int i = 0; i < paramsLength; i++)
            {
                sb.Append(Parameters[i].ParamTypeSource);
                sb.Append(" ");
                sb.Append(Parameters[i].Name);

                if (i + 1 < paramsLength)
                {
                    sb.Append(", ");
                }
            }
        }

        sb.Append(")");

        var stringMethod = sb.ToString();

        SymbolUtilities.ReturnStringBuilder(sb);

        return stringMethod;
    }

    public Location GetLocation(TypeDeclarationSyntax fallback)
    {
        var location = Symbol.Locations.FirstOrDefault() ?? fallback.Identifier.GetLocation();
        return location;
    }
}

