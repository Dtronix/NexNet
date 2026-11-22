namespace NexNet;

/// <summary>
/// Current state of the server.
/// </summary>
public enum NexusServerState
{
    /// <summary>
    /// Server is stopped
    /// </summary>
    Stopped = 0,
        
    /// <summary>
    /// Server is running.
    /// </summary>
    Running = 1,
        
    /// <summary>
    /// Server has stopped and been disposed.
    /// </summary>
    Disposed = 2,

    /// <summary>
    /// Server is binding its endpoints. Connections are not accepted yet.
    /// </summary>
    Starting = 3,

    /// <summary>
    /// Server is closing its endpoints and disconnecting sessions. New connections are refused.
    /// </summary>
    Stopping = 4,
}
