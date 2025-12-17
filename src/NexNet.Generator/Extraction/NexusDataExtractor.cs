using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Generator.Models;

namespace NexNet.Generator.Extraction;

/// <summary>
/// Extracts all required data from semantic model into cacheable records.
/// This is the ONLY place where ISymbol access should occur in the transform phase.
/// </summary>
internal static class NexusDataExtractor
{
    private static readonly XxHash32 _hash = new XxHash32();

    /// <summary>
    /// Extracts generation data from a GeneratorAttributeSyntaxContext.
    /// Returns null if extraction fails or the input is invalid.
    /// </summary>
    public static NexusGenerationData? Extract(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetNode is not ClassDeclarationSyntax syntax)
            return null;

        if (context.TargetSymbol is not INamedTypeSymbol symbol)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        var typeHasher = new TypeHasher();

        // Extract nexus attribute data
        var nexusAttributeData = symbol.GetAttributes()
            .FirstOrDefault(att => att.AttributeClass?.Name == "NexusAttribute");

        if (nexusAttributeData?.AttributeClass is null)
            return null;

        var nexusInterfaceSymbol = nexusAttributeData.AttributeClass.TypeArguments[0] as INamedTypeSymbol;
        var proxyInterfaceSymbol = nexusAttributeData.AttributeClass.TypeArguments[1] as INamedTypeSymbol;

        if (nexusInterfaceSymbol is null || proxyInterfaceSymbol is null)
            return null;

        // Extract attribute data
        var nexusAttribute = ExtractNexusAttribute(symbol, nexusInterfaceSymbol, proxyInterfaceSymbol);
        if (nexusAttribute is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        // Extract interface data - need to build IDs for both
        var nexusInterface = ExtractInterfaceData(nexusInterfaceSymbol, nexusAttribute, typeHasher, cancellationToken);
        var proxyInterface = ExtractInterfaceData(proxyInterfaceSymbol, nexusAttribute, typeHasher, cancellationToken);

        // Extract class methods
        var classMethods = symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(m => ExtractMethodData(m, typeHasher, 0)) // ID 0 for class methods since they're not invokable
            .ToImmutableArray();

        var fullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_");

        return new NexusGenerationData(
            TypeName: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            FullTypeName: fullTypeName,
            Namespace: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", ""),
            NamespaceWithGlobal: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsValueType: symbol.IsValueType,
            IsRecord: symbol.IsRecord,
            IsAbstract: symbol.IsAbstract,
            IsGeneric: symbol.IsGenericType,
            IsPartial: syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            IsNested: syntax.Parent is TypeDeclarationSyntax,
            NexusAttribute: nexusAttribute,
            NexusInterface: nexusInterface,
            ProxyInterface: proxyInterface,
            ClassMethods: classMethods,
            ClassLocation: LocationData.FromSyntax(syntax)!,
            IdentifierLocation: LocationData.FromToken(syntax.Identifier)!
        );
    }

    private static NexusAttributeData? ExtractNexusAttribute(
        INamedTypeSymbol symbol,
        INamedTypeSymbol nexusInterfaceSymbol,
        INamedTypeSymbol proxyInterfaceSymbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "NexusAttribute");

        if (attr?.AttributeClass is null)
            return null;

        // Determine if server or client based on attribute constructor argument
        var isServer = false;
        var isClient = false;

        // Check constructor arguments for NexusType
        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int typeValue)
        {
            isClient = typeValue == 0;
            isServer = typeValue == 1;
        }

        // Also check named arguments
        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key == "NexusType" && namedArg.Value.Value is int namedTypeValue)
            {
                isClient = namedTypeValue == 0;
                isServer = namedTypeValue == 1;
            }
        }

        return new NexusAttributeData(
            IsServer: isServer,
            IsClient: isClient,
            NexusInterfaceTypeName: nexusInterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ProxyInterfaceTypeName: proxyInterfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        );
    }

    private static InvocationInterfaceData ExtractInterfaceData(
        INamedTypeSymbol symbol,
        NexusAttributeData nexusAttribute,
        TypeHasher typeHasher,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build interface hierarchy first to create method/collection tables
        var interfaceMap = new Dictionary<INamedTypeSymbol, InvocationInterfaceData>(SymbolEqualityComparer.Default);
        var allInterfaceSymbols = symbol.AllInterfaces.ToList();

        // Extract methods from this interface directly
        var directMethods = ExtractMethodsFromMembers(symbol.GetMembers(), typeHasher);

        // Collect all methods including inherited
        var allMethodsList = directMethods.ToList();
        foreach (var iface in allInterfaceSymbols)
        {
            allMethodsList.AddRange(ExtractMethodsFromMembers(iface.GetMembers(), typeHasher));
        }

        // Extract collections
        var directCollections = ExtractCollectionsFromMembers(symbol.GetMembers());
        var allCollectionsList = directCollections.ToList();
        foreach (var iface in allInterfaceSymbols)
        {
            allCollectionsList.AddRange(ExtractCollectionsFromMembers(iface.GetMembers()));
        }

        // Filter out ignored items
        allMethodsList = allMethodsList.Where(m => !m.MethodAttribute.Ignore).ToList();
        allCollectionsList = allCollectionsList.Where(c => !c.CollectionAttribute.Ignore).ToList();
        directMethods = directMethods.Where(m => !m.MethodAttribute.Ignore).ToImmutableArray();
        directCollections = directCollections.Where(c => !c.CollectionAttribute.Ignore).ToImmutableArray();

        // Convert to arrays for ID assignment
        var allMethods = allMethodsList.ToArray();
        var allCollections = allCollectionsList.ToArray();

        // Assign IDs
        AssignMethodIds(allMethods);
        AssignCollectionIds(allCollections, allMethods);

        // Update direct methods/collections with assigned IDs
        directMethods = UpdateMethodsWithIds(directMethods, allMethods);
        directCollections = UpdateCollectionsWithIds(directCollections, allCollections);

        // Extract version attribute
        var versionAttr = ExtractVersionAttribute(symbol);

        // Build shallow interface data for each inherited interface
        var interfaces = new List<InvocationInterfaceData>();
        var versions = new List<InvocationInterfaceData>();

        foreach (var interfaceSymbol in allInterfaceSymbols)
        {
            var ifaceData = ExtractInterfaceDataShallow(interfaceSymbol, nexusAttribute, typeHasher, allMethods, allCollections);
            interfaces.Add(ifaceData);
            interfaceMap[interfaceSymbol] = ifaceData;

            if (ifaceData.VersionAttribute?.AttributeExists == true)
            {
                versions.Add(ifaceData);
            }
        }

        // Compute hash
        var nexusHash = ComputeInterfaceHash(allMethods, allCollections);

        // Add the current interface to versions if it has a version attribute
        if (versionAttr?.AttributeExists == true)
        {
            var selfData = new InvocationInterfaceData(
                TypeName: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Namespace: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                NamespaceName: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", ""),
                ProxyImplName: nexusAttribute.IsClient ? "ServerProxy" : "ClientProxy",
                IsValueType: symbol.IsValueType,
                IsRecord: symbol.IsRecord,
                IsAbstract: symbol.IsAbstract,
                IsVersioning: true,
                VersionAttribute: versionAttr,
                Methods: directMethods,
                AllMethods: allMethods.ToImmutableArray(),
                Collections: directCollections,
                AllCollections: allCollections.ToImmutableArray(),
                Interfaces: ImmutableArray<InvocationInterfaceData>.Empty,
                Versions: ImmutableArray<InvocationInterfaceData>.Empty,
                Location: LocationData.FromSymbol(symbol)
            )
            {
                NexusHash = nexusHash
            };
            versions.Add(selfData);
        }

        var result = new InvocationInterfaceData(
            TypeName: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Namespace: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            NamespaceName: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", ""),
            ProxyImplName: nexusAttribute.IsClient ? "ServerProxy" : "ClientProxy",
            IsValueType: symbol.IsValueType,
            IsRecord: symbol.IsRecord,
            IsAbstract: symbol.IsAbstract,
            IsVersioning: versionAttr?.AttributeExists == true,
            VersionAttribute: versionAttr,
            Methods: directMethods,
            AllMethods: allMethods.ToImmutableArray(),
            Collections: directCollections,
            AllCollections: allCollections.ToImmutableArray(),
            Interfaces: interfaces.ToImmutableArray(),
            Versions: versions.ToImmutableArray(),
            Location: LocationData.FromSymbol(symbol)
        )
        {
            NexusHash = nexusHash
        };

        return result;
    }

    private static InvocationInterfaceData ExtractInterfaceDataShallow(
        INamedTypeSymbol symbol,
        NexusAttributeData nexusAttribute,
        TypeHasher typeHasher,
        MethodData[] rootAllMethods,
        CollectionData[] rootAllCollections)
    {
        // For shallow extraction, we need methods/collections from this interface AND all inherited interfaces
        // to correctly compute the hash (matching the old behavior).
        var interfaceMethods = ExtractMethodsFromMembers(symbol.GetMembers(), typeHasher)
            .Where(m => !m.MethodAttribute.Ignore)
            .ToList();

        var interfaceCollections = ExtractCollectionsFromMembers(symbol.GetMembers())
            .Where(c => !c.CollectionAttribute.Ignore)
            .ToList();

        // Also collect methods/collections from all inherited interfaces
        var allMethods = new List<MethodData>(interfaceMethods);
        var allCollections = new List<CollectionData>(interfaceCollections);

        foreach (var inherited in symbol.AllInterfaces)
        {
            var inheritedMethods = ExtractMethodsFromMembers(inherited.GetMembers(), typeHasher)
                .Where(m => !m.MethodAttribute.Ignore);
            allMethods.AddRange(inheritedMethods);

            var inheritedCollections = ExtractCollectionsFromMembers(inherited.GetMembers())
                .Where(c => !c.CollectionAttribute.Ignore);
            allCollections.AddRange(inheritedCollections);
        }

        // Update with IDs from root
        var methods = UpdateMethodsWithIds(interfaceMethods.ToImmutableArray(), rootAllMethods);
        var collections = UpdateCollectionsWithIds(interfaceCollections.ToImmutableArray(), rootAllCollections);
        var allMethodsWithIds = UpdateMethodsWithIds(allMethods.ToImmutableArray(), rootAllMethods);
        var allCollectionsWithIds = UpdateCollectionsWithIds(allCollections.ToImmutableArray(), rootAllCollections);

        var versionAttr = ExtractVersionAttribute(symbol);

        // Compute hash for this interface (including inherited methods/collections to match old behavior)
        var nexusHash = ComputeInterfaceHash(allMethodsWithIds.ToArray(), allCollectionsWithIds.ToArray());

        return new InvocationInterfaceData(
            TypeName: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Namespace: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            NamespaceName: symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", ""),
            ProxyImplName: nexusAttribute.IsClient ? "ServerProxy" : "ClientProxy",
            IsValueType: symbol.IsValueType,
            IsRecord: symbol.IsRecord,
            IsAbstract: symbol.IsAbstract,
            IsVersioning: versionAttr?.AttributeExists == true,
            VersionAttribute: versionAttr,
            Methods: methods,
            AllMethods: allMethodsWithIds, // Now includes inherited methods
            Collections: collections,
            AllCollections: allCollectionsWithIds, // Now includes inherited collections
            Interfaces: ImmutableArray<InvocationInterfaceData>.Empty,
            Versions: ImmutableArray<InvocationInterfaceData>.Empty,
            Location: LocationData.FromSymbol(symbol)
        )
        {
            NexusHash = nexusHash
        };
    }

    private static ImmutableArray<MethodData> ExtractMethodsFromMembers(
        ImmutableArray<ISymbol> members,
        TypeHasher typeHasher)
    {
        return members
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet))
            .Select(m => ExtractMethodData(m, typeHasher, 0))
            .ToImmutableArray();
    }

    private static MethodData ExtractMethodData(IMethodSymbol symbol, TypeHasher typeHasher, ushort id)
    {
        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;
        var isAsync = returnSymbol?.OriginalDefinition.Name == "ValueTask";

        var parameters = new List<MethodParameterData>();
        int cancellationTokenIndex = -1;
        int duplexPipeIndex = -1;
        int serializedCount = 0;
        int serializedId = 1;
        bool multiplePipes = false;
        bool multipleCancellationTokens = false;
        int pipeCount = 0;

        for (int i = 0; i < symbol.Parameters.Length; i++)
        {
            var param = ExtractParameterData(symbol.Parameters[i], i, typeHasher, ref serializedId);
            parameters.Add(param);

            if (param.SerializedType != null)
                serializedCount++;

            if (param.IsCancellationToken)
            {
                if (cancellationTokenIndex >= 0)
                    multipleCancellationTokens = true;
                cancellationTokenIndex = i;
            }

            if (param.UtilizesDuplexPipe)
            {
                duplexPipeIndex = i;
                pipeCount++;
                if (pipeCount > 1)
                    multiplePipes = true;
            }
        }

        var methodAttr = ExtractMethodAttribute(symbol);

        // Use attribute-specified ID or default
        var assignedId = methodAttr.MethodId ?? id;

        // Compute hash
        var hash = ComputeMethodHash(symbol, parameters, methodAttr);

        return new MethodData(
            Name: symbol.Name,
            Id: assignedId,
            IsStatic: symbol.IsStatic,
            IsAsync: isAsync,
            IsReturnVoid: returnSymbol?.Name == "Void",
            ReturnType: returnSymbol?.Arity > 0
                ? SymbolUtilities.GetFullSymbolType(returnSymbol, true)
                : null,
            ReturnTypeSource: returnSymbol?.Arity > 0
                ? returnSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                : null,
            ReturnArity: returnSymbol?.Arity ?? 0,
            Parameters: parameters.ToImmutableArray(),
            SerializedParameterCount: serializedCount,
            CancellationTokenParameterIndex: cancellationTokenIndex,
            DuplexPipeParameterIndex: duplexPipeIndex,
            UtilizesPipes: duplexPipeIndex >= 0,
            MultiplePipeParameters: multiplePipes,
            MultipleCancellationTokenParameters: multipleCancellationTokens,
            NexusHash: hash,
            MethodAttribute: methodAttr,
            Location: LocationData.FromSymbol(symbol)
        );
    }

    private static MethodParameterData ExtractParameterData(
        IParameterSymbol symbol,
        int index,
        TypeHasher typeHasher,
        ref int serializedId)
    {
        var paramType = SymbolUtilities.GetFullSymbolType(symbol.Type, false);
        var isCancellationToken = symbol.Type.Name == "CancellationToken";
        var isDuplexPipe = paramType == "global::NexNet.Pipes.INexusDuplexPipe";
        var isDuplexUnmanagedChannel = paramType.StartsWith("global::NexNet.Pipes.INexusDuplexUnmanagedChannel<");
        var isDuplexChannel = paramType.StartsWith("global::NexNet.Pipes.INexusDuplexChannel<");
        var utilizesDuplexPipe = isDuplexPipe || isDuplexUnmanagedChannel || isDuplexChannel;

        string? serializedType = null;
        string? serializedValue = null;
        string? channelType = null;
        int assignedSerializedId = 0;

        if (isDuplexPipe)
        {
            // Duplex Pipe is serialized as a byte
            serializedType = "global::System.Byte";
            serializedValue = $"__proxyInvoker.ProxyGetDuplexPipeInitialId({symbol.Name})";
            assignedSerializedId = serializedId++;
        }
        else if (isDuplexUnmanagedChannel || isDuplexChannel)
        {
            // Extract the channel type from the generic type argument
            var returnSymbol = symbol.Type as INamedTypeSymbol;
            channelType = SymbolUtilities.GetFullSymbolType(returnSymbol?.TypeArguments[0], false);
            // Duplex Pipe is serialized as a byte
            serializedType = "global::System.Byte";
            serializedValue = $"__proxyInvoker.ProxyGetDuplexPipeInitialId({symbol.Name}.BasePipe)";
            assignedSerializedId = serializedId++;
        }
        else if (!isCancellationToken)
        {
            serializedType = paramType;
            serializedValue = symbol.Name;
            assignedSerializedId = serializedId++;
        }

        return new MethodParameterData(
            Name: symbol.Name,
            Type: paramType,
            TypeSource: symbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            SerializedType: serializedType,
            SerializedValue: serializedValue,
            Index: index,
            SerializedId: assignedSerializedId,
            IsCancellationToken: isCancellationToken,
            IsDuplexPipe: isDuplexPipe,
            IsDuplexUnmanagedChannel: isDuplexUnmanagedChannel,
            IsDuplexChannel: isDuplexChannel,
            UtilizesDuplexPipe: utilizesDuplexPipe,
            ChannelType: channelType,
            NexusHashCode: typeHasher.GetHash(symbol.Type)
        );
    }

    private static ImmutableArray<CollectionData> ExtractCollectionsFromMembers(
        ImmutableArray<ISymbol> members)
    {
        return members
            .OfType<IPropertySymbol>()
            .Select(ExtractCollectionData)
            .ToImmutableArray();
    }

    private static CollectionData ExtractCollectionData(IPropertySymbol symbol)
    {
        var collectionAttr = ExtractCollectionAttribute(symbol);
        var methodAttr = ExtractMethodAttributeFromProperty(symbol);

        var returnSymbol = symbol.Type as INamedTypeSymbol;

        // Determine collection type
        var collectionType = returnSymbol?.OriginalDefinition.Name switch
        {
            "INexusList" => CollectionTypeValue.List,
            _ => CollectionTypeValue.Unset
        };

        string? itemType = null;
        string? itemTypeSource = null;

        if (returnSymbol?.Arity > 0)
        {
            itemType = SymbolUtilities.GetArityFullSymbolType(returnSymbol, 0);
            itemTypeSource = returnSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        // Compute hash
        var nexusHash = ComputeCollectionHash(symbol.Name, itemType, collectionAttr);

        return new CollectionData(
            Name: symbol.Name,
            Id: collectionAttr.Id ?? methodAttr?.MethodId ?? 0,
            PropertyType: symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            PropertyTypeSource: symbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ItemType: itemType,
            ItemTypeSource: itemTypeSource,
            CollectionType: collectionType,
            CollectionAttribute: collectionAttr,
            MethodAttribute: methodAttr,
            Location: LocationData.FromSymbol(symbol)
        )
        {
            NexusHash = nexusHash
        };
    }

    private static NexusMethodAttributeData ExtractMethodAttribute(IMethodSymbol symbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "NexusMethodAttribute");

        if (attr is null)
            return new NexusMethodAttributeData(false, null, false);

        ushort? methodId = null;
        bool ignore = false;

        // Check constructor arguments - handle both ushort and int types
        if (attr.ConstructorArguments.Length > 0)
        {
            var value = attr.ConstructorArguments[0].Value;
            if (value is ushort ushortId && ushortId != 0)
                methodId = ushortId;
            else if (value is int intId && intId != 0)
                methodId = (ushort)intId;
        }

        // Check named arguments
        foreach (var arg in attr.NamedArguments)
        {
            if (arg.Key == "MethodId")
            {
                var value = arg.Value.Value;
                if (value is ushort ushortId && ushortId != 0)
                    methodId = ushortId;
                else if (value is int intId && intId != 0)
                    methodId = (ushort)intId;
            }
            else if (arg.Key == "Ignore" && arg.Value.Value is bool ig)
                ignore = ig;
        }

        return new NexusMethodAttributeData(true, methodId, ignore);
    }

    private static NexusMethodAttributeData? ExtractMethodAttributeFromProperty(IPropertySymbol symbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "NexusMethodAttribute");

        if (attr is null)
            return null;

        ushort? methodId = null;
        bool ignore = false;

        // Check constructor arguments - handle both ushort and int types
        if (attr.ConstructorArguments.Length > 0)
        {
            var value = attr.ConstructorArguments[0].Value;
            if (value is ushort ushortId && ushortId != 0)
                methodId = ushortId;
            else if (value is int intId && intId != 0)
                methodId = (ushort)intId;
        }

        // Check named arguments
        foreach (var arg in attr.NamedArguments)
        {
            if (arg.Key == "MethodId")
            {
                var value = arg.Value.Value;
                if (value is ushort ushortId && ushortId != 0)
                    methodId = ushortId;
                else if (value is int intId && intId != 0)
                    methodId = (ushort)intId;
            }
            else if (arg.Key == "Ignore" && arg.Value.Value is bool ig)
                ignore = ig;
        }

        return new NexusMethodAttributeData(true, methodId, ignore);
    }

    private static NexusCollectionAttributeData ExtractCollectionAttribute(IPropertySymbol symbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "NexusCollectionAttribute");

        if (attr is null)
            return new NexusCollectionAttributeData(false, null, NexusCollectionMode.Unset, false);

        ushort? id = null;
        var mode = NexusCollectionMode.Unset;
        bool ignore = false;

        // Check constructor arguments - Mode is first (enum)
        if (attr.ConstructorArguments.Length > 0)
        {
            var modeValue = attr.ConstructorArguments[0].Value;
            if (modeValue is int modeInt)
            {
                var parsedMode = (NexusCollectionMode)modeInt;
                if (parsedMode != NexusCollectionMode.Unset)
                    mode = parsedMode;
            }
        }

        // Id is second constructor argument - handle both ushort and int
        if (attr.ConstructorArguments.Length > 1)
        {
            var idValue = attr.ConstructorArguments[1].Value;
            if (idValue is ushort ushortId && ushortId != 0)
                id = ushortId;
            else if (idValue is int intId && intId != 0)
                id = (ushort)intId;
        }

        // Check named arguments
        foreach (var arg in attr.NamedArguments)
        {
            if (arg.Key == "Id")
            {
                var idValue = arg.Value.Value;
                if (idValue is ushort ushortId && ushortId != 0)
                    id = ushortId;
                else if (idValue is int intId && intId != 0)
                    id = (ushort)intId;
            }
            else if (arg.Key == "Mode")
            {
                var modeValue = arg.Value.Value;
                if (modeValue is int namedMode)
                {
                    var parsedMode = (NexusCollectionMode)namedMode;
                    if (parsedMode != NexusCollectionMode.Unset)
                        mode = parsedMode;
                }
            }
            else if (arg.Key == "Ignore" && arg.Value.Value is bool ig)
                ignore = ig;
        }

        return new NexusCollectionAttributeData(true, id, mode, ignore);
    }

    private static VersionAttributeData? ExtractVersionAttribute(INamedTypeSymbol symbol)
    {
        var attr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "NexusVersionAttribute");

        if (attr is null)
            return null;

        string? version = null;
        int hash = 0;
        bool isHashSet = false;

        // Check constructor arguments
        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string versionStr)
        {
            version = versionStr;
        }

        if (attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is int hashVal)
        {
            isHashSet = true;
            hash = hashVal;
        }

        // Check named arguments
        foreach (var arg in attr.NamedArguments)
        {
            if (arg.Key == "Version" && arg.Value.Value is string namedVersion)
                version = namedVersion;
            else if (arg.Key == "HashLock" && arg.Value.Value is int namedHash)
            {
                isHashSet = true;
                hash = namedHash;
            }
        }

        return new VersionAttributeData(
            AttributeExists: true,
            Version: version,
            IsHashSet: isHashSet,
            Hash: hash,
            Location: LocationData.FromSymbol(symbol)
        );
    }

    private static void AssignMethodIds(MethodData[] methods)
    {
        var usedIds = new HashSet<ushort>();

        // First pass: collect pre-assigned IDs
        foreach (var method in methods)
        {
            if (method.MethodAttribute.MethodId.HasValue)
                usedIds.Add(method.MethodAttribute.MethodId.Value);
        }

        // Second pass: assign IDs to methods without them
        ushort nextId = 0;
        for (int i = 0; i < methods.Length; i++)
        {
            if (methods[i].Id == 0 && !methods[i].MethodAttribute.MethodId.HasValue)
            {
                while (usedIds.Contains(nextId))
                    nextId++;

                methods[i] = methods[i] with { Id = nextId };
                usedIds.Add(nextId++);
            }
        }
    }

    private static void AssignCollectionIds(CollectionData[] collections, MethodData[] methods)
    {
        var usedIds = new HashSet<ushort>(methods.Select(m => m.Id));

        // First pass: collect pre-assigned IDs
        foreach (var coll in collections)
        {
            if (coll.CollectionAttribute.Id.HasValue)
                usedIds.Add(coll.CollectionAttribute.Id.Value);
            else if (coll.MethodAttribute?.MethodId.HasValue == true)
                usedIds.Add(coll.MethodAttribute.MethodId.Value);
        }

        // Second pass: assign IDs
        ushort nextId = (ushort)(methods.Length > 0 ? methods.Max(m => m.Id) + 1 : 0);
        for (int i = 0; i < collections.Length; i++)
        {
            if (collections[i].Id == 0)
            {
                // Check if method attribute has an ID
                var methodAttrId = collections[i].MethodAttribute?.MethodId;
                if (methodAttrId.HasValue)
                {
                    collections[i] = collections[i] with { Id = methodAttrId.Value };
                    usedIds.Add(collections[i].Id);
                }
                else
                {
                    while (usedIds.Contains(nextId))
                        nextId++;

                    collections[i] = collections[i] with { Id = nextId };
                    usedIds.Add(nextId++);
                }
            }
        }
    }

    private static ImmutableArray<MethodData> UpdateMethodsWithIds(
        ImmutableArray<MethodData> methods,
        MethodData[] allMethods)
    {
        // Create lookup by name for matching
        var idLookup = allMethods.ToDictionary(m => m.Name, m => m.Id);

        return methods
            .Select(m => idLookup.TryGetValue(m.Name, out var id) ? m with { Id = id } : m)
            .ToImmutableArray();
    }

    private static ImmutableArray<CollectionData> UpdateCollectionsWithIds(
        ImmutableArray<CollectionData> collections,
        CollectionData[] allCollections)
    {
        // Create lookup by name, allowing for multiple collections with the same name (edge case)
        // This handles the case where duplicate property names exist (which is a C# error but still
        // needs to be handled gracefully for diagnostics)
        var idLookup = allCollections
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var result = new List<CollectionData>();
        var nameOccurrences = new Dictionary<string, int>();

        foreach (var c in collections)
        {
            // Track which occurrence of this name we're on
            if (!nameOccurrences.TryGetValue(c.Name, out var occurrence))
                occurrence = 0;
            nameOccurrences[c.Name] = occurrence + 1;

            // Get the ID for this occurrence
            if (idLookup.TryGetValue(c.Name, out var ids) && occurrence < ids.Count)
            {
                result.Add(c with { Id = ids[occurrence] });
            }
            else
            {
                result.Add(c);
            }
        }

        return result.ToImmutableArray();
    }

    private static int ComputeMethodHash(
        IMethodSymbol symbol,
        List<MethodParameterData> parameters,
        NexusMethodAttributeData methodAttr)
    {
        var hash = new HashCode();

        var returnSymbol = symbol.ReturnType as INamedTypeSymbol;
        if (returnSymbol?.Arity > 0)
        {
            var returnType = SymbolUtilities.GetFullSymbolType(returnSymbol, true);
            if (returnType != null)
                hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(returnType)));
        }

        hash.Add(methodAttr.MethodId ?? 0);

        // Add the name of the method to the hash if we do not have a manually specified ID
        if (!methodAttr.MethodId.HasValue)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(symbol.Name)));
        }

        foreach (var param in parameters)
            hash.Add(param.NexusHashCode);

        return hash.ToHashCode();
    }

    private static int ComputeCollectionHash(
        string name,
        string? itemType,
        NexusCollectionAttributeData collectionAttr)
    {
        var hash = new HashCode();

        if (itemType != null)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(itemType)));
        }

        // Add the name if we don't have a manually specified ID
        if (!collectionAttr.Id.HasValue)
        {
            hash.Add((int)_hash.ComputeHash(Encoding.UTF8.GetBytes(name)));
        }
        else
        {
            hash.Add(collectionAttr.Id.Value);
        }

        return hash.ToHashCode();
    }

    private static int ComputeInterfaceHash(
        MethodData[] methods,
        CollectionData[] collections)
    {
        var hash = new HashCode();

        foreach (var method in methods)
            hash.Add(method.NexusHash);

        foreach (var collection in collections)
            hash.Add(collection.NexusHash);

        return hash.ToHashCode();
    }
}
