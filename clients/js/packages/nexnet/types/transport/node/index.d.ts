/**
 * The Node transport factories, keyed by `ClientConfig` transport property. Node only.
 * @module
 */
import { httpSocketTransport } from './httpSocket.js';
import { tcpTlsTransport, tcpTransport, udsTransport } from './tcp.js';
import { nodeWebSocketTransport } from './ws.js';
export { httpSocketTransport, tcpTransport, tcpTlsTransport, udsTransport, nodeWebSocketTransport };
/**
 * Every transport of the Node entry. `webSocket` is the RFC 6455 client of `ws.js` (TLS options and headers),
 * not the platform `WebSocket`.
 */
export declare const nodeTransports: Readonly<{
    webSocket: typeof nodeWebSocketTransport;
    httpSocket: typeof httpSocketTransport;
    tcp: typeof tcpTransport;
    tcpTls: typeof tcpTlsTransport;
    uds: typeof udsTransport;
}>;
