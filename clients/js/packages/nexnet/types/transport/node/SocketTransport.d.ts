/**
 * A transport over a connected Node stream socket: TCP, TLS, UDS, and an upgraded HttpSocket connection. Node only.
 * @module
 */
import { TransportBase } from '../TransportBase.js';
/**
 * Wraps a connected `net.Socket` (or `tls.TLSSocket`). Writes resolve at once while the socket buffers less than
 * its high-water mark, and after `'drain'` otherwise.
 */
export declare class SocketTransport extends TransportBase {
    #private;
    /**
     * @param {import('node:net').Socket} socket A connected socket.
     * @param {{ name: string, isSecure: boolean, head?: Uint8Array }} options `head`: bytes that arrived with the
     *   upgrade response; they are delivered first.
     */
    constructor(socket: import('node:net').Socket, { name, isSecure, head }: {
        name: string;
        isSecure: boolean;
        head?: Uint8Array;
    });
    /** @param {Uint8Array[]} chunks @returns {Promise<void>} */
    write(chunks: Uint8Array[]): Promise<void>;
    /** Ends the socket, waits up to 250 ms for it to close, then destroys it. @param {unknown} [reason] @returns {Promise<void>} */
    close(reason?: unknown): Promise<void>;
    /** @protected */
    protected _release(): void;
}
