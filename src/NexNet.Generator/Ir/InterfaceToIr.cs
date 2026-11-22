using Microsoft.CodeAnalysis;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// Projects an <see cref="InvocationInterfaceData"/> into an <see cref="InterfaceIr"/>. Ids, hashes, versions and flags
/// come from the data, so they are exactly what the C# emitter writes; parameter and return types come from the
/// interface symbols, matched to the data in the extractor's member order.
/// </summary>
internal static class InterfaceToIr
{
    public static InterfaceIr Convert(InvocationInterfaceData data, INamedTypeSymbol symbol, ShapeToIr types, CancellationToken cancellationToken)
    {
        // The extractor lists the interface's own members, then each inherited interface's in ordinal display order.
        var declaringInterfaces = new[] { symbol }
            .Concat(symbol.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal))
            .ToArray();

        var methodSymbols = declaringInterfaces
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet))
            .ToList();

        var propertySymbols = declaringInterfaces
            .SelectMany(i => i.GetMembers().OfType<IPropertySymbol>())
            .ToList();

        var methods = new List<MethodIr>(data.AllMethods.Length);
        var next = 0;
        foreach (var method in data.AllMethods)
        {
            // Ignored members were filtered out of the data; skip their symbols.
            var match = FindNext(methodSymbols, ref next, s => s.Name == method.Name && s.Parameters.Length == method.Parameters.Length);
            methods.Add(ConvertMethod(method, match, types, cancellationToken));
        }

        var collections = new List<CollectionIr>(data.AllCollections.Length);
        next = 0;
        foreach (var collection in data.AllCollections)
        {
            var match = FindNext(propertySymbols, ref next, s => s.Name == collection.Name);
            collections.Add(ConvertCollection(collection, match, types, cancellationToken));
        }

        var versions = data.Versions
            .Select(v => new VersionIr(
                v.VersionAttribute?.Version ?? "",
                v.NexusHash,
                new EquatableArray<ushort>(v.AllMethods.Select(m => m.Id)
                    .Concat(v.AllCollections.Select(c => c.Id))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray())))
            .ToArray();

        return new InterfaceIr(
            ShapeToIr.DeclId(symbol),
            symbol.Name,
            symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            data.NexusHash,
            new EquatableArray<MethodIr>(methods.OrderBy(m => m.Id).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray()),
            new EquatableArray<CollectionIr>(collections.OrderBy(c => c.Id).ThenBy(c => c.Name, StringComparer.Ordinal).ToArray()),
            new EquatableArray<VersionIr>(versions));
    }

    private static T? FindNext<T>(List<T> symbols, ref int next, Func<T, bool> predicate)
        where T : class
    {
        for (var i = next; i < symbols.Count; i++)
        {
            if (predicate(symbols[i]))
            {
                next = i + 1;
                return symbols[i];
            }
        }

        return null;
    }

    private static MethodIr ConvertMethod(MethodData method, IMethodSymbol? symbol, ShapeToIr types, CancellationToken cancellationToken)
    {
        var returnKind = ReturnKind.Void;
        TypeRef? returnType = null;
        if (symbol?.ReturnType is INamedTypeSymbol returnSymbol)
        {
            if (returnSymbol.Arity == 1 && returnSymbol.ConstructedFrom.MetadataName == "ValueTask`1")
            {
                returnKind = ReturnKind.TaskOf;
                returnType = IrJsNumbers.MarkIf(types.Convert(returnSymbol.TypeArguments[0]), IrJsNumbers.Has(symbol.GetReturnTypeAttributes()));
            }
            else if (returnSymbol.MetadataName == "ValueTask")
            {
                returnKind = ReturnKind.Task;
            }
        }

        var parameters = new List<ParamIr>(method.Parameters.Length);
        var pipeKind = PipeKind.None;
        TypeRef? pipeType = null;
        foreach (var p in method.Parameters)
        {
            var parameterSymbol = symbol != null && p.Index < symbol.Parameters.Length ? symbol.Parameters[p.Index] : null;
            if (p.IsCancellationToken)
            {
                parameters.Add(new ParamIr(p.Name, ParamKind.CancellationToken, null, 0, false));
            }
            else if (p.IsDuplexPipe)
            {
                parameters.Add(new ParamIr(p.Name, ParamKind.DuplexPipe, null, p.SerializedId, false));
                if (pipeKind == PipeKind.None)
                    pipeKind = PipeKind.DuplexPipe;
            }
            else if (p.IsDuplexChannel)
            {
                TypeRef? item = null;
                if (parameterSymbol?.Type is INamedTypeSymbol { TypeArguments.Length: 1 } channel)
                    item = IrJsNumbers.MarkIf(types.Convert(channel.TypeArguments[0]), IrJsNumbers.Has(parameterSymbol));
                parameters.Add(new ParamIr(p.Name, ParamKind.DuplexChannel, item, p.SerializedId, false));
                pipeKind = PipeKind.Channel;
                pipeType ??= item;
            }
            else
            {
                var type = parameterSymbol != null ? IrJsNumbers.MarkIf(types.Convert(parameterSymbol.Type), IrJsNumbers.Has(parameterSymbol)) : null;
                parameters.Add(new ParamIr(p.Name, ParamKind.Value, type, p.SerializedId, type?.MayBeNull == true));
            }
        }

        return new MethodIr(
            method.Name,
            method.Id,
            method.NexusHash,
            returnKind,
            returnType,
            new EquatableArray<ParamIr>(parameters.ToArray()),
            method.SerializedParameterCount,
            method.CancellationTokenParameterIndex >= 0,
            pipeKind,
            pipeType,
            method.RunsInSeparatePool,
            method.AuthorizeData != null,
            symbol != null ? IrDocs.FromSymbol(symbol, cancellationToken) : null,
            method.Location);
    }

    private static CollectionIr ConvertCollection(CollectionData collection, IPropertySymbol? symbol, ShapeToIr types, CancellationToken cancellationToken)
    {
        TypeRef? item = null;
        if (symbol?.Type is INamedTypeSymbol { Arity: > 0 } propertyType)
            item = IrJsNumbers.MarkIf(types.Convert(propertyType.TypeArguments[0]), IrJsNumbers.Has(symbol));

        return new CollectionIr(
            collection.Name,
            collection.Id,
            collection.NexusHash,
            collection.CollectionType == CollectionTypeValue.List ? CollectionKind.List : CollectionKind.Unset,
            collection.CollectionAttribute.Mode switch
            {
                NexusCollectionMode.ServerToClient => CollectionMode.ServerToClient,
                NexusCollectionMode.BiDirectional => CollectionMode.BiDirectional,
                NexusCollectionMode.Relay => CollectionMode.Relay,
                _ => CollectionMode.Unset,
            },
            item,
            collection.AuthorizeData != null,
            symbol != null ? IrDocs.FromSymbol(symbol, cancellationToken) : null);
    }
}
