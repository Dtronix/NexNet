/// <reference types="node" />
/**
 * Node transport helpers (J06): TLS options, connect-error mapping and abort handling. Node only.
 * @module
 */

import net from 'node:net';
import tls from 'node:tls';
import { cancellationError } from '../../contract/errors.js';
import { transportError } from '../TransportBase.js';

/**
 * Builds `tls.connect` / `https.request` options from {@link import('../../contract/config.js').TlsOptions}.
 * The host name is verified (Node's `tls.checkServerIdentity`) unless `rejectUnauthorized` is false; a user
 * `checkServerIdentity` runs in addition to it, after it passes (pinning). SNI is the configured `servername`, else
 * the host when it is a DNS name (never an IP literal, RFC 6066).
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
