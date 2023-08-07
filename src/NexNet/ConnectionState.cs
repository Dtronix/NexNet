namespace NexNet;

/// <summary>
/// State for the connection.
/// </summary>
public enum ConnectionState : int
{
    /// <summary>
    /// Unset state.  Unused.
    /// </summary>
    Unset,

    /// <summary>
    /// Connection has been established but but setup
    /// </summary>
    Connecting,

    /// <summary>
    /// Connection has been setup and ready for usage.
    /// </summary>
    Connected,

    /// <summary>
    /// Connection has been lost but the client is attempting reconnection.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// Connection is in the process of closing.
    /// </summary>
    Disconnecting,

    /// <summary>
    /// Connection has been closed.
    /// </summary>
    Disconnected
}
