using System;

namespace NexNet;

/// <summary>
/// Mode of the hub.
/// </summary>
public enum NexNetHubType
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
/// <typeparam name="THub">Hub interface.</typeparam>
/// <typeparam name="TProxy">Proxy interface</typeparam>
[AttributeUsage(AttributeTargets.Class)]
// ReSharper disable twice UnusedTypeParameter
public class NexNetHubAttribute<THub, TProxy> : Attribute
    where THub : class
    where TProxy : class
{
    /// <summary>
    /// Gets the type of hub this is implemented as.
    /// </summary>
    public NexNetHubType HubType { get; init; }

    /// <summary>
    /// Creates the attribute with the passed type.
    /// </summary>
    /// <param name="hubType">Type of hub to implement.</param>
    public NexNetHubAttribute(NexNetHubType hubType)
    {
        HubType = hubType;
    }

    /// <summary>
    /// Creates a blank attribute to set the <see cref="HubType"/> at a later point.
    /// </summary>
    public NexNetHubAttribute()
    {
    }

}
