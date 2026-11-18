/**
 * Transport contract between the session (J05) and the transports (J06).
 * J05 moves the interface to `src/transport/ITransport.js`.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * A connected byte stream. NexNet frames may span chunks in both directions.
 *
 * @typedef {object} Transport
 * @property {(chunks: Uint8Array[]) => Promise<void>} write Writes the chunks in order; resolves when the transport
 *   can take more (backpressure).
 * @property {(callback: (data: Uint8Array) => void) => void} onData Registers the data callback. Bytes received with
 *   the upgrade response (HTTP 101) are delivered first.
 * @property {(callback: (error?: unknown) => void) => void} onClose Registers the close callback; called once.
 * @property {(reason?: unknown) => Promise<void>} close
 * @property {boolean} isSecure True for TLS, WSS, HTTPS and UDS. Any transport that does not declare itself secure is not.
 * @property {unknown} remoteCertificate The server certificate on Node TLS transports, otherwise null.
 * @property {string} name Transport name used in logs and the plaintext warning (`'TCP'`, `'ws'`, …).
 */

/**
 * Opens a transport for one connect attempt.
 *
 * @callback TransportFactory
 * @param {import('./config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<Transport>}
 */

/**
 * Placeholder factory used until the transport card lands.
 *
 * @param {import('./config.js').TransportKind} kind
 * @returns {TransportFactory}
 */
export function unimplementedTransport(kind) {
  return () => {
    throw notImplemented(`transport '${kind}'`);
  };
}
