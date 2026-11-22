/// <reference types="node" />
/**
 * A transport over a connected Node stream socket: TCP, TLS, UDS, and an upgraded HttpSocket connection. Node only.
 * @module
 */

import { TransportBase, transportError } from '../TransportBase.js';

/** How long `close` waits for the socket to finish after `end()` before destroying it. */
const CLOSE_WAIT_MS = 250;

/**
 * Wraps a connected `net.Socket` (or `tls.TLSSocket`). Writes resolve at once while the socket buffers less than
 * its high-water mark, and after `'drain'` otherwise.
 */
export class SocketTransport extends TransportBase {
  /** @type {import('node:net').Socket} */
  #socket;
  /** @type {Promise<void>} */
  #socketClosed;

  /**
   * @param {import('node:net').Socket} socket A connected socket.
   * @param {{ name: string, isSecure: boolean, head?: Uint8Array }} options `head`: bytes that arrived with the
   *   upgrade response; they are delivered first.
   */
  constructor(socket, { name, isSecure, head }) {
    super(name, isSecure);
    this.#socket = socket;
    if (typeof (/** @type {any} */ (socket).getPeerX509Certificate) === 'function') {
      this.remoteCertificate = /** @type {import('node:tls').TLSSocket} */ (socket).getPeerX509Certificate() ?? null;
    }
    this.remoteAddress = socket.remoteAddress ?? null;
    this.remotePort = socket.remotePort ?? null;
    if (head !== undefined && head.length > 0) this._deliver(head);
    this.#socketClosed = new Promise((resolve) => socket.once('close', () => resolve(undefined)));
    socket.on('data', (/** @type {Buffer} */ data) => this._deliver(data));
    socket.on('error', (error) => this._ended(error));
    socket.on('end', () => this._ended());
    socket.on('close', () => this._ended());
    socket.resume();
  }

  /** @param {Uint8Array[]} chunks @returns {Promise<void>} */
  write(chunks) {
    const socket = this.#socket;
    if (this.ended || socket.destroyed || !socket.writable) {
      return Promise.reject(transportError(`${this.name} connection is closed.`, { kind: 'ConnectionAborted' }));
    }
    socket.cork();
    let more = true;
    for (const chunk of chunks) {
      if (chunk.length > 0) more = socket.write(chunk);
    }
    socket.uncork();
    if (more) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const onDrain = () => {
        socket.off('close', onClose);
        resolve();
      };
      const onClose = () => {
        socket.off('drain', onDrain);
        reject(transportError(`${this.name} connection closed while writing.`, { kind: 'ConnectionAborted' }));
      };
      socket.once('drain', onDrain);
      socket.once('close', onClose);
    });
  }

  /** Ends the socket, waits up to 250 ms for it to close, then destroys it. @param {unknown} [reason] @returns {Promise<void>} */
  async close(reason) {
    const socket = this.#socket;
    if (!socket.destroyed) {
      socket.end();
      /** @type {ReturnType<typeof setTimeout> | undefined} */
      let timer;
      await Promise.race([this.#socketClosed, new Promise((resolve) => { timer = setTimeout(resolve, CLOSE_WAIT_MS); })]);
      clearTimeout(timer);
      socket.destroy();
    }
    this._ended();
  }

  /** @protected */
  _release() {
    if (!this.#socket.destroyed) this.#socket.destroy();
  }
}
