using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Invocation;

/// <summary>
/// Base context for hubs to use.
/// </summary>
public interface ISessionContext
{
    /// <summary>
    /// Logger.
    /// </summary>
    INexusLogger? Logger { get; }

    /// <summary>
    /// Store for this session used to keep and pass variables for the lifetime of this session.
    /// </summary>
    SessionStore Store { get; }

    /// <summary>
    /// Id of the current session.
    /// </summary>
    long Id { get; }

    /// <summary>
    /// Creates a pipe for use with the current session.
    /// </summary>
    /// <returns>Pipe for communication over teh current session.</returns>
    IRentedNexusDuplexPipe CreatePipe();

    /// <summary>
    /// Creates an unmanaged duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of unmanaged data to be transmitted through the channel.</typeparam>
    /// <returns>An instance of <see cref="INexusDuplexUnmanagedChannel{T}"/> that allows for bidirectional communication of unmanaged data.</returns>
    /// <remarks>
    /// This method is optimized for unmanaged types and should be used over the non-unmanaged version when possible.
    /// </remarks>
    INexusDuplexUnmanagedChannel<T> CreateUnmanagedChannel<T>()
        where T : unmanaged;

    /// <summary>
    /// Creates a duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of data to be transmitted through the channel.</typeparam>
    /// <returns>An instance of <see cref="INexusDuplexChannel{T}"/> that allows for bidirectional communication of data.</returns>
    /// <remarks>
    /// This method creates a channel from a rented pipe and is suitable for any MessagePack data type.
    /// </remarks>
    INexusDuplexChannel<T> CreateChannel<T>();

    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    /// <param name="reason">Reason for the disconnection.</param>
    internal Task DisconnectAsync(DisconnectReason reason);

    /// <summary>
    /// Resets the context for closure.
    /// </summary>
    void Reset();
}
