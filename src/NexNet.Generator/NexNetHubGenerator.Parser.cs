using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace NexNet.Generator;

internal partial class InvocationInterfaceMeta
{
    public INamedTypeSymbol Symbol { get; set; }
    /// <summary>MinimallyQualifiedFormat(include generics T)</summary>
    public string TypeName { get; }
    public bool IsValueType { get; set; }
    public bool IsRecord { get; }
    public bool IsInterfaceOrAbstract { get; }
    public MethodMeta[] Methods { get; }
    public string ProxyImplName { get; }
    public string ProxyImplNameWithNamespace { get; }
    public string Namespace { get; }

    //public bool AlreadyGeneratedHash { get; }

    public InvocationInterfaceMeta(INamedTypeSymbol? symbol)
    {
        if (symbol == null)
            throw new ArgumentNullException(nameof(symbol));

        int methodId = 0;
        this.Symbol = symbol;
        this.Namespace = symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;
        this.Methods = Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(x => new MethodMeta(x, methodId++))
            .ToArray();

        var members = Symbol.GetMembers().ToArray();

        var lessInterfaceI = symbol.Name[0] == 'I' ? symbol.Name.Substring(1) : symbol.Name;
        this.ProxyImplName = $"{lessInterfaceI}ProxyImpl";
        this.ProxyImplNameWithNamespace = $"{Namespace}.{ProxyImplName}";
        //this.AlreadyGeneratedHash = false;
    }



    public bool Validate(TypeDeclarationSyntax syntax, IGeneratorContext context)
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
        var hash = new HashCode();
        foreach (var methodMeta in Methods)
        {
            hash.Add(methodMeta.GetHash());
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return this.TypeName;
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

    public bool IsClientHub { get; }
    public bool IsServerHub { get; }

    public InvocationInterfaceMeta HubInterface { get; }
    public InvocationInterfaceMeta ProxyInterface { get; }
    public MethodMeta[] Methods { get; }

    public HubMeta(INamedTypeSymbol symbol)
    {
        static object GetItem(TypedConstant arg) => arg.Kind == TypedConstantKind.Array ? arg.Values : arg.Value ?? new object();
        this.Symbol = symbol;
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);


        foreach (AttributeData attributeData in symbol.GetAttributes())
        {
            // supports: [NexNetHubAttribute<IHub, IProxy>(NexNetHubType.Client)]
            // supports: [NexNetHubAttribute<IHub, IProxy>(hubType: NexNetHubType.Client)]
            if (attributeData.ConstructorArguments.Any())
            {
                ImmutableArray<TypedConstant> items = attributeData.ConstructorArguments;
                Debug.Assert(items.Length == 1);

                if (!items[0].IsNull)
                {
                    IsClientHub = (int)GetItem(items[0]) == 0;
                    IsServerHub = (int)GetItem(items[0]) == 1;
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
                    switch (namedArgument.Key)
                    {
                        case "HubType":
                            IsClientHub = (int)GetItem(value) == 0;
                            IsServerHub = (int)GetItem(value) == 1;
                            break;
                    }
                }
            }
        }

        var hubAttributeData = symbol.GetAttributes().First(att => att.AttributeClass!.Name == "NexNetHubAttribute");
        //new InvocationInterfaceMeta()
        HubInterface = new InvocationInterfaceMeta(hubAttributeData.AttributeClass!.TypeArguments[0] as INamedTypeSymbol);
        ProxyInterface = new InvocationInterfaceMeta(hubAttributeData.AttributeClass!.TypeArguments[1] as INamedTypeSymbol);

        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;
        this.Methods = Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(x => new MethodMeta(x, -1))
            .ToArray();
    }


    public bool Validate(TypeDeclarationSyntax syntax, IGeneratorContext context)
    {
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

    public bool IsCancellationToken { get; set; }

    public MethodParameterMeta(IParameterSymbol symbol)
    {
        this.Symbol = symbol;
        this.Name = symbol.Name;
        this.ParamType = symbol.Type.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + symbol.Type.Name;
        this.IsParamsArray = symbol.IsParams;
        IsCancellationToken = symbol.Type.Name == "CancellationToken";
    }
}

internal partial class MethodMeta
{
    private static readonly XxHash32 _hash = new XxHash32();
    public IMethodSymbol Symbol { get; }
    public string Name { get; }
    public bool IsStatic { get; }

    public bool IsValueTypeReturn { get; }
    public bool IsReturnVoid { get; }
    public string? ReturnType { get; }
    public bool IsAsync { get; }
    public int ReturnArity { get; }
    public MethodParameterMeta? CancellationTokenParameter { get; }
    public MethodParameterMeta[] ParametersLessCancellation { get; }
    public int Id { get; }

    public MethodMeta(IMethodSymbol symbol, int id)
    {
        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;

        this.Symbol = symbol;
        this.Id = id;
        this.Name = symbol.Name;
        this.IsStatic = symbol.IsStatic;
        this.IsAsync = returnSymbol!.OriginalDefinition.Name == "ValueTask";
        this.ParametersLessCancellation = symbol.Parameters.Select(p => new MethodParameterMeta(p)).Where(p => !p.IsCancellationToken).ToArray();
        this.ReturnArity = returnSymbol.Arity;
        this.IsReturnVoid = returnSymbol.Name == "Void";

        var lastParameter = symbol.Parameters.LastOrDefault();
        if (lastParameter != null)
        {
            var lastParamMeta = new MethodParameterMeta(lastParameter);
            if (lastParamMeta.IsCancellationToken)
                this.CancellationTokenParameter = lastParamMeta;
        }

        if (ReturnArity > 0)
        {
            var returnType = (INamedTypeSymbol)returnSymbol.TypeArguments[0];
            
            //this.ReturnType = "global::" + returnType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            this.ReturnType = returnType.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + returnType.Name;
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

        _hash.ComputeHash(Encoding.UTF8.GetBytes(Name));
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

