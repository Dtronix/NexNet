/**
 * RFC 6455 WebSocket client for Node (protocol spec §13.7): the opening handshake over `http`/`https`, masked binary
 * frames out, fragmented binary messages in (their payload streamed to the session as it arrives), Ping/Pong and the
 * close handshake. No subprotocol and no extensions. Node only.
 * @module
 */
import { TransportBase } from '../TransportBase.js';
/** WebSocket opcodes. */
export declare const Opcode: Readonly<{
    Continuation: 0;
    Text: 1;
    Binary: 2;
    Close: 8;
    Ping: 9;
    Pong: 10;
}>;
/** Close codes this client sends. */
export declare const CloseCode: Readonly<{
    Normal: 1000;
    ProtocolError: 1002;
    UnsupportedData: 1003;
    MessageTooBig: 1009;
}>;
/**
 * `Sec-WebSocket-Accept` for a key (RFC 6455 §4.2.2).
 * @param {string} key
 * @returns {string}
 */
export declare function acceptKey(key: string): string;
/**
 * Encodes one client frame: FIN set, masked with a fresh key (RFC 6455 §5.3).
 *
 * @param {number} opcode
 * @param {Uint8Array[]} chunks Payload, concatenated.
 * @returns {Buffer}
 */
export declare function encodeClientFrame(opcode: number, chunks: Uint8Array[]): Buffer;
/**
 * Error raised by {@link FrameDecoder} on a frame that breaks RFC 6455 or §13.7. `closeCode` is the code to fail the
 * connection with.
 */
export declare class WebSocketProtocolError extends Error {
    closeCode: number;
    /**
     * @param {string} message
     * @param {number} [closeCode]
     */
    constructor(message: string, closeCode?: number);
}
/**
 * Incremental decoder of server frames. Binary payload is passed to `onBinary` as soon as it arrives, so a message
 * (or a frame) split across reads, and a message split into continuation frames, become one byte stream.
 * Control frames may arrive between the fragments of a message.
 */
export declare class FrameDecoder {
    #private;
    onBinary: (data: Uint8Array) => void;
    onControl: (opcode: number, payload: Uint8Array) => void;
    /**
     * @param {object} handlers
     * @param {(data: Uint8Array) => void} handlers.onBinary
     * @param {(opcode: number, payload: Uint8Array) => void} handlers.onControl Close, Ping or Pong, complete.
     */
    constructor({ onBinary, onControl }: {
        onBinary: (data: Uint8Array) => void;
        onControl: (opcode: number, payload: Uint8Array) => void;
    });
    /**
     * Feeds bytes. Throws {@link WebSocketProtocolError} on a malformed frame; the decoder must not be used after that.
     * @param {Uint8Array} data
     */
    push(data: Uint8Array): void;
}
/**
 * A NexNet stream over an RFC 6455 connection. Created by {@link nodeWebSocketTransport}.
 */
export declare class NodeWebSocketTransport extends TransportBase {
    #private;
    /** Close code received from the server, or 0. */
    closeCode: number;
    /**
     * @param {import('node:net').Socket} socket The upgraded connection.
     * @param {{ name: string, isSecure: boolean, head?: Uint8Array }} options
     */
    constructor(socket: import('node:net').Socket, { name, isSecure, head }: {
        name: string;
        isSecure: boolean;
        head?: Uint8Array;
    });
    /**
     * Sends the chunks as one binary message.
     * @param {Uint8Array[]} chunks
     * @returns {Promise<void>}
     */
    write(chunks: Uint8Array[]): Promise<void>;
    /**
     * Starts the close handshake with code 1000 and waits up to 250 ms for the server to finish it, then closes the
     * connection.
     * @param {unknown} [reason]
     * @returns {Promise<void>}
     */
    close(reason?: unknown): Promise<void>;
    /** Sends a Ping control frame (keepalive at the WebSocket layer; not a NexNet ping). @param {Uint8Array} [payload] */
    ping(payload?: Uint8Array): void;
    /** @protected */
    protected _release(): void;
}
