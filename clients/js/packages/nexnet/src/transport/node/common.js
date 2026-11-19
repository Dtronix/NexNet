/// <reference types="node" />
/**
 * Node transport helpers (J06): TLS options, connect-error mapping and abort handling. Node only.
 * @module
 */

import { createHash } from 'node:crypto';
import net from 'node:net';
import tls from 'node:tls';
import { cancellationError } from '../../contract/errors.js';
import { transportError } from '../TransportBase.js';

/**
 * Builds `tls.connect` / `https.request` options from {@link import('../../contract/config.js').TlsOptions}.
 * The host name is verified (Node's `tls.checkServerIdentity`) unless `rejectUnauthorized` is false; a user
 * `checkServerIdentity` runs in addition to it, after it passes (pinning). SNI is the configured `servername`, else
 * the host when it is a DNS name (never an IP literal, RFC 6066). `pins` are not part of the result: the transports
 * check them after the handshake with {@link checkPins}.
 *
 * @param {import('../../contract/config.js').TlsOptions | undefined} options
 * @param {string} host
 * @returns {import('node:tls').ConnectionOptions}
 */
export function tlsConnectOptions(options, host) {
  /** @type {import('node:tls').ConnectionOptions} */
  const out = {};
  const o = options ?? {};
  if (o.ca !== undefined) out.ca = /** @type {any} */ (toNodeData(o.ca));
  if (o.cert !== undefined) out.cert = /** @type {any} */ (toNodeData(o.cert));
  if (o.key !== undefined) out.key = /** @type {any} */ (toNodeData(o.key));
  if (o.pfx !== undefined) out.pfx = /** @type {any} */ (toNodeData(o.pfx));
  if (o.passphrase !== undefined) out.passphrase = o.passphrase;
  out.rejectUnauthorized = o.rejectUnauthorized !== false;
  const servername = o.servername ?? (net.isIP(host) === 0 ? host : undefined);
  if (servername !== undefined) out.servername = servername;
  const extra = o.checkServerIdentity;
  if (extra !== undefined) {
    out.checkServerIdentity = (hostname, certificate) => tls.checkServerIdentity(hostname, certificate) ?? extra(hostname, certificate);
  }
  return out;
}

/** Base64 or base64url text, padded or not. */
const BASE64 = /^[A-Za-z0-9+/_-]+={0,2}$/;

/**
 * Decodes `TlsOptions.pins` into 32-byte SHA-256 values. Each pin is base64 (or base64url) text, or the raw bytes.
 *
 * @param {import('../../contract/config.js').TlsOptions | undefined} options
 * @returns {Buffer[]} Empty when no pin is set.
 * @throws {TypeError} A pin is not 32 bytes of SHA-256 in base64 or a `Uint8Array`.
 */
export function tlsPins(options) {
  const pins = options?.pins;
  if (pins === undefined || pins === null) return [];
  if (!Array.isArray(pins)) throw new TypeError('TlsOptions.pins must be an array.');
  return pins.map((pin, i) => {
    /** @type {Buffer} */
    let bytes;
    if (typeof pin === 'string') {
      if (!BASE64.test(pin)) throw new TypeError(`TlsOptions.pins[${i}] is not base64.`);
      bytes = Buffer.from(pin, 'base64');
    } else if (pin instanceof Uint8Array) {
      bytes = Buffer.from(pin);
    } else {
      throw new TypeError(`TlsOptions.pins[${i}] must be a base64 string or a Uint8Array.`);
    }
    if (bytes.length !== 32) throw new TypeError(`TlsOptions.pins[${i}] is not a SHA-256 value (32 bytes).`);
    return bytes;
  });
}

/**
 * Checks the server chain of a connected TLS socket against the pins (Java `TlsOptions` parity): a pin matches a
 * chain certificate when it equals the SHA-256 of the certificate's DER encoding or of its DER
 * SubjectPublicKeyInfo. The connection is accepted when any pin matches any certificate of the chain the server
 * sent (leaf, intermediates and, when Node can find it, the root). Runs after the handshake, so it applies with
 * `rejectUnauthorized: false` too.
 *
 * @param {import('node:tls').TLSSocket} socket
 * @param {Buffer[]} pins From {@link tlsPins}. Empty accepts every chain.
 * @returns {Error | undefined} An error with code `ERR_TLS_CERT_PIN_MISMATCH` when no pin matches.
 */
export function checkPins(socket, pins) {
  if (pins.length === 0) return undefined;
  /** @type {import('node:tls').DetailedPeerCertificate | undefined} */
  let cert = socket.getPeerCertificate(true);
  /** @type {Set<unknown>} */
  const seen = new Set();
  while (cert !== undefined && cert !== null && cert.raw !== undefined && !seen.has(cert)) {
    seen.add(cert);
    const hashes = [sha256(cert.raw)];
    if (cert.pubkey !== undefined) hashes.push(sha256(cert.pubkey));
    if (hashes.some((h) => pins.some((p) => p.equals(h)))) return undefined;
    cert = cert.issuerCertificate;
  }
  const error = /** @type {Error & { code: string }} */ (new Error('The server certificate chain matches no pinned certificate.'));
  error.code = 'ERR_TLS_CERT_PIN_MISMATCH';
  return error;
}

/**
 * @param {Uint8Array} data
 * @returns {Buffer}
 */
function sha256(data) {
  return createHash('sha256').update(data).digest();
}

/**
 * @param {string | Uint8Array | Array<string | Uint8Array>} value
 * @returns {string | Buffer | Array<string | Buffer>}
 */
function toNodeData(value) {
  if (Array.isArray(value)) return value.map((v) => /** @type {string | Buffer} */ (toNodeData(v)));
  if (typeof value === 'string') return value;
  return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
}

/** OpenSSL and Node error codes of a failed certificate check or TLS handshake. */
const TLS_CODE = /^(ERR_SSL_|ERR_TLS_|CERT_|UNABLE_TO_|DEPTH_ZERO_|SELF_SIGNED_|HOSTNAME_MISMATCH|ERR_OSSL_)/;

/**
 * True when `error` is a TLS handshake or certificate failure.
 * @param {unknown} error
 * @returns {boolean}
 */
export function isTlsError(error) {
  const code = /** @type {{ code?: unknown }} */ (error)?.code;
  return typeof code === 'string' && TLS_CODE.test(code);
}

/**
 * Maps a socket or TLS connect error to a `NexusTransportError` (protocol spec §13.1).
 *
 * @param {string} what Transport and address, for the message.
 * @param {unknown} error
 * @param {{ tlsPhase?: boolean }} [state] `tlsPhase`: the TCP connect succeeded and the TLS handshake had not.
 * @returns {import('../TransportBase.js').TransportConnectError}
 */
export function connectError(what, error, state = {}) {
  const code = /** @type {{ code?: unknown }} */ (error)?.code;
  const message = error instanceof Error ? error.message : String(error);
  /** @type {import('../TransportBase.js').TransportErrorKind} */
  let kind;
  if (state.tlsPhase || isTlsError(error)) kind = 'AuthenticationError';
  else if (code === 'ECONNREFUSED' || code === 'ENOENT') kind = 'ConnectionRefused';
  else if (code === 'ENOTFOUND' || code === 'EAI_AGAIN' || code === 'ENETUNREACH' || code === 'EHOSTUNREACH') kind = 'Unreachable';
  else if (code === 'ECONNRESET' || code === 'EPIPE') kind = 'ConnectionReset';
  else if (code === 'ECONNABORTED') kind = 'ConnectionAborted';
  else if (code === 'ETIMEDOUT') kind = 'ConnectionTimeout';
  else if (code === 'EADDRINUSE') kind = 'AddressInUse';
  else if (code === 'EADDRNOTAVAIL' || code === 'EINVAL') kind = 'InvalidAddress';
  else kind = 'InternalError';
  return transportError(`${what}: ${message}`, { kind, cause: error });
}

/**
 * Runs a connect attempt that can be aborted. `start` gets `resolve`/`reject` and returns a cleanup function that
 * destroys whatever it opened; cleanup runs when the attempt fails or is aborted, never after success.
 *
 * @template T
 * @param {AbortSignal} signal
 * @param {(resolve: (value: T) => void, reject: (error: unknown) => void) => (error?: unknown) => void} start
 * @returns {Promise<T>}
 */
export function abortableConnect(signal, start) {
  if (signal.aborted) return Promise.reject(cancellationError(signal));
  return new Promise((resolve, reject) => {
    let settled = false;
    /** @type {(error?: unknown) => void} */
    let destroy = () => {};
    const onAbort = () => fail(cancellationError(signal));
    /** @param {unknown} error */
    const fail = (error) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener('abort', onAbort);
      try {
        destroy(error);
      } finally {
        reject(error);
      }
    };
    signal.addEventListener('abort', onAbort, { once: true });
    try {
      destroy = start((value) => {
        if (settled) return;
        settled = true;
        signal.removeEventListener('abort', onAbort);
        resolve(value);
      }, fail);
    } catch (error) {
      fail(error);
    }
  });
}

/**
 * The host of a URL without IPv6 brackets.
 * @param {URL} url
 * @returns {string}
 */
export function urlHost(url) {
  const host = url.hostname;
  return host.startsWith('[') && host.endsWith(']') ? host.slice(1, -1) : host;
}
