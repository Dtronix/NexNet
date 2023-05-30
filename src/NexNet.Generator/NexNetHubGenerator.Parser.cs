using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using static NexNet.Generator.NexNetHubGenerator;

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

        var usedHashes = new HashSet<ushort>();
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
            .Where(x => x.NexNetMethodAttribute.Ignore == false) // Bypass ignored items.
            .ToArray();


        // Get all pre-defined method ids first.
        foreach (var methodMeta in this.Methods)
        {
            if (methodMeta.NexNetMethodAttribute.MethodId != null)
            {
                // Assign defined ids.
                methodMeta.Id = methodMeta.NexNetMethodAttribute.MethodId.Value;

                // Add the id to the hash list to prevent reuse during automatic assignment.
                _methodIds.Add(methodMeta.NexNetMethodAttribute.MethodId.Value);
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


        var members = Symbol.GetMembers().ToArray();

        var lessInterfaceI = symbol.Name[0] == 'I' ? symbol.Name.Substring(1) : symbol.Name;
        //this.ProxyImplName = $"{lessInterfaceI}ProxyImpl";

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
            // supports: [NexNetHubAttribute<IHub, IProxy>(NexNetHubType.Client)]
            // supports: [NexNetHubAttribute<IHub, IProxy>(hubType: NexNetHubType.Client)]
            if (attributeData.ConstructorArguments.Any())
            {
                ImmutableArray<TypedConstant> items = attributeData.ConstructorArguments;

                for (var i = 0; i < items.Length; i++)
                {
                    ProcessArgument(null, i, items[i]);
                }
            }

            // argument syntax takes parameters. e.g. EventId = 0
            // supports: e.g. [NexNetHubAttribute<IHub, IProxy>(HubType = NexNetHubType.Client)]
            if (attributeData.NamedArguments.Any())
            {
                foreach (KeyValuePair<string, TypedConstant> namedArgument in attributeData.NamedArguments)
                {
                    TypedConstant typedConstant = namedArgument.Value;
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

internal class NexNetHubAttributeMeta : AttributeMetaBase
{
    public bool IsClientHub { get; private set; }
    public bool IsServerHub { get; private set; }

    public NexNetHubAttributeMeta(INamedTypeSymbol symbol)
        : base("NexNetHubAttribute", symbol)
    {
    }

    protected override void ProcessArgument(string? key, int? constructorArgIndex, TypedConstant typedConstant)
    {
        if (key == "HubType" || constructorArgIndex == 0)
        {
            IsClientHub = (int)GetItem(typedConstant) == 0;
            IsServerHub = (int)GetItem(typedConstant) == 1;
        }
    }
}

internal class NexNetMethodAttributeMeta : AttributeMetaBase
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

    public NexNetMethodAttributeMeta(ISymbol symbol)
        : base("NexNetMethodAttribute", symbol)
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

internal partial class HubMeta
{
    public INamedTypeSymbol Symbol { get; set; }
    public string TypeName { get; }
    //public string ClassName { get; }
    public bool IsValueType { get; set; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }

    public string Namespace { get; }

    public NexNetHubAttributeMeta NexNetHubAttribute { get; set; }

    public InvocationInterfaceMeta HubInterface { get; }
    public InvocationInterfaceMeta ProxyInterface { get; }
    public MethodMeta[] Methods { get; }

    public HubMeta(INamedTypeSymbol symbol)
    {
        
        this.Symbol = symbol;
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.Namespace = Symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.NexNetHubAttribute = new NexNetHubAttributeMeta(symbol);

        var hubAttributeData = symbol.GetAttributes().First(att => att.AttributeClass!.Name == "NexNetHubAttribute");
        HubInterface = new InvocationInterfaceMeta(
            hubAttributeData.AttributeClass!.TypeArguments[0] as INamedTypeSymbol, NexNetHubAttribute.IsServerHub);
        ProxyInterface = new InvocationInterfaceMeta(
            hubAttributeData.AttributeClass!.TypeArguments[1] as INamedTypeSymbol, NexNetHubAttribute.IsServerHub);

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
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.HubMustNotBeGeneric, syntax.Identifier.GetLocation(), Symbol.Name));
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



        var hubs = new HashSet<ushort>();
        foreach (var hubInterfaceMethod in this.HubInterface.Methods)
        {
            // Validate hub method ids.
            if (hubs.Contains(hubInterfaceMethod.Id))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicatedMethodId, hubInterfaceMethod.GetLocation(syntax), hubInterfaceMethod.Name));
                return false;
            }
            else
            {
                hubs.Add(hubInterfaceMethod.Id);
            }

            if (hubInterfaceMethod.ParametersLessCancellation.Any(p => p.IsCancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCancellationToken, hubInterfaceMethod.GetLocation(syntax), hubInterfaceMethod.Name));
                return false;
            }

            if (hubInterfaceMethod.IsReturnVoid && hubInterfaceMethod.CancellationTokenParameter != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CancellationTokenOnVoid, hubInterfaceMethod.GetLocation(syntax), hubInterfaceMethod.Name));
                return false;
            }


            // Validate return values.
            if (hubInterfaceMethod.IsReturnVoid)
                continue;


            if (hubInterfaceMethod.IsAsync
                && hubInterfaceMethod.ReturnArity <= 1)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidReturnValue, hubInterfaceMethod.GetLocation(syntax), hubInterfaceMethod.Name));
            return false;
        }

        // Validate method hubs

        return true;
    }

    public override string ToString()
    {
        return this.TypeName;
    }
}

internal partial class MethodParameterMeta
{
    public IParameterSymbol Symbol { get; }
    public string Name { get; }

    public string ParamType { get; set; }

    public bool IsParamsArray { get; set; }

    public bool IsArrayType { get; set; }

    public bool IsCancellationToken { get; set; }

    public MethodParameterMeta(IParameterSymbol symbol)
    {
        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.IsArrayType = symbol.Type.TypeKind == TypeKind.Array;
        this.ParamType = SymbolUtilities.GetFullSymbolType(symbol.Type, false);
        this.IsParamsArray = symbol.IsParams;
        this.IsCancellationToken = symbol.Type.Name == "CancellationToken";
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
    public bool IsAsync { get; }
    public int ReturnArity { get; }
    public MethodParameterMeta? CancellationTokenParameter { get; }
    public MethodParameterMeta[] ParametersLessCancellation { get; }
    public ushort Id { get; set; }
    public NexNetMethodAttributeMeta NexNetMethodAttribute { get; }

    public MethodMeta(IMethodSymbol symbol)
    {
        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;

        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.IsStatic = symbol.IsStatic;
        this.IsAsync = returnSymbol!.OriginalDefinition.Name == "ValueTask";
        var parameters = symbol.Parameters.Select(p => new MethodParameterMeta(p)).ToList();

        var lastParameter = parameters.LastOrDefault();
        if (lastParameter != null && lastParameter.IsCancellationToken)
        {
            this.CancellationTokenParameter = lastParameter;

            // Remove this from the param list.
            parameters.Remove(this.CancellationTokenParameter);
        }

        this.ParametersLessCancellation = parameters.ToArray();
        this.ReturnArity = returnSymbol.Arity;
        this.IsReturnVoid = returnSymbol.Name == "Void";
        this.NexNetMethodAttribute = new NexNetMethodAttributeMeta(symbol);

        if (ReturnArity > 0)
            this.ReturnType = SymbolUtilities.GetFullSymbolType(returnSymbol, true);
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
        if (NexNetMethodAttribute.MethodId == null)
        {
            // Take the name of the method into consideration.
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(Name)));
        }

        foreach (var param in ParametersLessCancellation)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(param.ParamType)));
        }

        if(CancellationTokenParameter != null)
            hash.Add(1);

        return hash.ToHashCode();
    }

    public Location GetLocation(TypeDeclarationSyntax fallback)
    {
        var location = Symbol.Locations.FirstOrDefault() ?? fallback.Identifier.GetLocation();
        return location;
    }
}

