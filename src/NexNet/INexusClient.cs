using System;
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
    /// Task which completes upon the completed connection and optional authentication of the client.
    /// Null when the client is has not started connection or after disconnection.
    /// </summary>
    Task? ReadyTask { get; }

    /// <summary>
    /// Task which completes upon the disconnection of the client.
    /// </summary>
    Task DisconnectedTask { get; }

    /// <summary>
    /// Connects to the server.
    /// </summary>
    /// <returns>Task for completion</returns>
    /// <exception cref="InvalidOperationException">Throws when the client is already connected to the server.</exception>
    Task ConnectAsync();

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <returns>Task which completes upon disconnection.</returns>
    Task DisconnectAsync();
}
