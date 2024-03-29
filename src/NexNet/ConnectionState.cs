﻿namespace NexNet;

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
    /// Connection has been established but but setup
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
