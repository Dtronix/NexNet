namespace NexNet.Transports;

/// <summary>
/// Defines the possible modes for server connections.
/// </summary>
public enum ServerConnectionMode
{
    /// <summary>
    /// Represents a server connection mode in which the server operates as a listener,
    /// actively accepting incoming connections from clients.
    /// </summary>
    Listener,
    
    /// <summary>
    /// Represents a server connection mode where the server operates as a receiver,
    /// where it is provided connections initiated by other endpoints.
    /// </summary>
    Receiver,
}
