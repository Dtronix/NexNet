using System.Collections.Immutable;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Models;

/// <summary>
/// Root data container for a single Nexus class generation.
/// All data extracted from semantic model - no ISymbol references.
/// </summary>
internal sealed record NexusGenerationData(
    // Type identification
    string TypeName,                    // MinimallyQualifiedFormat
    string FullTypeName,                // For file naming (escaped)
    string Namespace,                   // FullyQualifiedFormat without "global::"
    string NamespaceWithGlobal,         // FullyQualifiedFormat with "global::"

    // Type characteristics
    bool IsValueType,
    bool IsRecord,
    bool IsAbstract,
    bool IsGeneric,
    bool IsPartial,
    bool IsNested,

    // Attribute data
    NexusAttributeData NexusAttribute,

    // Interface data
    InvocationInterfaceData NexusInterface,
    InvocationInterfaceData ProxyInterface,

    // Methods declared directly on the nexus class
    ImmutableArray<MethodData> ClassMethods,

    // Diagnostic locations
    LocationData ClassLocation,
    LocationData IdentifierLocation
)
{
    /// <summary>
    /// Generated MessagePack formatter classes and registrations for every type reachable from the nexus
    /// (emitted only with the MessagePack serializer backend).
    /// </summary>
    public string SerializationCode { get; init; } = string.Empty;

    /// <summary>
    /// Serialization diagnostics (reported only with the MessagePack serializer backend).
    /// </summary>
    public ImmutableArray<SerializationDiagnostic> SerializationDiagnostics { get; init; } = ImmutableArray<SerializationDiagnostic>.Empty;
}
