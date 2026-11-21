/**
 * Plumbing shared by the built-in transports: connect-error construction, and the {@link TransportBase} class
 * that buffers data and the close until the session registers its callbacks. Platform-neutral: reachable from the
 * browser entry, so it must not import `node:*`.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';
import { NexusTransportError } from '../contract/errors.js';

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
export function transportError(message, { kind = 'InternalError', cause, status } = {}) {
  const reason = kind === 'AuthenticationError' ? DisconnectReason.Authentication : DisconnectReason.SocketError;
  const error = /** @type {TransportConnectError} */ (new NexusTransportError(message, { reason, cause }));
  error.transportError = kind;
  if (status !== undefined) error.status = status;
  return error;
}

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
export function upgradeRefused(transport, status, url) {
  /** @type {TransportErrorKind} */
  let kind;
  if (status === 401 || status === 403) kind = 'AuthenticationError';
  else if (transport === 'WebSocket' && (status < 500 || status > 599)) kind = 'ProtocolError';
  else kind = 'InternalError';
  return transportError(`${transport} upgrade to ${url} was refused with HTTP ${status}.`, { kind, status });
}

/**
 * Base class of the built-in transports. A subclass reports received bytes with {@link TransportBase#_deliver} and
 * the end of the stream with {@link TransportBase#_ended}. Bytes received before the session registers `onData`
 * (the bytes that came with the HTTP 101, for example) are kept and delivered first, in order; a close that happens
 * before the callbacks are registered is reported after those bytes.
 */
export class TransportBase {
  /** @type {boolean} */
  isSecure;
  /** @type {unknown} */
  remoteCertificate = null;
  /** @type {string | null} */
  remoteAddress = null;
  /** @type {number | null} */
  remotePort = null;
  /** @type {string} */
  name;

  /** @type {((data: Uint8Array) => void) | null} */
  #onData = null;
  /** @type {((error?: unknown) => void) | null} */
  #onClose = null;
  /** @type {Uint8Array[]} */
  #early = [];
  #ended = false;
  #closeReported = false;
  /** @type {unknown} */
  #endError = undefined;

  /**
   * @param {string} name
   * @param {boolean} isSecure
   */
  constructor(name, isSecure) {
    this.name = name;
    this.isSecure = isSecure;
  }

  /** True once the stream has ended (either side). @returns {boolean} */
  get ended() {
    return this.#ended;
  }

  /** @param {(data: Uint8Array) => void} callback */
  onData(callback) {
    this.#onData = callback;
    const early = this.#early;
    this.#early = [];
    for (const chunk of early) {
      if (!this.#dispatch(callback, chunk)) return;
    }
  }

  /** @param {(error?: unknown) => void} callback */
  onClose(callback) {
    this.#onClose = callback;
    // Reported from a microtask, so bytes that arrived before the close reach a data callback registered right after.
    if (this.#ended) queueMicrotask(() => this.#reportClose());
  }

  /**
   * Hands received bytes to the session (or keeps them until it registers).
   * @protected
   * @param {Uint8Array} data
   */
  _deliver(data) {
    if (this.#ended || data.length === 0) return;
    const callback = this.#onData;
    if (callback === null) this.#early.push(data);
    else this.#dispatch(callback, data);
  }

  /**
   * Marks the stream ended and reports it once. Later calls are ignored.
   * @protected
   * @param {unknown} [error]
   */
  _ended(error) {
    if (this.#ended) return;
    this.#ended = true;
    this.#endError = error;
    this._release();
    if (this.#onClose !== null) this.#reportClose();
  }

  /**
   * Releases the underlying connection after the stream ended or the data callback threw. Subclasses override it.
   * @protected
   */
  _release() {}

  /**
   * @param {(data: Uint8Array) => void} callback
   * @param {Uint8Array} data
   * @returns {boolean} false when the callback threw (the transport is then ended).
   */
  #dispatch(callback, data) {
    try {
      callback(data);
      return true;
    } catch (error) {
      this._ended(error);
      return false;
    }
  }

  #reportClose() {
    const callback = this.#onClose;
    if (this.#closeReported || callback === null) return;
    this.#closeReported = true;
    callback(this.#endError);
  }
}

/**
 * Concatenates chunks into one array (the chunk itself when there is only one).
 * @param {Uint8Array[]} chunks
 * @returns {Uint8Array}
 */
export function concatChunks(chunks) {
  if (chunks.length === 1) return chunks[0];
  let length = 0;
  for (const chunk of chunks) length += chunk.length;
  const out = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.length;
  }
  return out;
}

/**
 * Parses a transport URL.
 * @param {string | URL} url
 * @returns {URL}
 */
export function toUrl(url) {
  return url instanceof URL ? url : new URL(url);
}
