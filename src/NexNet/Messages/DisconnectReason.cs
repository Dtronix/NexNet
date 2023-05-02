namespace NexNet.Messages;

public enum DisconnectReason : byte
{
    None = 0,
    DisconnectSocketError = MessageType.DisconnectSocketError,
    DisconnectGraceful = MessageType.DisconnectGraceful,
    TransportError = MessageType.DisconnectTransportError,
    Timeout = MessageType.DisconnectTimeout,
    DisconnectClientHubMismatch = MessageType.DisconnectClientHubMismatch,
    DisconnectServerHubMismatch = MessageType.DisconnectServerHubMismatch,
    DisconnectMessageParsingError = MessageType.DisconnectMessageParsingError,
    DisconnectFromHub = MessageType.DisconnectFromHub,
    DisconnectServerShutdown = MessageType.DisconnectServerShutdown,
    DisconnectAuthentication = MessageType.DisconnectAuthentication,
    DisconnectServerRestarting = MessageType.DisconnectServerRestarting,
}
