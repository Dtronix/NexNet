namespace NexNet;

/// <summary>
/// Internal State int used for interlocked operations.
/// </summary>
internal static class ConnectionStateInternal
{
    /// <summary>
    /// Unset state.  Unused.
    /// </summary>
    public const int Unset = 0;

    /// <summary>
    /// Connection has been established but but setup
    /// </summary>
    public const int Connecting = 1;

    /// <summary>
    /// Connection has been setup and ready for usage.
    /// </summary>
    public const int Connected = 2;

    /// <summary>
    /// Connection has been lost but the client is attempting reconnection.
    /// </summary>
    public const int Reconnecting = 3;

    /// <summary>
    /// Connection is in the process of closing.
    /// </summary>
    public const int Disconnecting = 4;

    /// <summary>
    /// Connection has been closed.
    /// </summary>
    public const int Disconnected = 5;
}
