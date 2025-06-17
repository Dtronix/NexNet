using System;

namespace NexNet.Collections;

/// <summary>
/// Attribute to configure the creation of the nexus property implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class NexusCollectionAttribute : Attribute
{
    /// <summary>
    /// Manually specifies the ID of this collection.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public ushort Id { get; init; } = 0;

    /// <summary>
    /// Mode of the collection operation.
    /// </summary>
    public NexusCollectionMode Mode { get; init; } = NexusCollectionMode.ServerToClient;
    
    /// <summary>
    /// Ignore this collection from the generated interface.
    /// </summary>
    public bool Ignore { get; init; }

    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    /// <param name="mode">Mode of the collection operation.</param>
    /// <param name="id">Manually assigned id for the attached method.  Cannot overlap with other ids.
    /// Must be greater than 0, otherwise it will be auto-assigned.</param>
    public NexusCollectionAttribute(NexusCollectionMode mode, ushort id)
    {
        Mode = mode;
        Id = id;
    }
    
    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    /// <param name="mode">Mode of the collection operation.</param>
    public NexusCollectionAttribute(NexusCollectionMode mode)
    {
        Mode = mode;
    }

    /// <summary>
    /// Creates a blank attribute.
    /// </summary>
    public NexusCollectionAttribute()
    {
    }

}
