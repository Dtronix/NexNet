using System.Collections.Immutable;

namespace NexNet.Generator.Models;

/// <summary>
/// Data for a nexus/proxy interface and all its inherited interfaces.
/// </summary>
internal sealed record InvocationInterfaceData(
    // Identification
    string TypeName,                    // MinimallyQualifiedFormat
    string Namespace,                   // FullyQualifiedFormat
    string NamespaceName,               // Without "global::"
    string ProxyImplName,               // "ServerProxy" or "ClientProxy"

    // Type characteristics
    bool IsValueType,
    bool IsRecord,
    bool IsAbstract,
    bool IsVersioning,

    // Version attribute (if present)
    VersionAttributeData? VersionAttribute,

    // Methods directly on this interface
    ImmutableArray<MethodData> Methods,

    // All methods including inherited
    ImmutableArray<MethodData> AllMethods,

    // Collections directly on this interface
    ImmutableArray<CollectionData> Collections,

    // All collections including inherited
    ImmutableArray<CollectionData> AllCollections,

    // Inherited interfaces (for version tree)
    ImmutableArray<InvocationInterfaceData> Interfaces,

    // Version hierarchy
    ImmutableArray<InvocationInterfaceData> Versions,

    // For diagnostics
    LocationData? Location
)
{
    /// <summary>
    /// Computed hash of interface shape for version locking.
    /// </summary>
    public int NexusHash { get; init; }
}
