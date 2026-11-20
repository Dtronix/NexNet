/**
 * The MessagePack subset the session needs for protocol message bodies (§4), and the encoders and decoders of the
 * session-level messages. Payload values (arguments, results, channel items) use the J02 codec, not this.
 *
 * Integers are written canonically, as C# MessagePack does: non-negative values in the smallest unsigned format,
 * negative values in the smallest signed format. Readers accept any integer format whose value is in range.
 * @module
 */
/** Thrown when a body does not decode; the session maps it to `ProtocolError`. */
export declare class WireFormatError extends Error {
    /** @param {string} message */
    constructor(message: string);
}
/** Appends MessagePack to a growable buffer. */
export declare class BodyWriter {
    #private;
    /** @param {number} [capacity] */
    constructor(capacity?: number);
    /** @param {number} count @returns {this} */
    writeArrayHeader(count: number): this;
    /** @returns {this} */
    writeNil(): this;
    /**
     * Writes an integer: a safe-integer `number` or a 64-bit `bigint`.
     * @param {number | bigint} value
     * @returns {this}
     */
    writeInt(value: number | bigint): this;
    /** Writes a UTF-8 string, or nil for null. @param {string | null} value @returns {this} */
    writeString(value: string | null): this;
    /** Writes bin, or nil for null. @param {Uint8Array | null} value @returns {this} */
    writeBin(value: Uint8Array | null): this;
    /** @returns {number} */
    get length(): number;
    /** A copy of the bytes written. @returns {Uint8Array} */
    toUint8Array(): Uint8Array;
}
/** Reads MessagePack from one body. Every read checks bounds and throws {@link WireFormatError}. */
export declare class BodyReader {
    #private;
    /** @param {Uint8Array} body */
    constructor(body: Uint8Array);
    /** @returns {number} */
    get remaining(): number;
    /** @returns {number} The next format byte, not consumed. */
    peek(): number;
    /** @returns {boolean} True (and consumes it) when the next value is nil. */
    tryReadNil(): boolean;
    /** @returns {number} */
    readArrayHeader(): number;
    /** Reads any MessagePack integer as a bigint. @returns {bigint} */
    readBigInt(): bigint;
    /**
     * Reads an integer and checks it is in `[min, max]`.
     * @param {number} min @param {number} max
     * @returns {number}
     */
    readIntInRange(min: number, max: number): number;
    /** @returns {number} */
    readInt32(): number;
    /** @returns {number} */
    readUInt8(): number;
    /** @returns {number} */
    readUInt16(): number;
    /** @returns {number} */
    readUInt32(): number;
    /** @returns {bigint} */
    readInt64(): bigint;
    /** Reads a strict UTF-8 string, or null for nil. @returns {string | null} */
    readString(): string | null;
    /**
     * Reads the length of a bin, or -1 for nil, leaving the reader at the bytes.
     * @returns {number}
     */
    readBinHeader(): number;
    /**
     * Reads bin as a copy; nil reads as null.
     * @param {number} [maxLength] Checked against the header before the bytes are read.
     * @returns {Uint8Array | null}
     */
    readBin(maxLength?: number): Uint8Array | null;
    /** Skips one value of any type (depth-bounded). @param {number} [depth] @returns {void} */
    skip(depth?: number): void;
    /** Throws unless the whole body was read (§4: readers reject trailing bytes). @returns {void} */
    end(): void;
}
export type ClientGreeting = {
    version: string | null;
    serverNexusHash: number;
    clientNexusHash: number;
    /**
     * Null is written as an empty token.
     */
    authenticationToken: Uint8Array | null;
    maxMessageSize: number;
    invocationCredits: number;
};
/**
 * ClientGreeting / ClientGreetingReconnection body (§4.1).
 *
 * @typedef {object} ClientGreeting
 * @property {string | null} version
 * @property {number} serverNexusHash
 * @property {number} clientNexusHash
 * @property {Uint8Array | null} authenticationToken Null is written as an empty token.
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 */
/**
 * Encodes a client greeting body. A missing token is sent as an empty bin (`c4 00`), as C# does (§5.4).
 * @param {ClientGreeting} greeting
 * @returns {Uint8Array}
 */
export declare function encodeClientGreeting(greeting: ClientGreeting): Uint8Array;
/**
 * Decodes a client greeting body (used by tests and test servers). Nil token reads as an empty token.
 * @param {Uint8Array} body
 * @returns {ClientGreeting}
 */
export declare function decodeClientGreeting(body: Uint8Array): ClientGreeting;
export type ServerGreeting = {
    /**
     * The client nexus hash the server expects.
     */
    version: number;
    /**
     * The session ID.
     */
    clientId: bigint;
    maxMessageSize: number;
    invocationCredits: number;
    idleTimeoutMs: number | null;
};
/**
 * ServerGreeting body (§4.2). `idleTimeoutMs` is the S04 trailing field: null when the server does not send it.
 *
 * @typedef {object} ServerGreeting
 * @property {number} version The client nexus hash the server expects.
 * @property {bigint} clientId The session ID.
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 * @property {number | null} idleTimeoutMs
 */
/**
 * Encodes a server greeting body (used by tests and test servers). Writes the trailing `idleTimeoutMs` only when set.
 * @param {ServerGreeting | Omit<ServerGreeting, 'idleTimeoutMs'> & { idleTimeoutMs?: number | null }} greeting
 * @returns {Uint8Array}
 */
export declare function encodeServerGreeting(greeting: ServerGreeting | (Omit<ServerGreeting, 'idleTimeoutMs'> & {
    idleTimeoutMs?: number | null;
})): Uint8Array;
/**
 * Decodes a server greeting body. Four elements are required; a fifth is `idleTimeoutMs` (uint32 or nil); any further
 * trailing elements are skipped (trailing-field rule, protocol specification).
 * @param {Uint8Array} body
 * @returns {ServerGreeting}
 */
export declare function decodeServerGreeting(body: Uint8Array): ServerGreeting;
/** @param {number} credits @returns {Uint8Array} InvocationCredit body `[credits: uint16]` (§4.7). */
export declare function encodeInvocationCredit(credits: number): Uint8Array;
/** @param {Uint8Array} body @returns {{ credits: number }} */
export declare function decodeInvocationCredit(body: Uint8Array): {
    credits: number;
};
/** @param {number} invocationId @returns {Uint8Array} InvocationCancellation body `[invocationId: uint16]` (§4.5). */
export declare function encodeInvocationCancellation(invocationId: number): Uint8Array;
/** @param {Uint8Array} body @returns {{ invocationId: number }} */
export declare function decodeInvocationCancellation(body: Uint8Array): {
    invocationId: number;
};
/**
 * MessageAbort body `[invocationId: uint16, kind: uint8, reason: uint8]` (§4.9).
 * @param {number} invocationId @param {number} kind @param {number} reason
 * @returns {Uint8Array}
 */
export declare function encodeMessageAbort(invocationId: number, kind: number, reason: number): Uint8Array;
/** @param {Uint8Array} body @returns {{ invocationId: number, kind: number, reason: number }} */
export declare function decodeMessageAbort(body: Uint8Array): {
    invocationId: number;
    kind: number;
    reason: number;
};
/**
 * DuplexPipeUpdateState body `[pipeId: uint16, state: uint8, window: uint32]` (§4.6).
 * @param {number} pipeId @param {number} state @param {number} window
 * @returns {Uint8Array}
 */
export declare function encodeDuplexPipeUpdateState(pipeId: number, state: number, window: number): Uint8Array;
/** @param {Uint8Array} body @returns {{ pipeId: number, state: number, window: number }} */
export declare function decodeDuplexPipeUpdateState(body: Uint8Array): {
    pipeId: number;
    state: number;
    window: number;
};
/** Message types whose bodies this module encodes, for callers that switch on them. */
export declare const SessionBodyTypes: readonly (100 | 101 | 105 | 111 | 115 | 116 | 120)[];
