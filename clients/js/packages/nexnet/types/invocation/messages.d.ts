/**
 * Bodies of the invocation messages (protocol spec §4.3, §4.4, §4.8) and the remote error detail. Session-level
 * bodies (credit, cancellation, abort) are in `../session/wire.js`.
 * @module
 */
/** Invocation flags (§4.3). */
export declare const InvocationFlags: Readonly<{
    None: 0;
    IgnoreReturn: 1;
    DuplexPipe: 2;
}>;
/** InvocationResult states (§4.4). */
export declare const ResultState: Readonly<{
    Unset: 0;
    CompletedResult: 1;
    Exception: 2;
    Unauthorized: 3;
    Canceled: 4;
}>;
/** Large message kinds (§4.8). */
export declare const LargeMessageKind: Readonly<{
    Argument: 0;
    Result: 1;
}>;
/** MessageAbort reasons (§4.9). */
export declare const AbortReason: Readonly<{
    Busy: 0;
    TooLarge: 1;
    Cancelled: 2;
    Ack: 3;
}>;
/** Longest remote exception type name sent or accepted, in UTF-16 code units (§4.4). */
export declare const MAX_REMOTE_TYPE_LENGTH = 256;
/** Longest remote message sent or accepted, in UTF-16 code units (§4.4). */
export declare const MAX_REMOTE_MESSAGE_LENGTH = 2048;
/**
 * Returns the offset just past the MessagePack value at `pos`. Iterative, so nesting depth costs no stack; every
 * length is bounds-checked. Throws {@link WireFormatError}.
 * @param {Uint8Array} buf
 * @param {number} pos
 * @returns {number}
 */
export declare function skipValue(buf: Uint8Array, pos: number): number;
export type InvocationMessage = {
    invocationId: number;
    methodId: number;
    flags: number;
    /**
     * The arguments array, as MessagePack (`0x90` when there are none).
     */
    args: Uint8Array;
};
/**
 * @typedef {object} InvocationMessage
 * @property {number} invocationId
 * @property {number} methodId
 * @property {number} flags
 * @property {Uint8Array} args The arguments array, as MessagePack (`0x90` when there are none).
 */
/**
 * Invocation body `[invocationId, methodId, flags, arguments]` (§4.3). `args` is the serialized arguments array; null
 * or empty sends an empty array.
 * @param {number} invocationId
 * @param {number} methodId
 * @param {number} flags
 * @param {Uint8Array | null} args
 * @returns {Uint8Array}
 */
export declare function encodeInvocation(invocationId: number, methodId: number, flags: number, args: Uint8Array | null): Uint8Array;
/**
 * Decodes an Invocation body; the arguments are copied.
 * @param {Uint8Array} body
 * @returns {InvocationMessage}
 */
export declare function decodeInvocation(body: Uint8Array): InvocationMessage;
export type InvocationResultMessage = {
    invocationId: number;
    /**
     * A {@link ResultState}.
     */
    state: number;
    /**
     * The embedded result (or error detail), or null when the body has none.
     */
    value: Uint8Array | null;
};
/**
 * @typedef {object} InvocationResultMessage
 * @property {number} invocationId
 * @property {number} state A {@link ResultState}.
 * @property {Uint8Array | null} value The embedded result (or error detail), or null when the body has none.
 */
/**
 * InvocationResult body `[invocationId, state]` or `[invocationId, state, value]` (§4.4).
 * @param {number} invocationId
 * @param {number} state
 * @param {Uint8Array | null} value An embedded MessagePack value, or null for none.
 * @returns {Uint8Array}
 */
export declare function encodeInvocationResult(invocationId: number, state: number, value: Uint8Array | null): Uint8Array;
/**
 * Decodes an InvocationResult body; the value is copied.
 * @param {Uint8Array} body
 * @returns {InvocationResultMessage}
 */
export declare function decodeInvocationResult(body: Uint8Array): InvocationResultMessage;
export type MessageStart = {
    invocationId: number;
    /**
     * A {@link LargeMessageKind}.
     */
    kind: number;
    /**
     * 110 (Invocation) or 112 (InvocationResult).
     */
    innerType: number;
    totalLength: number;
};
/**
 * @typedef {object} MessageStart
 * @property {number} invocationId
 * @property {number} kind A {@link LargeMessageKind}.
 * @property {number} innerType 110 (Invocation) or 112 (InvocationResult).
 * @property {number} totalLength
 */
/**
 * MessageStart body `[invocationId, kind, innerType, totalLength]` (§4.8).
 * @param {MessageStart} start
 * @returns {Uint8Array}
 */
export declare function encodeMessageStart({ invocationId, kind, innerType, totalLength }: MessageStart): Uint8Array;
/**
 * @param {Uint8Array} body
 * @returns {MessageStart}
 */
export declare function decodeMessageStart(body: Uint8Array): MessageStart;
/**
 * Cuts `value` to at most `max` UTF-16 code units without splitting a surrogate pair.
 * @param {string | null} value
 * @param {number} max
 * @returns {string | null}
 */
export declare function truncate(value: string | null, max: number): string | null;
export type RemoteErrorDetail = {
    code: number | null;
    type: string | null;
    message: string | null;
};
/**
 * @typedef {object} RemoteErrorDetail
 * @property {number | null} code
 * @property {string | null} type
 * @property {string | null} message
 */
/**
 * The detail of an Exception result, `[code: int32|nil, type: str|nil, message: str|nil]` (§4.4), truncating the
 * type to 256 and the message to 2048 code units.
 * @param {RemoteErrorDetail} detail
 * @returns {Uint8Array}
 */
export declare function encodeRemoteErrorDetail({ code, type, message }: RemoteErrorDetail): Uint8Array;
/**
 * Reads a remote error detail. Returns null for anything that is not exactly a valid one (§4.4); never throws.
 * @param {Uint8Array | null} value
 * @returns {RemoteErrorDetail | null}
 */
export declare function tryReadRemoteErrorDetail(value: Uint8Array | null): RemoteErrorDetail | null;
