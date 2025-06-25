using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Generator.MemoryPack;

namespace NexNet.Generator.MetaGenerator;

internal partial class NexusMeta
{
    public INamedTypeSymbol Symbol { get; set; }
    public ReferenceSymbols MemoryPackReference { get; }

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

    public NexusMeta(INamedTypeSymbol symbol, ReferenceSymbols memoryPackReference)
    {
        
        this.Symbol = symbol;
        this.MemoryPackReference = memoryPackReference;
        this.TypeName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        this.Namespace = Symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        this.NexusAttribute = new NexusAttributeMeta(symbol);

        var nexusAttributeData = symbol.GetAttributes().First(att => att.AttributeClass!.Name == "NexusAttribute");
        NexusInterface = new InvocationInterfaceMeta(
            nexusAttributeData.AttributeClass!.TypeArguments[0] as INamedTypeSymbol, NexusAttribute, null, MemoryPackReference);
        ProxyInterface = new InvocationInterfaceMeta(
            nexusAttributeData.AttributeClass!.TypeArguments[1] as INamedTypeSymbol, NexusAttribute, null, MemoryPackReference);
        
        // Build the versioning trees and method ids.
        NexusInterface.BuildVersions();
        NexusInterface.BuildMethodIds();
        ProxyInterface.BuildVersions();
        ProxyInterface.BuildMethodIds();
        

        this.IsValueType = symbol.IsValueType;
        this.IsInterfaceOrAbstract = symbol.IsAbstract;
        this.IsRecord = symbol.IsRecord;
        this.Methods = Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(x => new MethodMeta(x, memoryPackReference))
            .ToArray();
    }


    public bool Validate(TypeDeclarationSyntax syntax, NexusGenerator.GeneratorContext context)
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
        foreach (var method in this.NexusInterface.AllMethods!)
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
        
                    
        //Validate all the interface version information
        foreach (var nexusInterface in this.NexusInterface.Interfaces.Concat([this.NexusInterface]))
        {
            if (nexusInterface.VersionAttribute.AttributeExists)
            {
                var hash = nexusInterface.GetNexusHash();
                if (nexusInterface.VersionAttribute.IsHashSet)
                {
                    if (nexusInterface.VersionAttribute.Hash != hash)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.VersionHashLockMismatch,
                            nexusInterface.VersionAttribute.GetLocation(nexusLocation),
                            nexusInterface.TypeName,
                            hash));
                        failed = true;
                    }
                }
                else
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.VersionHashLockNotSet,
                        nexusInterface.VersionAttribute.GetLocation(nexusLocation),
                        nexusInterface.TypeName,
                        hash));
                }
            }
        }


        return !failed;
    }
}
