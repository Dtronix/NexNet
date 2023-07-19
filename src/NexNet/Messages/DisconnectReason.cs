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
    /// Client nexus does not match server's version.
    /// </summary>
    ClientMismatch = MessageType.DisconnectClientMismatch,

    /// <summary>
    /// Server nexus does not match client's version.
    /// </summary>
    ServerMismatch = MessageType.DisconnectServerMismatch,

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

    /// <summary>
    /// The high water cutoff was reached on a duplex pipe. No body.
    /// </summary>
    NexusPipeHighWaterCutoffReached = MessageType.DisconnectNexusPipeHighWaterCutoffReached,
}
