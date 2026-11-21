/**
 * Wire constants of the NexNet protocol (docs/internals/protocol-specification.md §1–§3, §5, §9).
 * @module
 */
/** Message type bytes (protocol spec §3). Bytes 20–38 are disconnect reasons, see {@link isDisconnectCode}. */
export declare const MessageType: Readonly<{
    Ping: 1;
    DuplexPipeWrite: 50;
    PipeWindowUpdate: 51;
    ClientGreeting: 100;
    ClientGreetingReconnection: 101;
    ServerGreeting: 105;
    Invocation: 110;
    InvocationCancellation: 111;
    InvocationResult: 112;
    MessageStart: 113;
    MessageChunk: 114;
    MessageAbort: 115;
    InvocationCredit: 116;
    DuplexPipeUpdateState: 120;
}>;
export type MessageTypeValue = typeof MessageType[keyof typeof MessageType];
/** @typedef {typeof MessageType[keyof typeof MessageType]} MessageTypeValue */
/** The 8-byte connection preamble: magic `NnP\x14`, three reserved zero bytes, protocol version 1 (§1). */
export declare const PREAMBLE: readonly number[];
/** Protocol version carried in the last preamble byte. */
export declare const PROTOCOL_VERSION = 1;
/** Largest frame body (§2). */
export declare const MAX_FRAME_BODY = 65535;
/** Largest frame body accepted before the connection completes (§2, §5.1). */
export declare const MAX_PRE_AUTH_BODY: number;
/** Smallest `maxMessageSize` a peer may advertise: one frame (65,526 bytes, §4.2). */
export declare const MIN_PEER_MAX_MESSAGE_SIZE: number;
/** Largest greeting authentication token (§5.4). */
export declare const MAX_AUTHENTICATION_TOKEN = 8192;
/** Largest greeting version string, in UTF-16 code units (§5.2). */
export declare const MAX_VERSION_LENGTH = 16;
/** Inclusive range of disconnect codes (§3, §5.6). */
export declare const DISCONNECT_MIN = 20;
export declare const DISCONNECT_MAX = 38;
/**
 * True when `byte` is a disconnect code (20–38). Assigned or not, every code in the range ends the session (§5.6).
 * @param {number} byte
 * @returns {boolean}
 */
export declare function isDisconnectCode(byte: number): boolean;
/**
 * Frame layout of a message type: whether it has a 2-byte body length, and the post-header length (§2).
 * Null for a type that is not defined (a protocol error) and for disconnect codes.
 *
 * @param {number} type
 * @returns {{ hasBody: boolean, postHeader: number } | null}
 */
export declare function frameLayout(type: number): {
    hasBody: boolean;
    postHeader: number;
} | null;
