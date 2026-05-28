using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Represents a rented stream transport that returns to its pool when disposed.
/// </summary>
/// <remarks>
/// Use this interface when working with stream transports obtained from a session or client.
/// Disposing the transport returns it to the pool for reuse rather than destroying it.
/// </remarks>
public interface IRentedNexusStreamTransport : INexusStreamTransport
{
    /// <summary>
    /// Gets whether this transport has been returned to the pool.
    /// </summary>
    bool IsReturned { get; }
}
