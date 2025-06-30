using System;

namespace NexNet;

/// <summary>
/// Mode of the nexus.
/// </summary>
public enum NexusType
{
    /// <summary>
    /// Nexus is operating in client mode.
    /// </summary>
    Client = 0,

    /// <summary>
    /// Nexus is operating in server mode.
    /// </summary>
    Server = 1
}

/// <summary>
/// Nexus versioning configuration.
/// </summary>
public enum NexusVersioning
{
    /// <summary>
    /// The methods of the server and client must match exactly, otherwise the client can't connect.
    /// </summary>
    MustMatch = 0,

    /// <summary>
    /// Client will specify a version and the server will verify the validity upon connection.
    /// </summary>
    Negotiation = 1
}

/// <summary>
/// Attribute to allow the generator to create the nexus and proxy implementation.
/// </summary>
/// <typeparam name="TNexusInterface">Nexus interface.</typeparam>
/// <typeparam name="TProxyInterface">Proxy interface</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
// ReSharper disable twice UnusedTypeParameter
public class NexusAttribute<TNexusInterface, TProxyInterface> : Attribute
    where TNexusInterface : class
    where TProxyInterface : class
{
    /// <summary>
    /// Gets the type of hub this is implemented as.
    /// </summary>
    public required NexusType NexusType { get; init; }

    /// <summary>
    /// Gets the type of versioning that the server nexus is to be configured with.
    /// </summary>
    public NexusVersioning Versioning { get; init; }

    /// <summary>
    /// Creates a blank attribute to set the <see cref="NexusType"/> at a later point.
    /// </summary>
    public NexusAttribute()
    {
    }

}

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
