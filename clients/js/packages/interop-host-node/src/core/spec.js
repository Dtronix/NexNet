/**
 * `ClientSpec` (§6.4.1) and pool options (§6.11.1) to runtime configuration.
 * @module
 */

import { badRequest, notSupported } from './errors.js';
import { remoteExceptionDetailValue, runtimeLevel } from './names.js';

/** The members of `ClientSpec` this host knows; any other member is refused (§6.4.1). */
const SPEC_MEMBERS = new Set([
  'transport', 'address', 'tlsServerName', 'caPem', 'clientCertificatePem', 'clientKeyPem', 'pinSha256',
  'insecureSkipVerify', 'connectionTimeoutMs', 'timeoutMs', 'handshakeTimeoutMs', 'pingIntervalMs', 'maxMessageSize',
  'invocationCredits', 'maxConcurrentConnectionInvocations', 'remoteExceptionDetail', 'onInvocationException',
  'reconnectDelaysMs', 'reconnectContinuously', 'defaultReconnection', 'authToken', 'authorizationHeader', 'fakeTime', 'logLevel',
]);

const POOL_MEMBERS = new Set(['maxConnections', 'maxIdleTimeMs', 'minIdleConnections']);

/**
 * What a platform's client can do.
 * @typedef {object} SpecLimits
 * @property {readonly string[]} transports `ClientTransport` names.
 * @property {boolean} browser True in a browser: the browser does the TLS (the launcher's context trusts the test CA)
 *   and sends no custom headers.
 */

/**
 * The parsed spec: the runtime config (without clock and logger) and the host-side options.
 * @typedef {object} ParsedSpec
 * @property {Record<string, any>} config
 * @property {boolean} fakeTime
 * @property {string | null} logLevel The runtime level, or null for no client logging.
 * @property {{ code: number | null, message: string | null } | null} onInvocationException
 * @property {string} transport
 */

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {number | undefined}
 */
function int(value, name) {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== 'number' || !Number.isInteger(value)) throw badRequest('ClientSpec.' + name + ' is not an integer.');
  return value;
}

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {string | undefined}
 */
function str(value, name) {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== 'string') throw badRequest('ClientSpec.' + name + ' is not a string.');
  return value;
}

/**
 * Host and port of `tcp://host:port` or `tls://host:port`.
 * @param {string} address
 * @param {string} scheme
 * @returns {{ host: string, port: number }}
 */
function endpoint(address, scheme) {
  const text = address.startsWith(scheme) ? address : scheme + address;
  /** @type {URL} */
  let url;
  try {
    url = new URL(text);
  } catch {
    throw badRequest('ClientSpec.address is not an endpoint: ' + address);
  }
  const port = Number(url.port);
  if (!url.hostname || !Number.isInteger(port) || port <= 0) throw badRequest('ClientSpec.address has no host and port: ' + address);
  const host = url.hostname.startsWith('[') && url.hostname.endsWith(']') ? url.hostname.slice(1, -1) : url.hostname;
  return { host, port };
}

/**
 * The C# `DefaultReconnectionPolicy(delays, continuously)`: delay `attempt` while there is one, then the last one
 * forever when continuous, else stop.
 * @param {readonly number[]} delays
 * @param {boolean} continuously
 * @returns {{ reconnectDelay: (attempt: number) => number | null }}
 */
export function reconnectionPolicy(delays, continuously) {
  const copy = [...delays];
  return {
    reconnectDelay(attempt) {
      if (attempt < copy.length) return copy[attempt];
      return continuously && copy.length > 0 ? copy[copy.length - 1] : null;
    },
  };
}

/**
 * Converts a `ClientSpec`. Unknown members and options the client cannot honor fail with `NotSupported`; malformed
 * values with `BadRequest`.
 * @param {unknown} spec
 * @param {SpecLimits} limits
 * @returns {ParsedSpec}
 */
export function parseSpec(spec, limits) {
  if (typeof spec !== 'object' || spec === null || Array.isArray(spec)) throw badRequest('client.create needs a spec object.');
  const s = /** @type {Record<string, any>} */ (spec);
  for (const name of Object.keys(s)) {
    if (!SPEC_MEMBERS.has(name)) throw notSupported('This host does not know the ClientSpec member \'' + name + '\'.');
  }

  const transport = str(s.transport, 'transport');
  if (!transport) throw badRequest('ClientSpec.transport is required.');
  if (!limits.transports.includes(transport)) throw notSupported('This client has no ' + transport + ' transport.');
  const address = str(s.address, 'address') ?? '';
  const secure = transport === 'TcpTls' || transport === 'HttpSocketTls' || transport === 'WebSocketTls';

  const clientCertificate = str(s.clientCertificatePem, 'clientCertificatePem');
  const clientKey = str(s.clientKeyPem, 'clientKeyPem');
  const pin = str(s.pinSha256, 'pinSha256');
  const authorization = str(s.authorizationHeader, 'authorizationHeader');
  if (limits.browser) {
    if (clientCertificate !== undefined) throw notSupported('A browser cannot present a client certificate.');
    if (pin !== undefined) throw notSupported('A browser cannot pin certificates.');
    if (authorization !== undefined) throw notSupported('A browser cannot send an Authorization header on a WebSocket.');
  }
  if (clientCertificate !== undefined && clientKey === undefined) throw badRequest('ClientSpec.clientCertificatePem needs clientKeyPem.');

  /**
   * TLS options (Node), as the C# in-process driver builds them: skip verification, else a pin alone, else the CA.
   * @param {string | undefined} serverName
   * @returns {Record<string, any>}
   */
  const tls = (serverName) => {
    /** @type {Record<string, any>} */
    const options = {};
    if (s.insecureSkipVerify === true) {
      options.rejectUnauthorized = false;
    } else if (pin !== undefined) {
      options.pins = [pin];
      options.rejectUnauthorized = false;
    } else {
      const ca = str(s.caPem, 'caPem');
      if (ca !== undefined) options.ca = ca;
    }
    if (serverName !== undefined) options.servername = serverName;
    if (clientCertificate !== undefined) {
      options.cert = clientCertificate;
      options.key = clientKey;
    }
    return options;
  };

  /** @type {Record<string, any>} */
  const config = {};
  /** @type {Record<string, string> | undefined} */
  const headers = authorization !== undefined ? { Authorization: authorization } : undefined;
  switch (transport) {
    case 'Tcp':
      config.tcp = endpoint(address, 'tcp://');
      break;
    case 'TcpTls':
      config.tcpTls = { ...endpoint(address, 'tls://'), tls: tls(str(s.tlsServerName, 'tlsServerName') ?? 'localhost') };
      break;
    case 'Uds':
      config.uds = { path: address.startsWith('uds:') ? address.slice(4) : address };
      break;
    case 'HttpSocket':
    case 'HttpSocketTls':
      config.httpSocket = { url: address, ...(headers ? { headers } : {}), ...(secure ? { tls: tls(undefined) } : {}) };
      break;
    case 'WebSocket':
    case 'WebSocketTls':
      config.webSocket = { url: address, ...(headers ? { headers } : {}), ...(secure && !limits.browser ? { tls: tls(undefined) } : {}) };
      break;
    default:
      throw notSupported('This client has no ' + transport + ' transport.');
  }
  if (headers && !config.httpSocket && !config.webSocket) {
    throw notSupported('An Authorization header needs an HTTP transport, not ' + transport + '.');
  }

  const token = str(s.authToken, 'authToken');
  if (token !== undefined) {
    const bytes = new TextEncoder().encode(token);
    config.authenticate = () => bytes;
  }

  /** @type {Array<[string, string]>} */
  const numbers = [
    ['connectionTimeoutMs', 'connectionTimeout'],
    ['timeoutMs', 'timeout'],
    ['handshakeTimeoutMs', 'handshakeTimeout'],
    ['pingIntervalMs', 'pingInterval'],
    ['maxMessageSize', 'maxMessageSize'],
    ['invocationCredits', 'invocationCredits'],
    ['maxConcurrentConnectionInvocations', 'maxConcurrentConnectionInvocations'],
  ];
  for (const [from, to] of numbers) {
    const value = int(s[from], from);
    if (value !== undefined) config[to] = value;
  }

  const detail = str(s.remoteExceptionDetail, 'remoteExceptionDetail');
  if (detail !== undefined) {
    const value = remoteExceptionDetailValue(detail);
    if (value === undefined) throw badRequest('Unknown RemoteExceptionDetail \'' + detail + '\'.');
    config.remoteExceptionDetail = value;
  }

  // Null turns reconnection off (a browser client otherwise reconnects by default); defaultReconnection leaves the
  // property unset, so the runtime's default applies (the default policy in a browser, none in Node).
  if (s.defaultReconnection !== undefined && s.defaultReconnection !== null && typeof s.defaultReconnection !== 'boolean') {
    throw badRequest('ClientSpec.defaultReconnection is not a boolean.');
  }
  if (s.defaultReconnection !== true) config.reconnectionPolicy = null;
  if (s.reconnectDelaysMs !== undefined && s.reconnectDelaysMs !== null) {
    if (!Array.isArray(s.reconnectDelaysMs)) throw badRequest('ClientSpec.reconnectDelaysMs is not an array.');
    const delays = s.reconnectDelaysMs.map((/** @type {unknown} */ d) => {
      const value = int(d, 'reconnectDelaysMs');
      if (value === undefined || value < 0) throw badRequest('ClientSpec.reconnectDelaysMs holds a bad delay.');
      return value;
    });
    config.reconnectionPolicy = reconnectionPolicy(delays, s.reconnectContinuously === true);
  }

  /** @type {{ code: number | null, message: string | null } | null} */
  let onInvocationException = null;
  if (s.onInvocationException !== undefined && s.onInvocationException !== null) {
    const hook = s.onInvocationException;
    if (typeof hook !== 'object' || Array.isArray(hook)) throw badRequest('ClientSpec.onInvocationException is not an object.');
    for (const name of Object.keys(hook)) {
      if (name !== 'code' && name !== 'message') throw notSupported('Unknown onInvocationException member \'' + name + '\'.');
    }
    onInvocationException = { code: int(hook.code, 'onInvocationException.code') ?? null, message: str(hook.message, 'onInvocationException.message') ?? null };
  }

  /** @type {string | null} */
  let logLevel = null;
  if (s.logLevel !== undefined && s.logLevel !== null) {
    const level = runtimeLevel(s.logLevel);
    if (level === undefined) throw badRequest('Unknown log level \'' + String(s.logLevel) + '\'.');
    logLevel = level === 'none' ? null : level;
  }

  return { config, fakeTime: s.fakeTime === true, logLevel, onInvocationException, transport };
}

/**
 * Pool options (§6.11.1) to the runtime's pool config members.
 * @param {unknown} options
 * @returns {{ maxConnections: number, maxIdleTime: number, minIdleConnections: number }}
 */
export function parsePoolOptions(options) {
  const o = /** @type {Record<string, any>} */ (options ?? {});
  if (typeof o !== 'object' || Array.isArray(o)) throw badRequest('pool.create options is not an object.');
  for (const name of Object.keys(o)) {
    if (!POOL_MEMBERS.has(name)) throw notSupported('This host does not know the pool option \'' + name + '\'.');
  }
  /**
   * @param {string} name
   * @param {number} fallback
   * @param {number} min
   * @returns {number}
   */
  const read = (name, fallback, min) => {
    const value = o[name];
    if (value === undefined || value === null) return fallback;
    if (typeof value !== 'number' || !Number.isInteger(value) || value < min) {
      throw badRequest('Pool option ' + name + ' is out of range: ' + String(value) + '.');
    }
    return value;
  };
  return {
    maxConnections: read('maxConnections', 10, 1),
    maxIdleTime: read('maxIdleTimeMs', 120_000, 0),
    minIdleConnections: read('minIdleConnections', 1, 0),
  };
}
