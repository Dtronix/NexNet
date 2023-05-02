namespace NexNet.Messages;

public enum MessageType : byte
{
    Unset = 0,
    Ping = 1,

    GreetingClient = 10,
    GreetingServer = 11,

    // Disconnects 20 - 39
    DisconnectSocketError = 20,
    DisconnectGraceful = 21,
    DisconnectTransportError = 22,
    DisconnectTimeout = 23,
    DisconnectClientHubMismatch = 24,
    DisconnectServerHubMismatch = 25,
    DisconnectMessageParsingError = 26,
    DisconnectFromHub = 27,
    DisconnectServerShutdown = 28,
    DisconnectAuthentication = 29,
    DisconnectServerRestarting = 30,


    // Requests
    Invocation = 100,
    InvocationWithResponseRequest = 101,
    InvocationWithResponseAndTimeout = 102,
    InvocationCancellationRequest = 103,

    // Responses
    InvocationProxyResult = 110,
}
