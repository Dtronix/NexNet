/// <reference types="node" />
/**
 * HTTP/1.1 upgrade request shared by the HttpSocket and WebSocket transports. Node only.
 * @module
 */

import http from 'node:http';
import https from 'node:https';
import { transportError, upgradeRefused } from '../TransportBase.js';
import { abortableConnect, checkPins, connectError, tlsConnectOptions, tlsPins, urlHost } from './common.js';

/**
 * @typedef {object} UpgradeResult
 * @property {import('node:http').IncomingMessage} response The `101` response.
 * @property {import('node:net').Socket} socket The upgraded connection (a `TLSSocket` for `https:`/`wss:`).
 * @property {Buffer} head Bytes received after the response headers; they belong to the upgraded stream.
 */

/**
 * Sends `GET <path>` with `Connection: Upgrade` on a fresh connection (no agent, no pooling) and resolves on the
 * `101`. Any other status rejects with the mapped `NexusTransportError` (`upgradeRefused`). A `3xx` is a redirect,
 * which is never followed: it rejects with `ProtocolError` and no second request is made (protocol spec §13.6/§13.7),
 * so the `Authorization` header and any query token never reach another URL. Over TLS it offers
 * ALPN `http/1.1` only (§13.6), so the server cannot pick HTTP/2. With `tls.pins` set, the request (and so its
 * headers) is sent only after the handshake and a matching pin; a mismatch rejects with `AuthenticationError`.
 *
 * @param {object} init
 * @param {'HttpSocket' | 'WebSocket'} init.transport For messages and status mapping.
 * @param {URL} init.url `http:`/`https:` (`ws:`/`wss:` are mapped by the caller).
 * @param {boolean} init.secure
 * @param {Record<string, string>} init.headers Upgrade headers (`Upgrade`, `Connection`, …) and the user's headers.
 * @param {import('../../contract/config.js').TlsOptions} [init.tls]
 * @param {AbortSignal} init.signal
 * @returns {Promise<UpgradeResult>}
 */
export function httpUpgrade({ transport, url, secure, headers, tls, signal }) {
  const host = urlHost(url);
  const what = `${transport} connect to ${url.href}`;
  return abortableConnect(signal, (resolve, reject) => {
    /** @type {import('node:https').RequestOptions} */
    const options = {
      method: 'GET',
      host,
      port: url.port === '' ? (secure ? 443 : 80) : Number(url.port),
      path: `${url.pathname}${url.search}`,
      headers,
      agent: false,
    };
    const pins = secure ? tlsPins(tls) : [];
    if (secure) Object.assign(options, tlsConnectOptions(tls, host), { ALPNProtocols: ['http/1.1'] });
    const request = (secure ? https : http).request(options);
    let tcpConnected = false;
    let tlsDone = !secure;
    request.once('socket', (socket) => {
      socket.setNoDelay(true);
      socket.once('connect', () => { tcpConnected = true; });
      if (secure) {
        socket.once('secureConnect', () => {
          tlsDone = true;
          if (pins.length === 0) return;
          const pinError = checkPins(/** @type {import('node:tls').TLSSocket} */ (socket), pins);
          if (pinError !== undefined) reject(connectError(what, pinError, { tlsPhase: true }));
          else request.end();
        });
      }
    });
    request.on('error', (error) => reject(connectError(what, error, { tlsPhase: tcpConnected && !tlsDone })));
    request.once('response', (response) => {
      response.resume();
      reject(refusal(transport, response.statusCode ?? 0, url.href));
    });
    request.once('upgrade', (response, socket, head) => {
      if (response.statusCode !== 101) {
        socket.destroy();
        reject(refusal(transport, response.statusCode ?? 0, url.href));
        return;
      }
      // Keep the bytes that arrive between now and the transport taking the socket.
      socket.pause();
      resolve({ response, socket, head });
    });
    // Pinned: hold the request until the pins are checked, so no header reaches an unpinned server.
    if (pins.length === 0) request.end();
    return () => request.destroy();
  });
}

/**
 * The rejection for an upgrade answered with a status other than `101`. A `3xx` redirect is `ProtocolError` for
 * both transports (it is never followed); every other status maps as in `upgradeRefused`.
 *
 * @param {'HttpSocket' | 'WebSocket'} transport
 * @param {number} status
 * @param {string} url
 */
function refusal(transport, status, url) {
  if (status >= 300 && status <= 399) {
    return transportError(
      `${transport} upgrade to ${url} was answered with redirect HTTP ${status}; redirects are not followed.`,
      { kind: 'ProtocolError', status },
    );
  }
  return upgradeRefused(transport, status, url);
}

/**
 * True when the comma-separated header value contains `token` (case-insensitive).
 * @param {string | string[] | undefined} value
 * @param {string} token
 * @returns {boolean}
 */
export function headerHasToken(value, token) {
  if (value === undefined) return false;
  const text = Array.isArray(value) ? value.join(',') : value;
  const wanted = token.toLowerCase();
  return text.split(',').some((part) => part.trim().toLowerCase() === wanted);
}
