namespace NexNet.Generator.Models;

/// <summary>
/// The type of collection (e.g., List, Dictionary, etc.).
/// </summary>
internal enum CollectionTypeValue
{
    Unset = 0,
    List = 1
}

/// <summary>
/// Data for a synchronized collection property.
/// </summary>
internal sealed record CollectionData(
    string Name,
    ushort Id,
    string PropertyType,                // Full property type
    string PropertyTypeSource,          // MinimallyQualifiedFormat
    string? ItemType,                   // Generic item type
    string? ItemTypeSource,
    CollectionTypeValue CollectionType,
    NexusCollectionAttributeData CollectionAttribute,
    NexusMethodAttributeData? MethodAttribute,
    LocationData? Location
)
{
    public int NexusHash { get; init; }
}
