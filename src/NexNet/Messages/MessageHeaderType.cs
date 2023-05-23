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

    // Pipe Channels
    /// <summary>   
    /// Header for message on Channel 1   
    /// </summary>   
    PipeChannel1 = 151,

    /// <summary>   
    /// Header for message on Channel 2   
    /// </summary>   
    PipeChannel2 = 152,

    /// <summary>   
    /// Header for message on Channel 3   
    /// </summary>   
    PipeChannel3 = 153,

    /// <summary>   
    /// Header for message on Channel 4   
    /// </summary>   
    PipeChannel4 = 154,

    /// <summary>   
    /// Header for message on Channel 5   
    /// </summary>   
    PipeChannel5 = 155,

    /// <summary>   
    /// Header for message on Channel 6   
    /// </summary>   
    PipeChannel6 = 156,

    /// <summary>   
    /// Header for message on Channel 7   
    /// </summary>   
    PipeChannel7 = 157,

    /// <summary>   
    /// Header for message on Channel 8   
    /// </summary>   
    PipeChannel8 = 158,

    /// <summary>   
    /// Header for message on Channel 9   
    /// </summary>   
    PipeChannel9 = 159,

    /// <summary>   
    /// Header for message on Channel 10   
    /// </summary>   
    PipeChannel10 = 160,

    /// <summary>   
    /// Header for message on Channel 11   
    /// </summary>   
    PipeChannel11 = 161,

    /// <summary>   
    /// Header for message on Channel 12   
    /// </summary>   
    PipeChannel12 = 162,

    /// <summary>   
    /// Header for message on Channel 13   
    /// </summary>   
    PipeChannel13 = 163,

    /// <summary>   
    /// Header for message on Channel 14   
    /// </summary>   
    PipeChannel14 = 164,

    /// <summary>   
    /// Header for message on Channel 15   
    /// </summary>   
    PipeChannel15 = 165,

    /// <summary>   
    /// Header for message on Channel 16   
    /// </summary>   
    PipeChannel16 = 166,

    /// <summary>   
    /// Header for message on Channel 17   
    /// </summary>   
    PipeChannel17 = 167,

    /// <summary>   
    /// Header for message on Channel 18   
    /// </summary>   
    PipeChannel18 = 168,

    /// <summary>   
    /// Header for message on Channel 19   
    /// </summary>   
    PipeChannel19 = 169,

    /// <summary>   
    /// Header for message on Channel 20   
    /// </summary>   
    PipeChannel20 = 170,

    /// <summary>   
    /// Header for message on Channel 21   
    /// </summary>   
    PipeChannel21 = 171,

    /// <summary>   
    /// Header for message on Channel 22   
    /// </summary>   
    PipeChannel22 = 172,

    /// <summary>   
    /// Header for message on Channel 23   
    /// </summary>   
    PipeChannel23 = 173,

    /// <summary>   
    /// Header for message on Channel 24   
    /// </summary>   
    PipeChannel24 = 174,

    /// <summary>   
    /// Header for message on Channel 25   
    /// </summary>   
    PipeChannel25 = 175,

    /// <summary>   
    /// Header for message on Channel 26   
    /// </summary>   
    PipeChannel26 = 176,

    /// <summary>   
    /// Header for message on Channel 27   
    /// </summary>   
    PipeChannel27 = 177,

    /// <summary>   
    /// Header for message on Channel 28   
    /// </summary>   
    PipeChannel28 = 178,

    /// <summary>   
    /// Header for message on Channel 29   
    /// </summary>   
    PipeChannel29 = 179,

    /// <summary>   
    /// Header for message on Channel 30   
    /// </summary>   
    PipeChannel30 = 180,

    /// <summary>   
    /// Header for message on Channel 31   
    /// </summary>   
    PipeChannel31 = 181,

    /// <summary>   
    /// Header for message on Channel 32   
    /// </summary>   
    PipeChannel32 = 182,

    /// <summary>   
    /// Header for message on Channel 33   
    /// </summary>   
    PipeChannel33 = 183,

    /// <summary>   
    /// Header for message on Channel 34   
    /// </summary>   
    PipeChannel34 = 184,

    /// <summary>   
    /// Header for message on Channel 35   
    /// </summary>   
    PipeChannel35 = 185,

    /// <summary>   
    /// Header for message on Channel 36   
    /// </summary>   
    PipeChannel36 = 186,

    /// <summary>   
    /// Header for message on Channel 37   
    /// </summary>   
    PipeChannel37 = 187,

    /// <summary>   
    /// Header for message on Channel 38   
    /// </summary>   
    PipeChannel38 = 188,

    /// <summary>   
    /// Header for message on Channel 39   
    /// </summary>   
    PipeChannel39 = 189,

    /// <summary>   
    /// Header for message on Channel 40   
    /// </summary>   
    PipeChannel40 = 190,

    /// <summary>   
    /// Header for message on Channel 41   
    /// </summary>   
    PipeChannel41 = 191,

    /// <summary>   
    /// Header for message on Channel 42   
    /// </summary>   
    PipeChannel42 = 192,

    /// <summary>   
    /// Header for message on Channel 43   
    /// </summary>   
    PipeChannel43 = 193,

    /// <summary>   
    /// Header for message on Channel 44   
    /// </summary>   
    PipeChannel44 = 194,

    /// <summary>   
    /// Header for message on Channel 45   
    /// </summary>   
    PipeChannel45 = 195,

    /// <summary>   
    /// Header for message on Channel 46   
    /// </summary>   
    PipeChannel46 = 196,

    /// <summary>   
    /// Header for message on Channel 47   
    /// </summary>   
    PipeChannel47 = 197,

    /// <summary>   
    /// Header for message on Channel 48   
    /// </summary>   
    PipeChannel48 = 198,

    /// <summary>   
    /// Header for message on Channel 49   
    /// </summary>   
    PipeChannel49 = 199,

    /// <summary>   
    /// Header for message on Channel 50   
    /// </summary>   
    PipeChannel50 = 200,

    /// <summary>
    /// Header for closing a write specific channel.
    /// </summary>
    PipeCloseWrite = 201,

    /// <summary>
    /// Header for closing a specific read channel.
    /// </summary>
    PipeCloseRead = 202,
}
