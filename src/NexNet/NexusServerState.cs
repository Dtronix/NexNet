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
}
