/**
 * Enumerations shared by the runtime and generated code. Values match the C# enums exactly.
 * @module
 */
/**
 * Connection state of a client. Mirrors C# `NexNet.ConnectionState`.
 */
export declare const ConnectionState: Readonly<{
    Unset: 0;
    Connecting: 1;
    Connected: 2;
    Reconnecting: 3;
    Disconnecting: 4;
    Disconnected: 5;
}>;
export type ConnectionStateValue = typeof ConnectionState[keyof typeof ConnectionState];
/** @typedef {typeof ConnectionState[keyof typeof ConnectionState]} ConnectionStateValue */
/**
 * Why a session ended. Mirrors C# `NexNet.Messages.DisconnectReason`; the values are the wire disconnect codes (20–38).
 * Codes in 20–38 that are not listed here are still disconnects (protocol spec).
 */
export declare const DisconnectReason: Readonly<{
    None: 0;
    SocketError: 20;
    Graceful: 21;
    ProtocolError: 22;
    Timeout: 23;
    ClientMismatch: 24;
    ServerMismatch: 25;
    ServerShutdown: 28;
    Authentication: 29;
    ServerRestarting: 30;
    SocketClosedWhenWriting: 32;
    RateLimited: 33;
    Unauthorized: 34;
    FlowControl: 35;
    Stalled: 37;
}>;
export type DisconnectReasonValue = number;
/** @typedef {number} DisconnectReasonValue A {@link DisconnectReason} value, or another code in 20–38. */
/**
 * Outcome of {@link NexusClient#tryConnect}. Mirrors C# `ConnectionResult.StateValue`.
 */
export declare const ConnectionResultState: Readonly<{
    Unset: 0;
    Success: 1;
    Disconnected: 2;
    Exception: 3;
    Canceled: 4;
}>;
export type ConnectionResultStateValue = typeof ConnectionResultState[keyof typeof ConnectionResultState];
/** @typedef {typeof ConnectionResultState[keyof typeof ConnectionResultState]} ConnectionResultStateValue */
/**
 * How much exception detail a peer sends with a failed invocation. Mirrors C# `RemoteExceptionDetail`.
 */
export declare const RemoteExceptionDetail: Readonly<{
    None: 0;
    TypeAndMessage: 1;
}>;
export type RemoteExceptionDetailValue = typeof RemoteExceptionDetail[keyof typeof RemoteExceptionDetail];
/** @typedef {typeof RemoteExceptionDetail[keyof typeof RemoteExceptionDetail]} RemoteExceptionDetailValue */
/**
 * State of a synchronized collection. Mirrors C# `NexusCollectionState`.
 */
export declare const NexusCollectionState: Readonly<{
    Unset: 0;
    Disconnected: 1;
    Connected: 2;
}>;
export type NexusCollectionStateValue = typeof NexusCollectionState[keyof typeof NexusCollectionState];
/** @typedef {typeof NexusCollectionState[keyof typeof NexusCollectionState]} NexusCollectionStateValue */
