using System;

namespace NexNet;

/// <summary>
/// Attribute to configure the generator creation of the hub method implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class NexNetMethodAttribute : Attribute
{
    /// <summary>
    /// Ignore this method
    /// </summary>
    public bool Ignore { get; init; }

    /// <summary>
    /// Manually specifies the ID of this method.  Used for invocations.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public ushort MethodId { get; init; }

    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    public NexNetMethodAttribute(bool ignore = false, ushort methodId = 0)
    {
        Ignore = ignore;
        MethodId = methodId;
    }

    /// <summary>
    /// Creates a blank attribute.
    /// </summary>
    public NexNetMethodAttribute()
    {
    }

}
