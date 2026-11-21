/**
 * Transport contract between the session and the transports. Interface only.
 * @module
 */

import { notImplemented } from '../contract/internal.js';

/**
 * A connected byte stream. NexNet frames may span chunks in both directions.
 *
 * @typedef {object} Transport
 * @property {(chunks: Uint8Array[]) => Promise<void>} write Writes the chunks in order; resolves when the transport
 *   can take more (backpressure). The session calls it at most once per microtask and never while a write is pending.
 * @property {(callback: (data: Uint8Array) => void) => void} onData Registers the data callback. Bytes received with
 *   the upgrade response (HTTP 101) are delivered first. The session parses every complete frame synchronously in it
 *   and does not keep a reference to `data` after it returns.
 * @property {(callback: (error?: unknown) => void) => void} onClose Registers the close callback; called once.
 * @property {(reason?: unknown) => Promise<void>} close
 * @property {boolean} isSecure True for TLS, WSS, HTTPS and UDS. Any transport that does not declare itself secure is not.
 * @property {unknown} remoteCertificate The server certificate on Node TLS transports, otherwise null.
 * @property {string | null} [remoteAddress] The server's address (the IP address of the Node socket's peer, or the socket
 *   path for UDS); null or absent when the transport does not expose it (the browser WebSocket).
 * @property {number | null} [remotePort] The server's port on the Node TCP-based transports; null or absent otherwise
 *   (UDS, browser).
 * @property {string} name Transport name used in logs and the plaintext warning (`'TCP'`, `'ws'`, …).
 */

/**
 * Opens a transport for one connect attempt. Rejects with `NexusTransportError` (with a `reason`) for a connection
 * failure; any other error is reported as an exception. `signal` aborts on the connect's cancellation or
 * `ClientConfig.connectionTimeout`.
 *
 * @callback TransportFactory
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<Transport>}
 */

/**
 * Placeholder factory for a transport kind that has no implementation in this environment.
 *
 * @param {import('../contract/config.js').TransportKind} kind
 * @returns {TransportFactory}
 */
export function unimplementedTransport(kind) {
  return () => {
    throw notImplemented(`transport '${kind}'`);
  };
}
