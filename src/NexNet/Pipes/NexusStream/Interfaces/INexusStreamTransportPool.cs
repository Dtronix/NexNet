using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Manages a pool of stream transports for reuse.
/// </summary>
internal interface INexusStreamTransportPool : IDisposable
{
    /// <summary>
    /// Gets the number of transports currently in the pool.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the maximum number of transports in the pool.
    /// </summary>
    int MaxSize { get; }

    /// <summary>
    /// Rents a transport from the pool or creates a new one if empty.
    /// </summary>
    /// <returns>A rented transport that returns to the pool on dispose.</returns>
    IRentedNexusStreamTransport Rent();

    /// <summary>
    /// Returns a transport to the pool.
    /// </summary>
    /// <param name="transport">The transport to return.</param>
    void Return(NexusStreamTransport transport);
}
