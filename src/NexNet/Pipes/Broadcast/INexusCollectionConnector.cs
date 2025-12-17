using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Interface for broadcast connectors that manage server-side collection connections.
/// </summary>
internal interface INexusBroadcastConnector
{
    /// <summary>
    /// Starts a collection connection on the server side for a connected client.
    /// </summary>
    /// <param name="pipe">The duplex pipe for bidirectional communication.</param>
    /// <param name="context">The session context for the connection.</param>
    /// <returns>A task that completes when the collection connection ends.</returns>
    public ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession context);

    /// <summary>
    /// Starts the broadcast connector and begins accepting connections.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the broadcast connector and closes all connections.
    /// </summary>
    void Stop();
}
