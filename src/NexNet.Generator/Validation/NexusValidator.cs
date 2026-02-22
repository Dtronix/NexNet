using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using NexNet.Generator.Models;

namespace NexNet.Generator.Validation;

/// <summary>
/// Validates NexusGenerationData and produces diagnostics.
/// Works entirely with cached data - no semantic model access.
/// </summary>
internal static class NexusValidator
{
    public static ImmutableArray<Diagnostic> Validate(
        NexusGenerationData data,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        cancellationToken.ThrowIfCancellationRequested();

        // Basic type validation
        if (!data.IsPartial)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.MustBePartial,
                data.IdentifierLocation,
                data.TypeName));
        }

        if (data.IsNested)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.NestedNotAllow,
                data.IdentifierLocation,
                data.TypeName));
        }

        if (data.IsGeneric)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.NexusMustNotBeGeneric,
                data.IdentifierLocation,
                data.TypeName));
        }

        if (data.IsAbstract)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.MustNotBeAbstractOrInterface,
                data.IdentifierLocation,
                data.TypeName));
        }

        // Check for reserved method names
        var reservedMethod = data.ClassMethods
            .FirstOrDefault(m => m.Name == "InvokeMethodCore");
        if (reservedMethod != null)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.InvokeMethodCoreReservedMethodName,
                data.IdentifierLocation,
                reservedMethod.Name));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Validate versioning consistency
        ValidateVersioning(data, diagnostics);

        // Validate methods
        ValidateMethods(data, diagnostics, cancellationToken);

        // Validate collections
        ValidateCollections(data, diagnostics);

        // Validate version hashes
        ValidateVersionHashes(data, diagnostics);

        // Validate authorization
        ValidateAuthorization(data, diagnostics);

        return diagnostics.ToImmutable();
    }

    private static void ValidateVersioning(
        NexusGenerationData data,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var isVersioning = data.NexusInterface.IsVersioning ||
            data.NexusInterface.Interfaces.Any(i => i.IsVersioning);

        // Check all interfaces have consistent versioning
        foreach (var iface in data.NexusInterface.Interfaces)
        {
            if (iface.IsVersioning != isVersioning)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.AllInterfacesMustBeVersioning,
                    iface.Location ?? data.IdentifierLocation,
                    iface.TypeName));
            }
        }

        if (data.NexusInterface.IsVersioning != isVersioning)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.AllInterfacesMustBeVersioning,
                data.NexusInterface.Location ?? data.IdentifierLocation,
                data.NexusInterface.TypeName));
        }

        // If versioning, validate all methods have IDs
        if (isVersioning)
        {
            foreach (var method in data.NexusInterface.AllMethods)
            {
                if (!method.MethodAttribute.AttributeExists)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.AllMethodsIdsShallBeSetForVersioningNexuses,
                        method.Location ?? data.IdentifierLocation,
                        method.Name));
                }
                else if (method.MethodAttribute.MethodId is 0 or null)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.AllMethodsIdsShallNotBe0ForVersioningNexuses,
                        method.Location ?? data.IdentifierLocation,
                        method.Name,
                        method.MethodAttribute.MethodId?.ToString() ?? "NULL"));
                }
            }

            foreach (var collection in data.NexusInterface.AllCollections)
            {
                var methodAttr = collection.MethodAttribute;
                if (methodAttr is null || !methodAttr.AttributeExists)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.AllMethodsIdsShallBeSetForVersioningNexuses,
                        collection.Location ?? data.IdentifierLocation,
                        collection.Name));
                }
                else if (methodAttr.MethodId is 0 or null)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.AllMethodsIdsShallNotBe0ForVersioningNexuses,
                        collection.Location ?? data.IdentifierLocation,
                        collection.Name,
                        methodAttr.MethodId?.ToString() ?? "NULL"));
                }
            }
        }
    }

    private static void ValidateMethods(
        NexusGenerationData data,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var usedIds = new HashSet<ushort>();

        foreach (var method in data.NexusInterface.AllMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var location = method.Location ?? data.IdentifierLocation;

            // Duplicate ID check
            if (usedIds.Contains(method.Id))
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.DuplicatedMethodId,
                    location,
                    method.Name));
            }
            usedIds.Add(method.Id);

            // Cancellation token must be last
            if (method.CancellationTokenParameterIndex >= 0 &&
                method.CancellationTokenParameterIndex != method.Parameters.Length - 1)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.InvalidCancellationToken,
                    location,
                    method.Name));
            }

            // Only one cancellation token
            if (method.MultipleCancellationTokenParameters)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.TooManyCancellationTokens,
                    location,
                    method.Name));
            }

            // Only one pipe
            if (method.MultiplePipeParameters)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.TooManyPipes,
                    location,
                    method.Name));
            }

            // Void methods can't have cancellation token
            if (method.IsReturnVoid && method.CancellationTokenParameterIndex >= 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.CancellationTokenOnVoid,
                    location,
                    method.Name));
            }

            // Pipe validation
            if (method.UtilizesPipes)
            {
                // Validates the return type of the Nexus Interface method and returns a valueless
                // response if the method is async and has only one type argument or if the method has no
                // return type at all.
                if (method.IsReturnVoid || (method.IsAsync && method.ReturnArity == 1))
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.PipeOnVoidOrReturnTask,
                        location,
                        method.Name));
                }

                // This code block checks if a method has both a duplex pipe parameter and a cancellation token parameter.
                // This ensures that the method doesn't consume both a pipe and a cancellation token at the same time.
                if (method.CancellationTokenParameterIndex >= 0)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.PipeOnMethodWithCancellationToken,
                        location,
                        method.Name));
                }
            }

            // Return type validation
            if (!method.IsReturnVoid && !(method.IsAsync && method.ReturnArity <= 1))
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.InvalidReturnValue,
                    location,
                    method.Name));
            }
        }
    }

    private static void ValidateCollections(
        NexusGenerationData data,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var usedIds = new HashSet<ushort>(data.NexusInterface.AllMethods.Select(m => m.Id));

        foreach (var collection in data.NexusInterface.Collections)
        {
            var location = collection.Location ?? data.IdentifierLocation;

            // Collections only on server
            if (!data.NexusAttribute.IsServer)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.CollectionCanNotBeOnClient,
                    location,
                    collection.Name));
                continue;
            }

            // Duplicate ID check
            if (usedIds.Contains(collection.Id))
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.DuplicatedMethodId,
                    location,
                    collection.Name));
            }
            usedIds.Add(collection.Id);

            // Unknown collection type
            if (collection.CollectionType == CollectionTypeValue.Unset)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.CollectionUnknownType,
                    location,
                    collection.Name));
            }

            // Mode validation
            if (collection.CollectionAttribute.Mode == NexusCollectionMode.Unset ||
                !Enum.IsDefined(typeof(NexusCollectionMode), collection.CollectionAttribute.Mode))
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.CollectionUnknownMode,
                    location,
                    collection.Name));
            }

            // Attribute required for known collection types
            if (!collection.CollectionAttribute.AttributeExists &&
                collection.CollectionType != CollectionTypeValue.Unset)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.CollectionAttributeMissing,
                    location,
                    collection.Name));
            }
        }
    }

    private static void ValidateVersionHashes(
        NexusGenerationData data,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var allInterfaces = data.NexusInterface.Interfaces
            .Concat(new[] { data.NexusInterface });

        foreach (var iface in allInterfaces)
        {
            if (iface.VersionAttribute?.AttributeExists == true &&
                iface.VersionAttribute.IsHashSet)
            {
                if (iface.VersionAttribute.Hash != iface.NexusHash)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DiagnosticDescriptors.VersionHashLockMismatch,
                        iface.VersionAttribute.Location ?? data.IdentifierLocation,
                        iface.TypeName,
                        iface.NexusHash.ToString()));
                }
            }
        }
    }

    private static void ValidateAuthorization(
        NexusGenerationData data,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var authorizedMethods = data.NexusInterface.AllMethods
            .Where(m => m.AuthorizeData != null)
            .ToList();

        var authorizedCollections = data.NexusInterface.AllCollections
            .Where(c => c.AuthorizeData != null)
            .ToList();

        if (authorizedMethods.Count == 0 && authorizedCollections.Count == 0)
            return;

        // Client nexus error
        if (data.NexusAttribute.IsClient)
        {
            foreach (var method in authorizedMethods)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.AuthorizeOnClientNexus,
                    method.Location ?? data.IdentifierLocation,
                    method.Name));
            }
            foreach (var collection in authorizedCollections)
            {
                diagnostics.Add(CreateDiagnostic(
                    DiagnosticDescriptors.AuthorizeOnClientNexus,
                    collection.Location ?? data.IdentifierLocation,
                    collection.Name));
            }
            return;
        }

        // OnAuthorize not overridden warning
        var hasOnAuthorizeOverride = data.ClassMethods.Any(m => m.Name == "OnAuthorize");
        if (!hasOnAuthorizeOverride)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.AuthorizeWithoutOnAuthorize,
                data.IdentifierLocation,
                data.TypeName));
        }

        // Mixed permission enum types — collect from both methods and collections
        var enumTypes = authorizedMethods
            .Select(m => m.AuthorizeData!.PermissionEnumFullyQualifiedName)
            .Concat(authorizedCollections.Select(c => c.AuthorizeData!.PermissionEnumFullyQualifiedName))
            .Distinct()
            .ToList();

        if (enumTypes.Count > 1)
        {
            diagnostics.Add(CreateDiagnostic(
                DiagnosticDescriptors.MixedPermissionEnumTypes,
                data.IdentifierLocation,
                data.TypeName));
        }
    }

    private static Diagnostic CreateDiagnostic(
        DiagnosticDescriptor descriptor,
        LocationData location,
        params object[] args)
    {
        // Note: In the output phase, we don't have access to compilation
        // to reconstruct the full Location. Using Location.None with
        // file path in the message is acceptable.
        // In future, we could pass syntax trees to reconstruct locations.
        return Diagnostic.Create(descriptor, Location.None, args);
    }
}
