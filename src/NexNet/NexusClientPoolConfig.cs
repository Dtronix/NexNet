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
    /// Clients that exceed this time will be removed, but a minimum of 1 client will always be maintained.
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);

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