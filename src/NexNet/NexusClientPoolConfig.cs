using System;
using NexNet.Transports;

namespace NexNet;

/// <summary>
/// Configuration options for NexusClientPool.
/// </summary>
public sealed class NexusClientPoolConfig
{
    /// <summary>
    /// Client configuration to use for all connections in the pool.
    /// </summary>
    public ClientConfig ClientConfig { get; }

    /// <summary>
    /// Maximum number of connections to maintain in the pool.
    /// </summary>
    public int MaxConnections { get; set; } = 10;

    /// <summary>
    /// Maximum amount of time a client can remain idle in the pool before being disposed.
    /// Clients that exceed this time will be removed, but a minimum number specified by MinIdleConnections will always be maintained.
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Minimum number of idle connections to maintain in the pool before cleaning them up with MaxIdleTime.
    /// This will not create new clients if the number provided is higher than the current number of idling clients.
    /// It will only allow user created clients to idle.
    /// </summary>
    public int MinIdleConnections { get; set; } = 1;

    /// <summary>
    /// Creates a new configuration with the specified client config.
    /// </summary>
    /// <param name="clientConfig">Client configuration to use for all connections in the pool.</param>
    /// <exception cref="ArgumentNullException">Thrown if clientConfig is null.</exception>
    public NexusClientPoolConfig(ClientConfig clientConfig)
    {
        ClientConfig = clientConfig ?? throw new ArgumentNullException(nameof(clientConfig));
    }
}
