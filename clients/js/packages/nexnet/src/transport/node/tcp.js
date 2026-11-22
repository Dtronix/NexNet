/// <reference types="node" />
/**
 * TCP, TLS-over-TCP and Unix domain socket transports (protocol spec §13.2–§13.4). Node only.
 * @module
 */

import net from 'node:net';
import tls from 'node:tls';
import { SocketTransport } from './SocketTransport.js';
import { abortableConnect, checkPins, connectError, tlsConnectOptions, tlsPins } from './common.js';

/**
 * Opens a plain TCP transport (`ClientConfig.tcp`). Sets `TCP_NODELAY` (§13.2).
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export function tcpTransport(config, { signal }) {
  const { host, port } = /** @type {import('../../contract/config.js').TcpTransportConfig} */ (config.tcp);
  const what = `TCP connect to ${host}:${port}`;
  return abortableConnect(signal, (resolve, reject) => {
    const socket = net.connect({ host, port, noDelay: true });
    const onError = (/** @type {Error} */ error) => reject(connectError(what, error));
    socket.once('error', onError);
    socket.once('connect', () => {
      socket.off('error', onError);
      resolve(new SocketTransport(socket, { name: 'tcp', isSecure: false }));
    });
    return () => socket.destroy();
  });
}

/**
 * Opens a TLS-over-TCP transport (`ClientConfig.tcpTls`). No ALPN (§13.3). A certificate or handshake failure
 * rejects with `transportError` `AuthenticationError` (`DisconnectReason.Authentication`), and so does a chain that
 * matches none of `tls.pins` (checked after the handshake, before the transport is returned).
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export function tcpTlsTransport(config, { signal }) {
  const { host, port, tls: tlsOptions } = /** @type {import('../../contract/config.js').TcpTlsTransportConfig} */ (config.tcpTls);
  const what = `TLS connect to ${host}:${port}`;
  return abortableConnect(signal, (resolve, reject) => {
    let tcpConnected = false;
    const pins = tlsPins(tlsOptions);
    const socket = tls.connect({ ...tlsConnectOptions(tlsOptions, host), host, port });
    socket.setNoDelay(true);
    const onError = (/** @type {Error} */ error) => reject(connectError(what, error, { tlsPhase: tcpConnected }));
    socket.once('error', onError);
    socket.once('connect', () => { tcpConnected = true; });
    socket.once('secureConnect', () => {
      socket.off('error', onError);
      const pinError = checkPins(socket, pins);
      if (pinError !== undefined) {
        reject(connectError(what, pinError, { tlsPhase: true }));
        return;
      }
      resolve(new SocketTransport(socket, { name: 'tls', isSecure: true }));
    });
    return () => socket.destroy();
  });
}

/**
 * Opens a Unix domain socket transport (`ClientConfig.uds`, §13.4). On Windows, Node connects `path` as a named
 * pipe, not as an `AF_UNIX` socket.
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export function udsTransport(config, { signal }) {
  const { path } = /** @type {import('../../contract/config.js').UdsTransportConfig} */ (config.uds);
  const what = `UDS connect to ${path}`;
  return abortableConnect(signal, (resolve, reject) => {
    const socket = net.connect({ path });
    const onError = (/** @type {Error} */ error) => reject(connectError(what, error));
    socket.once('error', onError);
    socket.once('connect', () => {
      socket.off('error', onError);
      const transport = new SocketTransport(socket, { name: 'uds', isSecure: true });
      // A UDS peer has no address of its own: report the socket path, as C# does.
      transport.remoteAddress = path;
      resolve(transport);
    });
    return () => socket.destroy();
  });
}
