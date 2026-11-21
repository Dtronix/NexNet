/**
 * Plumbing shared by the built-in transports: connect-error construction, and the {@link TransportBase} class
 * that buffers data and the close until the session registers its callbacks. Platform-neutral: reachable from the
 * browser entry, so it must not import `node:*`.
 * @module
 */
import { NexusTransportError } from '../contract/errors.js';
export type TransportErrorKind = 'InternalError' | 'ConnectionAborted' | 'AddressInUse' | 'InvalidAddress' | 'ConnectionTimeout' | 'Unreachable' | 'ConnectionRefused' | 'ProtocolError' | 'ConnectionReset' | 'AuthenticationError';
export type TransportConnectError = NexusTransportError & {
    transportError: TransportErrorKind;
    status?: number;
};
/**
 * C# `TransportError` names a connect failure maps to (protocol spec §13.1). Set as `transportError` on the
 * `NexusTransportError` a transport factory rejects with.
 *
 * @typedef {'InternalError' | 'ConnectionAborted' | 'AddressInUse' | 'InvalidAddress' | 'ConnectionTimeout'
 *   | 'Unreachable' | 'ConnectionRefused' | 'ProtocolError' | 'ConnectionReset' | 'AuthenticationError'} TransportErrorKind
 */
/**
 * A `NexusTransportError` with the extra fields the built-in transports set: `transportError` (C# `TransportError`
 * name) and, for a refused HTTP upgrade, the response `status`.
 *
 * @typedef {NexusTransportError & { transportError: TransportErrorKind, status?: number }} TransportConnectError
 */
/**
 * Creates the error a transport factory rejects with. `AuthenticationError` maps to
 * `DisconnectReason.Authentication` and everything else to `SocketError`, as in the C# client.
 *
 * @param {string} message
 * @param {{ kind?: TransportErrorKind, cause?: unknown, status?: number }} [options]
 * @returns {TransportConnectError}
 */
export declare function transportError(message: string, { kind, cause, status }?: {
    kind?: TransportErrorKind;
    cause?: unknown;
    status?: number;
}): TransportConnectError;
/**
 * Maps the status of a refused HTTP upgrade (HttpSocket and WebSocket, protocol spec §13.6/§13.7).
 * 401 and 403 are `AuthenticationError`. For WebSocket, 5xx is `InternalError` and anything else `ProtocolError`; for
 * HttpSocket, anything else, 2xx included, is `InternalError`. A 3xx never reaches here: the upgrade maps it to
 * `ProtocolError` first, for both transports, and never follows it (`node/upgrade.js`).
 *
 * @param {string} transport `'HttpSocket'` or `'WebSocket'`.
 * @param {number} status
 * @param {string} url
 * @returns {TransportConnectError}
 */
export declare function upgradeRefused(transport: string, status: number, url: string): TransportConnectError;
/**
 * Base class of the built-in transports. A subclass reports received bytes with {@link TransportBase#_deliver} and
 * the end of the stream with {@link TransportBase#_ended}. Bytes received before the session registers `onData`
 * (the bytes that came with the HTTP 101, for example) are kept and delivered first, in order; a close that happens
 * before the callbacks are registered is reported after those bytes.
 */
export declare class TransportBase {
    #private;
    /** @type {boolean} */
    isSecure: boolean;
    /** @type {unknown} */
    remoteCertificate: unknown;
    /** @type {string | null} */
    remoteAddress: string | null;
    /** @type {number | null} */
    remotePort: number | null;
    /** @type {string} */
    name: string;
    /**
     * @param {string} name
     * @param {boolean} isSecure
     */
    constructor(name: string, isSecure: boolean);
    /** True once the stream has ended (either side). @returns {boolean} */
    get ended(): boolean;
    /** @param {(data: Uint8Array) => void} callback */
    onData(callback: (data: Uint8Array) => void): void;
    /** @param {(error?: unknown) => void} callback */
    onClose(callback: (error?: unknown) => void): void;
    /**
     * Hands received bytes to the session (or keeps them until it registers).
     * @protected
     * @param {Uint8Array} data
     */
    protected _deliver(data: Uint8Array): void;
    /**
     * Marks the stream ended and reports it once. Later calls are ignored.
     * @protected
     * @param {unknown} [error]
     */
    protected _ended(error?: unknown): void;
    /**
     * Releases the underlying connection after the stream ended or the data callback threw. Subclasses override it.
     * @protected
     */
    protected _release(): void;
}
/**
 * Concatenates chunks into one array (the chunk itself when there is only one).
 * @param {Uint8Array[]} chunks
 * @returns {Uint8Array}
 */
export declare function concatChunks(chunks: Uint8Array[]): Uint8Array;
/**
 * Parses a transport URL.
 * @param {string | URL} url
 * @returns {URL}
 */
export declare function toUrl(url: string | URL): URL;
