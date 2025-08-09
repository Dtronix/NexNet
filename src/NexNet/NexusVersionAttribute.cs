using System;

namespace NexNet;

/// <summary>
/// Indicates the version for the attributed Nexus interface.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class NexusVersionAttribute : Attribute
{
    /// <summary>
    /// Gets the version identifier that the attributed nexus interface is.  Must be 32 characters or less.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Locked in hash for the version. Used to ensure the nexus members and member's arguments remains constant.
    /// </summary>
    public int HashLock { get; init; } = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusVersionAttribute"/> class.
    /// </summary>
    public NexusVersionAttribute()
    {
    }
}
