namespace NexNet.Transports.HttpSocket.Internals;

internal static class Strings
{
    public const string net_HttpSockets_Generic =
        "An internal HttpSocket error occurred. Please see the innerException, if present, for more details. ";

    public const string net_HttpSockets_InvalidMessageType_Generic =
        "The received  message type is invalid after calling {0}. {0} should only be used if no more data is expected from the remote endpoint. Use '{1}' instead to keep being able to receive data but close the output channel.";

    public const string net_HttpSockets_HttpSocketBaseFaulted =
        "An exception caused the HttpSocket to enter the Aborted state. Please see the InnerException, if present, for more details.";

    public const string net_HttpSockets_NotAHttpSocket_Generic =
        "A HttpSocket operation was called on a request or response that is not a HttpSocket.";

    public const string net_HttpSockets_UnsupportedHttpSocketVersion_Generic = "Unsupported HttpSocket version.";

    public const string net_HttpSockets_UnsupportedProtocol_Generic =
        "The HttpSocket request or response operation was called with unsupported protocol(s). ";

    public const string net_HttpSockets_HeaderError_Generic =
        "The HttpSocket request or response contained unsupported header(s). ";

    public const string net_HttpSockets_ConnectionClosedPrematurely_Generic =
        "The remote party closed the HttpSocket connection without completing the close handshake.";

    public const string net_HttpSockets_InvalidState_Generic =
        "The HttpSocket instance cannot be used for communication because it has been transitioned into an invalid state.";

    public const string net_HttpSockets_ArgumentOutOfRange_TooSmall = "The argument must be a value greater than {0}.";

    public const string net_HttpSockets_InvalidCharInProtocolString =
        "The HttpSocket protocol '{0}' is invalid because it contains the invalid character '{1}'.";

    public const string net_HttpSockets_InvalidCloseStatusCode =
        "The close status code '{0}' is reserved for system use only and cannot be specified when calling this method.";

    public const string net_HttpSockets_UnsupportedPlatform =
        "The HttpSocket protocol is not supported on this platform.";
    
    public const string net_HttpSockets_ReservedBitsSet =
        "The HttpSocket received a frame with one or more reserved bits set.";

    public const string net_HttpSockets_ClientReceivedMaskedFrame = "The HttpSocket server sent a masked frame.";

    public const string net_HttpSockets_ContinuationFromFinalFrame =
        "The HttpSocket received a continuation frame from a previous final message.";

    public const string net_HttpSockets_NonContinuationAfterNonFinalFrame =
        "The HttpSocket expected a continuation frame after having received a previous non-final frame.";

    public const string net_HttpSockets_InvalidControlMessage = "The HttpSocket received an invalid control message.";

    public const string net_HttpSockets_UnknownOpcode =
        "The HttpSocket received a frame with an unknown opcode: '0x{0}'.";

    public const string NotReadableStream = "The base stream is not readable.";
    public const string NotWriteableStream = "The base stream is not writeable.";
    public const string net_HttpSockets_ArgumentOutOfRange = "The argument must be a value between {0} and {1}.";

    public const string net_HttpSockets_KeepAlivePingTimeout =
        "The HttpSocket didn't recieve a Pong frame in response to a Ping frame within the configured KeepAliveTimeout.";

    public const string net_HttpSockets_PerMessageCompressedFlagInContinuation =
        "The HttpSocket received a continuation frame with Per-Message Compressed flag set.";

    public const string net_HttpSockets_PerMessageCompressedFlagWhenNotEnabled =
        "The HttpSocket received compressed frame when compression is not enabled.";

    public const string ZLibErrorDLLLoadError = "The underlying compression routine could not be loaded correctly.";

    public const string ZLibErrorInconsistentStream =
        "The stream state of the underlying compression routine is inconsistent.";

    public const string ZLibErrorNotEnoughMemory =
        "The underlying compression routine could not reserve sufficient memory.";

    public const string ZLibErrorUnexpected =
        "The underlying compression routine returned an unexpected error code {0}.";

    public const string ZLibUnsupportedCompression =
        "The message was compressed using an unsupported compression method.";

    public const string net_HttpSockets_Argument_MessageFlagsHasDifferentCompressionOptions =
        "The compression options for a continuation cannot be different than the options used to send the first fragment of the message.";

    public const string net_HttpSockets_InvalidPayloadLength =
        "The HttpSocket received a frame with an invalid payload length.";

    public const string PlatformNotSupported_HttpSockets = "HttpSocketsTests is not supported on this platform.";
    public const string net_HttpSockets_Scheme = "Only Uris starting with 'ws://' or 'wss://' are supported.";
    public const string net_httpstatus_ConnectFailure = "Unable to connect to the remote server";
    public const string net_uri_NotAbsolute = "This operation is not supported for a relative URI.";

    public const string net_HttpSockets_AlreadyOneOutstandingOperation =
        "There is already one outstanding '{0}' call for this HttpSocket instance. ReceiveAsync and SendAsync can be called simultaneously, but at most one outstanding operation for each of them is allowed at the same time.";

    public const string net_HttpSockets_AcceptUnsupportedProtocol =
        "The HttpSocket client request requested '{0}' protocol(s), but server is only accepting '{1}' protocol(s).";

    public const string net_HttpSockets_ConnectStatusExpected =
        "The server returned status code '{0}' when status code '{1}' was expected.";

    public const string net_HttpSockets_MissingResponseHeader =
        "The server's response was missing the required header '{0}'.";

    public const string net_HttpSockets_InvalidState_ClosedOrAborted =
        "The '{0}' instance cannot be used for communication because it has been transitioned into the '{1}' state.";

    public const string net_HttpSockets_InvalidState =
        "The HttpSocket is in an invalid state ('{0}') for this operation. Valid states are: '{1}'";

    public const string net_HttpSockets_Argument_InvalidMessageType =
        "The message type '{0}' is not allowed for the '{1}' operation. Valid message types are: '{2}, {3}'. To close the HttpSocket, use the '{4}' operation instead. ";

    public const string net_HttpSockets_ReasonNotNull =
        "The close status description '{0}' is invalid. When using close status code '{1}' the description must be null.";

    public const string net_HttpSockets_InvalidCloseStatusDescription =
        "The close status description '{0}' is too long. The UTF-8 representation of the status description must not be longer than {1} bytes.";

    public const string net_HttpSockets_AlreadyStarted = "The HttpSocket has already been started.";
    public const string net_HttpSockets_InvalidResponseHeader = "The '{0}' header value '{1}' is invalid.";
    public const string net_HttpSockets_NotConnected = "The HttpSocket is not connected.";
    public const string net_HttpSockets_NoDuplicateProtocol = "Duplicate protocols are not allowed: '{0}'.";
    public const string net_securityprotocolnotsupported = "The requested security protocol is not supported.";

    public const string net_HttpSockets_ServerWindowBitsNegotiationFailure =
        "The HttpSocket failed to negotiate max server window bits. The client requested {0} but the server responded with {1}.";

    public const string net_HttpSockets_ClientWindowBitsNegotiationFailure =
        "The HttpSocket failed to negotiate max client window bits. The client requested {0} but the server responded with {1}.";

    public const string net_HttpSockets_OptionsIncompatibleWithCustomInvoker =
        "UseDefaultCredentials, Credentials, Proxy, ClientCertificates, RemoteCertificateValidationCallback and Cookies must not be set on ClientHttpSocketOptions when an HttpMessageInvoker instance is also specified. These options should be set on the HttpMessageInvoker's underlying HttpMessageHandler instead.";

    public const string net_HttpSockets_CustomInvokerRequiredForHttp2 =
        "An HttpMessageInvoker instance must be passed to ConnectAsync when using HTTP/2.";
}