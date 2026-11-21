/**
 * WebSocket transport over the platform `WebSocket` (browsers). Protocol spec §13.7: binary messages only, no
 * subprotocol, message boundaries carry no meaning. The Node entry uses its own RFC 6455 client
 * (`node/ws.js`) instead, for TLS options and headers.
 * @module
 */

import { cancellationError } from '../contract/errors.js';
import { TransportBase, concatChunks, toUrl, transportError } from './TransportBase.js';

/** Buffered bytes above which `write` waits for the socket to drain. */
export const WEBSOCKET_HIGH_WATER_MARK = 1024 * 1024;
/** How often `write` checks `bufferedAmount` while it waits. */
const DRAIN_POLL_MS = 5;
/** How long `close` waits for the server's Close (C# waits 250 ms too). */
const CLOSE_WAIT_MS = 250;

const OPEN = 1;
const CLOSED = 3;

/**
 * A transport over a platform `WebSocket`. Created by {@link webSocketTransport}.
 */
export class BrowserWebSocketTransport extends TransportBase {
  /** @type {WebSocket} */
  #socket;
  #highWaterMark;
  /** @type {Promise<void>} */
  #closedEvent;

  /**
   * @param {WebSocket} socket A socket in the CONNECTING or OPEN state.
   * @param {URL} url
   * @param {number} [highWaterMark]
   */
  constructor(socket, url, highWaterMark = WEBSOCKET_HIGH_WATER_MARK) {
    super(`${url.protocol}//`, url.protocol === 'wss:');
    this.#socket = socket;
    this.#highWaterMark = highWaterMark;
    socket.binaryType = 'arraybuffer';
    socket.addEventListener('message', (event) => this.#onMessage(event));
    this.#closedEvent = new Promise((resolve) => {
      socket.addEventListener('close', (event) => {
        this._ended(event.code === 1000 ? undefined : transportError(`WebSocket closed with code ${event.code}.`, { kind: 'ConnectionAborted' }));
        resolve(undefined);
      }, { once: true });
    });
  }

  /** @param {Uint8Array[]} chunks @returns {Promise<void>} */
  async write(chunks) {
    const socket = this.#socket;
    if (this.ended || socket.readyState !== OPEN) throw transportError('WebSocket is closed.', { kind: 'ConnectionAborted' });
    const data = concatChunks(chunks);
    if (data.length === 0) return;
    socket.send(/** @type {Uint8Array<ArrayBuffer>} */ (data));
    while (socket.bufferedAmount > this.#highWaterMark) {
      await new Promise((resolve) => setTimeout(resolve, DRAIN_POLL_MS));
      if (this.ended || socket.readyState !== OPEN) throw transportError('WebSocket closed while writing.', { kind: 'ConnectionAborted' });
    }
  }

  /** Starts the close handshake (code 1000) and waits up to 250 ms for it. @param {unknown} [reason] @returns {Promise<void>} */
  async close(reason) {
    const socket = this.#socket;
    if (socket.readyState !== CLOSED) {
      try {
        socket.close(1000);
      } catch {
        // Already closing.
      }
      /** @type {ReturnType<typeof setTimeout> | undefined} */
      let timer;
      await Promise.race([this.#closedEvent, new Promise((resolve) => { timer = setTimeout(resolve, CLOSE_WAIT_MS); })]);
      clearTimeout(timer);
    }
    this._ended();
  }

  /** @param {MessageEvent} event */
  #onMessage(event) {
    const data = event.data;
    if (data instanceof ArrayBuffer) {
      this._deliver(new Uint8Array(data));
      return;
    }
    // §13.7: binary messages only.
    try {
      this.#socket.close(1003);
    } catch {
      // Already closing.
    }
    this._ended(transportError('WebSocket received a text message; NexNet uses binary messages only.', { kind: 'ProtocolError' }));
  }
}

/**
 * Opens a WebSocket transport (`ClientConfig.webSocket`) with the platform `WebSocket`. TLS, headers and the HTTP
 * status of a failed upgrade are under the platform's control: a browser reports every failure the same way, so it is
 * `SocketError` with `transportError` `InternalError`. That includes an upgrade the server refuses with `403` (an
 * `Origin` outside NexNet.Asp `AllowedOrigins`), which the Node transport reports as `AuthenticationError`. A refused
 * authentication is `AuthenticationError` in a browser too: NexNet.Asp refuses a failed ASP.NET authentication (cookie
 * or query-string token) over the open socket (`DisconnectReason.Authentication`), as it does a refused greeting token
 * (`authenticate`).
 *
 * @type {import('./ITransport.js').TransportFactory}
 */
export async function webSocketTransport(config, { signal }) {
  const options = /** @type {import('../contract/config.js').WebSocketTransportConfig} */ (config.webSocket);
  const url = toUrl(options.url);
  if (signal.aborted) throw cancellationError(signal);
  const WebSocketCtor = globalThis.WebSocket;
  if (typeof WebSocketCtor !== 'function') throw transportError('WebSocket is not available on this platform.');

  // No protocols argument: NexNet requests no subprotocol (§13.7).
  const socket = new WebSocketCtor(url.href);
  const transport = new BrowserWebSocketTransport(socket, url);
  await new Promise((resolve, reject) => {
    const cleanup = () => {
      socket.removeEventListener('open', onOpen);
      socket.removeEventListener('error', onFail);
      socket.removeEventListener('close', onFail);
      signal.removeEventListener('abort', onAbort);
    };
    const onOpen = () => {
      cleanup();
      resolve(undefined);
    };
    // A failed connect fires 'error' then 'close' (WHATWG "fail the WebSocket connection"). Either one fails the
    // connect: Node 22's undici fires only 'error' for a refused upgrade and leaves the socket CONNECTING.
    const onFail = () => {
      cleanup();
      reject(transportError(
        `WebSocket connection to ${url.href} failed. The browser does not say why: the server may be unreachable, the ` +
        'TLS handshake may have failed, or the server may have refused the upgrade (HTTP 403 for an origin it does not ' +
        'allow, for example).',
      ));
    };
    const onAbort = () => {
      cleanup();
      try {
        socket.close();
      } catch {
        // Already closing.
      }
      reject(cancellationError(signal));
    };
    socket.addEventListener('open', onOpen);
    socket.addEventListener('error', onFail);
    socket.addEventListener('close', onFail);
    signal.addEventListener('abort', onAbort);
  });
  return transport;
}
