/**
 * WebSocket transport over the platform `WebSocket` (browsers; D6). Protocol spec §13.7: binary messages only, no
 * subprotocol, message boundaries carry no meaning. The Node entry uses its own RFC 6455 client
 * (`node/ws.js`) instead, for TLS options and headers.
 * @module
 */
import { TransportBase } from './TransportBase.js';
/** Buffered bytes above which `write` waits for the socket to drain. */
export declare const WEBSOCKET_HIGH_WATER_MARK: number;
/**
 * A transport over a platform `WebSocket`. Created by {@link webSocketTransport}.
 */
export declare class BrowserWebSocketTransport extends TransportBase {
    #private;
    /**
     * @param {WebSocket} socket A socket in the CONNECTING or OPEN state.
     * @param {URL} url
     * @param {number} [highWaterMark]
     */
    constructor(socket: WebSocket, url: URL, highWaterMark?: number);
    /** @param {Uint8Array[]} chunks @returns {Promise<void>} */
    write(chunks: Uint8Array[]): Promise<void>;
    /** Starts the close handshake (code 1000) and waits up to 250 ms for it. @param {unknown} [reason] @returns {Promise<void>} */
    close(reason?: unknown): Promise<void>;
}
