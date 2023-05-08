namespace NexNet.Messages;

/// <summary>
/// Contains the reasons the connection would disconnect.
/// </summary>
public enum DisconnectReason : byte
{
    /// <summary>
    /// No reason provided.  Means the connection is not disconnected.
    /// </summary>
    None = 0,

    /// <summary>
    /// Socket disconnected 
    /// </summary>
    SocketError = MessageType.DisconnectSocketError,

    /// <summary>
    /// Graceful disconnection.
    /// </summary>
    Graceful = MessageType.DisconnectGraceful,

    /// <summary>
    /// There was an error with the transport protocol.
    /// </summary>
    ProtocolError = MessageType.DisconnectProtocolError,

    /// <summary>
    /// Connection timed out.
    /// </summary>
    Timeout = MessageType.DisconnectTimeout,

    /// <summary>
    /// Client hub does not match server's version.
    /// </summary>
    ClientHubMismatch = MessageType.DisconnectClientHubMismatch,

    /// <summary>
    /// Server's hub does not match client's version.
    /// </summary>
    ServerHubMismatch = MessageType.DisconnectServerHubMismatch,

    /// <summary>
    /// Server is shutting down.
    /// </summary>
    ServerShutdown = MessageType.DisconnectServerShutdown,

    /// <summary>
    /// Authentication required and or failed.
    /// </summary>
    Authentication = MessageType.DisconnectAuthentication,

    /// <summary>
    /// Server is restarting.
    /// </summary>
    ServerRestarting = MessageType.DisconnectServerRestarting,
}
