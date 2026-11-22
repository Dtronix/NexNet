namespace NexNet;

/// <summary>
/// State for the connection.
/// </summary>
public enum ConnectionState : int
{
    /// <summary>
    /// Unset state.  Unused.
    /// </summary>
    Unset = 0,

    /// <summary>
    /// The connection is being set up: the transport is opening or the handshake has not completed yet. A client is
    /// Connecting from the start of an application connect (ConnectAsync or TryConnectAsync) until it is Connected,
    /// or Disconnected when the connect fails; attempts of the reconnection loop do not raise it, the client stays
    /// Reconnecting. A server session is Connecting until its handshake completes.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// Connection has been setup and ready for usage.
    /// </summary>
    Connected = 2,

    /// <summary>
    /// Connection has been lost but the client is attempting reconnection.
    /// </summary>
    Reconnecting = 3,

    /// <summary>
    /// Connection is in the process of closing.
    /// </summary>
    Disconnecting = 4,

    /// <summary>
    /// Connection has been closed.
    /// </summary>
    Disconnected = 5
}
