using Microsoft.CodeAnalysis;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// Builds the IR. Each producer (a nexus class, a [NexusObject] type, the assembly attributes) builds an
/// <see cref="IrFragment"/> in the transform phase; <see cref="Merge"/> combines the fragments of a compilation into the
/// single <see cref="NexusIr"/>, independent of producer order.
/// </summary>
internal static class IrBuilder
{
    /// <summary>
    /// The fragment of one nexus class: its export (the server and client APIs seen from the client) and every type
    /// both APIs reach. <paramref name="data"/> must be the extractor's data for <paramref name="nexusClass"/>.
    /// </summary>
    public static IrFragment Build(Compilation compilation, INamedTypeSymbol nexusClass, NexusGenerationData data, CancellationToken cancellationToken = default)
        => Build(new ShapeBuilder(compilation), nexusClass, data, cancellationToken);

    /// <inheritdoc cref="Build(Compilation, INamedTypeSymbol, NexusGenerationData, CancellationToken)"/>
    public static IrFragment Build(ShapeBuilder shapes, INamedTypeSymbol nexusClass, NexusGenerationData data, CancellationToken cancellationToken = default)
    {
        var attribute = nexusClass.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass is { Name: "NexusAttribute", TypeArguments.Length: 2 });
        if (attribute?.AttributeClass?.TypeArguments[0] is not INamedTypeSymbol nexusInterface
            || attribute.AttributeClass.TypeArguments[1] is not INamedTypeSymbol proxyInterface)
            return IrFragment.Empty;

        var isServer = data.NexusAttribute.IsServer;
        var serverData = isServer ? data.NexusInterface : data.ProxyInterface;
        var clientData = isServer ? data.ProxyInterface : data.NexusInterface;
        var serverSymbol = isServer ? nexusInterface : proxyInterface;
        var clientSymbol = isServer ? proxyInterface : nexusInterface;

        var types = new ShapeToIr(shapes, cancellationToken);
        var serverApi = InterfaceToIr.Convert(serverData, serverSymbol, types, cancellationToken);
        var clientApi = InterfaceToIr.Convert(clientData, clientSymbol, types, cancellationToken);

        var export = new ExportIr(
            nexusClass.Name,
            nexusClass.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            Targets: 0,
            serverApi,
            clientApi,
            Handshake(serverData, clientData),
            ClientAcceptedIds(clientData),
            IrDocs.FromSymbol(nexusClass, cancellationToken),
            data.IdentifierLocation);

        return new IrFragment(new EquatableArray<ExportIr>(new[] { export }), types.GetDecls());
    }

    /// <summary>The fragment of standalone types (a [NexusObject] type, a type used only at runtime): their declarations.</summary>
    public static IrFragment BuildTypes(ShapeBuilder shapes, IEnumerable<ITypeSymbol> roots, CancellationToken cancellationToken = default)
    {
        var types = new ShapeToIr(shapes, cancellationToken);
        foreach (var root in roots)
            types.Convert(root);
        return new IrFragment(EquatableArray<ExportIr>.Empty, types.GetDecls());
    }

    /// <summary>The fragment of the assembly's <c>[assembly: NexusSerializable&lt;T&gt;]</c> declarations.</summary>
    public static IrFragment BuildAssemblyDeclared(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var roots = new List<ITypeSymbol>();
        foreach (var attr in compilation.Assembly.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is { IsGenericType: true, Name: "NexusSerializableAttribute" } && cls.TypeArguments.Length == 1
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization")
                roots.Add(cls.TypeArguments[0]);
        }

        return roots.Count == 0
            ? IrFragment.Empty
            : BuildTypes(new ShapeBuilder(compilation), roots, cancellationToken);
    }

    /// <summary>
    /// Merges fragments into the compilation's IR. Exports are ordered by namespace then name and types by id (ordinal);
    /// a type reached by several producers appears once. The result does not depend on the order of the fragments.
    /// </summary>
    public static NexusIr Merge(IEnumerable<IrFragment> fragments)
    {
        var list = fragments.ToList();

        var exports = list
            .SelectMany(f => f.Exports)
            .GroupBy(e => (e.ClrNamespace, e.Name))
            .Select(g => PickOne(g.ToList(), IrJson.WriteExport))
            .OrderBy(e => e.ClrNamespace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();

        var types = list
            .SelectMany(f => f.Types)
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => PickOne(g.ToList(), IrJson.WriteDecl))
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToArray();

        return new NexusIr(NexusIr.CurrentVersion, new EquatableArray<ExportIr>(exports), new EquatableArray<TypeDeclIr>(types));
    }

    // Producers build the same item for the same key; should they ever differ, the choice must still be order independent.
    private static T PickOne<T>(List<T> items, Func<T, string> key)
        where T : IEquatable<T>
    {
        var first = items[0];
        if (items.All(i => i.Equals(first)))
            return first;
        return items.OrderBy(key, StringComparer.Ordinal).First();
    }

    /// <summary>The client greeting values, derived exactly as <c>NexusSession.StartAsClient</c> does.</summary>
    internal static HandshakeIr Handshake(InvocationInterfaceData server, InvocationInterfaceData client)
    {
        // The client proxy's version table holds only the server API's last version.
        var last = server.Versions.Length > 0 ? server.Versions[server.Versions.Length - 1] : null;
        return new HandshakeIr(
            last == null ? null : last.VersionAttribute?.Version ?? "",
            last?.NexusHash ?? server.NexusHash,
            client.NexusHash);
    }

    /// <summary>
    /// What a client nexus accepts in incoming invocations (its <c>VersionMethodHashSet</c>): every method and
    /// collection id of the client API, checked against the client API hash.
    /// </summary>
    internal static EquatableArray<AcceptedIdIr> ClientAcceptedIds(InvocationInterfaceData client)
    {
        return new EquatableArray<AcceptedIdIr>(client.AllMethods.Select(m => m.Id)
            .Concat(client.AllCollections.Select(c => c.Id))
            .Distinct()
            .OrderBy(id => id)
            .Select(id => new AcceptedIdIr(client.NexusHash, id))
            .ToArray());
    }
}
