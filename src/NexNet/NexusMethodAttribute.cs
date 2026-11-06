using System;

namespace NexNet;

/// <summary>
/// Attribute to configure the creation of the nexus method implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property,  AllowMultiple = false, Inherited = true)]
public class NexusMethodAttribute : Attribute
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
    /// Runs invocations of this method in the session's separate invocation pool, capped by
    /// <see cref="NexNet.Transports.ConfigBase.MaxSeparatePoolInvocations"/> and with its own queue, instead of taking
    /// one of the <see cref="NexNet.Transports.ConfigBase.MaxConcurrentConnectionInvocations"/> slots. Use it for slow
    /// or long-lived plain methods, so they never hold back the fast calls on the same connection.
    /// </summary>
    /// <remarks>
    /// This is not <see cref="System.Threading.Tasks.TaskCreationOptions.LongRunning"/>: no dedicated thread is
    /// created and the method runs on the thread pool like any other. The method must be truly asynchronous; one that
    /// blocks synchronously holds a thread-pool thread for its whole run. Decided by the side that executes the method;
    /// it does not change the method hash or anything on the wire. No effect on a method with a pipe or channel
    /// parameter, which frees its execution slot once the pipe is registered (warning NEXNET039), or on a collection
    /// property.
    /// </remarks>
    public bool SeparatePool { get; init; }

    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    /// <param name="methodId">Manually assigned method id for the attached method.
    /// Must be greater than 0, otherwise it will be auto-assigned.</param>
    public NexusMethodAttribute(ushort methodId)
    {
        MethodId = methodId;
    }

    /// <summary>
    /// Creates a blank attribute.
    /// </summary>
    public NexusMethodAttribute()
    {
    }

}
