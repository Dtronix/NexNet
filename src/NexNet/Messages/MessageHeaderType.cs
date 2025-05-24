namespace NexNet.Messages;

/// <summary>
/// Contains all the ID message types
/// </summary>
public enum MessageType : byte
{
    /// <summary>
    /// Unset message.  Not used.
    /// </summary>
    Unset = 0,

    /// <summary>
    /// Ping header. No Body.
    /// </summary>
    Ping = 1,
    
    // Disconnects 20 - 39

    /// <summary>
    /// Socket disconnected. No body.
    /// </summary>
    DisconnectSocketError = 20,

    /// <summary>
    /// Graceful disconnection. No body.
    /// </summary>
    DisconnectGraceful = 21,

    /// <summary>
    /// There was an error with the transport protocol. No body.
    /// </summary>
    DisconnectProtocolError = 22,

    /// <summary>
    /// Connection timed out. No body.
    /// </summary>
    DisconnectTimeout = 23,

    /// <summary>
    /// Client hub does not match server's version. No body.
    /// </summary>
    DisconnectClientMismatch = 24,

    /// <summary>
    /// Server's hub does not match client's version. No body.
    /// </summary>
    DisconnectServerMismatch = 25,
    //DisconnectMessageParsingError = 26,
    //DisconnectFromHub = 27,

    /// <summary>
    /// Server is shutting down. No body.
    /// </summary>
    DisconnectServerShutdown = 28,

    /// <summary>
    /// Authentication required and or failed. No body.
    /// </summary>
    DisconnectAuthentication = 29,

    /// <summary>
    /// Server is restarting. No body.
    /// </summary>
    DisconnectServerRestarting = 30,

    // <summary>
    // The high water cutoff was reached on a duplex pipe. No body.
    // </summary>
    //DisconnectNexusPipeHighWaterCutoffReached = 31,

    /// <summary>
    /// The socket was closed while attempting to write. No body.
    /// </summary>
    DisconnectSocketClosedWhenWriting = 32,

    /// <summary>
    /// Header for data sent to a pipe.
    /// </summary>
    DuplexPipeWrite = 50,

    // Messages 100 - 109 are reserved for handshake messages.

    /// <summary>
    /// Header for <see cref="ClientGreetingMessage"/>.
    /// </summary>
    ClientGreeting = 100,
    
    /// <summary>
    /// Header for <see cref="ClientGreetingMessage"/>.
    /// </summary>
    ClientGreetingReconnection = 101,
    
    /// <summary>
    /// Header for <see cref="ServerGreetingMessage"/>.
    /// </summary>
    ServerGreeting = 105,

    // Messages 110 - 119 are reserved for invocation messages.

    /// <summary>
    /// Header for <see cref="InvocationMessage"/>.
    /// </summary>
    Invocation = 110,

    /// <summary>
    /// Header for <see cref="InvocationCancellationMessage"/>.
    /// </summary>
    InvocationCancellation = 111,

    /// <summary>
    /// Header for <see cref="InvocationResultMessage"/>
    /// </summary>
    InvocationResult = 112,

    // Messages 120 - 129 are reserved for duplex pipe messages.
    
    /// <summary>
    /// Header for <see cref="DuplexPipeUpdateStateMessage"/>.
    /// </summary>
    DuplexPipeUpdateState = 120,
    
    /// <summary>
    /// Header for <see cref="CollectionUpdateMessage"/>.
    /// </summary>
    CollectionUpdate = 130,
}
