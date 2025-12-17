namespace NexNet.Generator.Models;

/// <summary>
/// Data from [Nexus&lt;TServer, TClient&gt;] attribute.
/// </summary>
internal sealed record NexusAttributeData(
    bool IsServer,
    bool IsClient,
    string NexusInterfaceTypeName,
    string ProxyInterfaceTypeName
);

/// <summary>
/// Data from [NexusMethod] attribute.
/// </summary>
internal sealed record NexusMethodAttributeData(
    bool AttributeExists,
    ushort? MethodId,
    bool Ignore
);

/// <summary>
/// Data from [NexusCollection] attribute.
/// </summary>
internal sealed record NexusCollectionAttributeData(
    bool AttributeExists,
    ushort? Id,
    NexusCollectionMode Mode,
    bool Ignore
);

/// <summary>
/// Data from [NexusVersion] attribute.
/// </summary>
internal sealed record VersionAttributeData(
    bool AttributeExists,
    string? Version,
    bool IsHashSet,
    int Hash,
    LocationData? Location
);
