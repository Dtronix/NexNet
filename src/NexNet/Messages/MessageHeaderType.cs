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

    /// <summary>
    /// Ping Client greeting message.  Sent from client.
    /// </summary>
    GreetingClient = 10,

    /// <summary>
    /// Server greeting message. Sent from server.
    /// </summary>
    GreetingServer = 11,

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
    DisconnectClientHubMismatch = 24,

    /// <summary>
    /// Server's hub does not match client's version. No body.
    /// </summary>
    DisconnectServerHubMismatch = 25,
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


    // Requests
    //Invocation = 100,

    /// <summary>
    /// Header for InvocationRequestMessage.
    /// </summary>
    InvocationWithResponseRequest = 101,
    //InvocationWithResponseAndTimeout = 102,

    /// <summary>
    /// Header for InvocationCancellationRequestMessage.
    /// </summary>
    InvocationCancellationRequest = 103,

    // Responses

    /// <summary>
    /// Header for InvocationProxyResultMessage
    /// </summary>
    InvocationProxyResult = 110,
}
