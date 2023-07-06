using System;

namespace NexNet;

/// <summary>
/// Mode of the hub.
/// </summary>
public enum NexusType
{
    /// <summary>
    /// Hub is operating in client mode.
    /// </summary>
    Client = 0,

    /// <summary>
    /// Hub is operating in server mode.
    /// </summary>
    Server = 1
}

/// <summary>
/// Attribute to allow the generator to create the hub and proxy implementation.
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
    /// Creates a blank attribute to set the <see cref="NexusType"/> at a later point.
    /// </summary>
    public NexusAttribute()
    {
    }

}
