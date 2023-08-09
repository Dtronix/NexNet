using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet;

/// <summary>
/// Main interface for NexNetClient
/// </summary>
public interface INexusClient : IAsyncDisposable
{
    /// <summary>
    /// Current state of the connection
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Configurations used for this session.  Should not be changed once connection has been established.
    /// </summary>
    ClientConfig Config { get; }

    /// <summary>
    /// Task which completes upon the disconnection of the client.
    /// </summary>
    Task DisconnectedTask { get; }

    /// <summary>
    /// Connects to the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the connection.</param>
    /// <returns>Task for completion</returns>
    /// <exception cref="InvalidOperationException">Throws when the client is already connected to the server.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <returns>Task which completes upon disconnection.</returns>
    Task DisconnectAsync();
}
