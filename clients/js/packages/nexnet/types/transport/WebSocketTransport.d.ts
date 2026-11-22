/**
 * WebSocket transport over the platform `WebSocket` (browsers). Protocol spec §13.7: binary messages only, no
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
/**
 * Opens a WebSocket transport (`ClientConfig.webSocket`) with the platform `WebSocket`. TLS, headers and the HTTP
 * status of a failed upgrade are under the platform's control: a browser reports every failure the same way, so it is
 * `SocketError` with `transportError` `InternalError`. That includes an upgrade the server refuses with `403` (an
 * `Origin` outside NexNet.Asp `AllowedOrigins`), which the Node transport reports as `AuthenticationError`. A refused
 * authentication is `AuthenticationError` in a browser too: NexNet.Asp refuses a failed ASP.NET authentication (cookie
 * or query-string token) over the open socket (`DisconnectReason.Authentication`), as it does a refused greeting token
 * (`authenticate`).
 * `queryToken` is appended to the URL's query string; messages show the URL without it.
 *
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('./ITransport.js').Transport>} Satisfies {@link import('./ITransport.js').TransportFactory}.
 */
export declare function webSocketTransport(config: import('../contract/config.js').ClientConfig, { signal }: {
    signal: AbortSignal;
}): Promise<import('./ITransport.js').Transport>;
