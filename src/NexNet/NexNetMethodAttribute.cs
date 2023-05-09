using System;

namespace NexNet;

/// <summary>
/// Attribute to configure the generator creation of the hub method implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Method,  AllowMultiple = false, Inherited = true)]
public class NexNetMethodAttribute : Attribute
{
    /// <summary>
    /// Manually specifies the ID of this method.  Used for invocations.
    /// Useful for maintaining backward compatibility with a changing interface.
    /// </summary>
    public ushort MethodId { get; init; } = 0;

    /// <summary>
    /// Ignore this method
    /// </summary>
    public bool Ignore { get; init; }


    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    /// <param name="methodId">Manually assigned method id for the attached method.
    /// Must be greater than 0, otherwise it will be auto-assigned.</param>
    public NexNetMethodAttribute(ushort methodId)
    {
        MethodId = methodId;
    }

    /// <summary>
    /// Creates a blank attribute.
    /// </summary>
    public NexNetMethodAttribute()
    {
    }

}
