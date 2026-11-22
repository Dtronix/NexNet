/**
 * `ClientSpec` (§6.4.1) and pool options (§6.11.1) to runtime configuration.
 * @module
 */

import { badRequest, notSupported } from './errors.js';
import { reasonValue, remoteExceptionDetailValue, runtimeLevel } from './names.js';

/** The members of `ClientSpec` this host knows; any other member is refused (§6.4.1). */
const SPEC_MEMBERS = new Set([
  'transport', 'address', 'tlsServerName', 'caPem', 'clientCertificatePem', 'clientKeyPem', 'pinSha256',
  'insecureSkipVerify', 'connectionTimeoutMs', 'timeoutMs', 'handshakeTimeoutMs', 'pingIntervalMs', 'maxMessageSize',
  'invocationCredits', 'maxConcurrentConnectionInvocations', 'remoteExceptionDetail', 'onInvocationException',
  'reconnectDelaysMs', 'reconnectContinuously', 'defaultReconnection', 'authToken', 'authorizationHeader', 'fakeTime', 'logLevel',
  'reconnectReasons', 'authenticate', 'queryToken', 'maxSeparatePoolInvocations', 'invocationQueueWarningThresholdMs',
  'sessionBudget', 'maxPipes', 'pipeWindow', 'progressTimeoutMs', 'metrics',
]);

const AUTHENTICATE_MEMBERS = new Set(['token', 'appendCallNumber', 'delayMs', 'hang', 'throwMessage']);

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
 * @property {boolean} metrics True to record the client's metrics (`ClientSpec.metrics` `Native`).
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
 * forever when continuous, else stop. With `reasons`, it reconnects only for those disconnect reasons
 * (`ReconnectReasons`); without, the runtime's default reasons apply.
 * @param {readonly number[]} delays
 * @param {boolean} continuously
 * @param {ReadonlySet<number>} [reasons] `DisconnectReason` values.
 * @returns {{ reconnectDelay: (attempt: number) => number | null, shouldReconnect?: (reason: number) => boolean }}
 */
export function reconnectionPolicy(delays, continuously, reasons) {
  const copy = [...delays];
  /** @type {{ reconnectDelay: (attempt: number) => number | null, shouldReconnect?: (reason: number) => boolean }} */
  const policy = {
    reconnectDelay(attempt) {
      if (attempt < copy.length) return copy[attempt];
      return continuously && copy.length > 0 ? copy[copy.length - 1] : null;
    },
  };
  if (reasons !== undefined) {
    const set = new Set(reasons);
    policy.shouldReconnect = (reason) => set.has(reason);
  }
  return policy;
}

/**
 * `ClientSpec.reconnectReasons` (`DisconnectReason` names) as values; undefined when absent.
 * @param {unknown} value
 * @returns {Set<number> | undefined}
 */
function reconnectReasons(value) {
  if (value === undefined || value === null) return undefined;
  if (!Array.isArray(value)) throw badRequest('ClientSpec.reconnectReasons is not an array.');
  return new Set(value.map((/** @type {unknown} */ name) => {
    const reason = typeof name === 'string' ? reasonValue(name) : undefined;
    if (reason === undefined) throw badRequest('Unknown DisconnectReason \'' + String(name) + '\'.');
    return reason;
  }));
}

/**
 * The scripted authenticate callback of `ClientSpec.authenticate`: each call counts, waits `delayMs` (real time),
 * then hangs until the client aborts it, throws `throwMessage`, or returns `token` (with the call number appended
 * when `appendCallNumber`; null for no token).
 * @param {unknown} value
 * @returns {(options: { signal: AbortSignal }) => Promise<Uint8Array | null>}
 */
export function authenticateScript(value) {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw badRequest('ClientSpec.authenticate is not an object.');
  const a = /** @type {Record<string, any>} */ (value);
  for (const name of Object.keys(a)) {
    if (!AUTHENTICATE_MEMBERS.has(name)) throw notSupported('Unknown authenticate member \'' + name + '\'.');
  }
  const token = str(a.token, 'authenticate.token');
  const delayMs = int(a.delayMs, 'authenticate.delayMs') ?? 0;
  const throwMessage = str(a.throwMessage, 'authenticate.throwMessage');
  const hang = a.hang === true;
  const appendCallNumber = a.appendCallNumber === true;
  let calls = 0;
  return async ({ signal }) => {
    const call = ++calls;
    if (delayMs > 0) await wait(delayMs, signal);
    if (hang) await wait(null, signal);
    if (throwMessage !== undefined) throw new Error(throwMessage);
    if (token === undefined) return null;
    return new TextEncoder().encode(appendCallNumber ? token + String(call) : token);
  };
}

/**
 * Waits `ms` milliseconds (forever when null), rejecting with the signal's reason when it aborts.
 * @param {number | null} ms
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
function wait(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    /** @type {ReturnType<typeof setTimeout> | undefined} */
    let timer;
    const onAbort = () => {
      if (timer !== undefined) clearTimeout(timer);
      reject(signal.reason);
    };
    signal.addEventListener('abort', onAbort, { once: true });
    if (ms !== null) {
      timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
    }
  });
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
    if (s.insecureSkipVerify === true) throw notSupported('A browser cannot skip certificate verification; its TLS stack decides trust.');
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
  if (s.queryToken !== undefined && s.queryToken !== null) {
    const queryToken = s.queryToken;
    if (typeof queryToken !== 'object' || Array.isArray(queryToken)) throw badRequest('ClientSpec.queryToken is not an object.');
    for (const name of Object.keys(queryToken)) {
      if (name !== 'name' && name !== 'value') throw notSupported('Unknown queryToken member \'' + name + '\'.');
    }
    const name = str(queryToken.name, 'queryToken.name');
    if (!name) throw badRequest('ClientSpec.queryToken.name is required.');
    const option = { name, value: str(queryToken.value, 'queryToken.value') ?? '' };
    const upgrade = config.webSocket ?? config.httpSocket;
    if (!upgrade) throw notSupported('A query-string token needs an HTTP transport, not ' + transport + '.');
    upgrade.queryToken = option;
  }

  const token = str(s.authToken, 'authToken');
  if (token !== undefined) {
    const bytes = new TextEncoder().encode(token);
    config.authenticate = () => bytes;
  }
  if (s.authenticate !== undefined && s.authenticate !== null) {
    if (token !== undefined) throw badRequest('ClientSpec.authToken and authenticate are exclusive.');
    config.authenticate = authenticateScript(s.authenticate);
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
    ['maxSeparatePoolInvocations', 'maxSeparatePoolInvocations'],
    ['invocationQueueWarningThresholdMs', 'invocationQueueWarningThreshold'],
    ['sessionBudget', 'sessionBudget'],
    ['maxPipes', 'maxPipes'],
    ['pipeWindow', 'pipeWindow'],
    ['progressTimeoutMs', 'progressTimeout'],
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
    config.reconnectionPolicy = reconnectionPolicy(delays, s.reconnectContinuously === true, reconnectReasons(s.reconnectReasons));
  } else if (s.reconnectReasons !== undefined && s.reconnectReasons !== null) {
    throw badRequest('ClientSpec.reconnectReasons needs reconnectDelaysMs.');
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

  const metrics = str(s.metrics, 'metrics');
  if (metrics !== undefined && metrics !== 'Native') {
    if (metrics === 'Micrometer') throw notSupported('Micrometer metrics are a Java module; the JavaScript client records Native metrics.');
    throw badRequest('Unknown MetricsRecording \'' + metrics + '\'.');
  }

  return { config, fakeTime: s.fakeTime === true, logLevel, onInvocationException, transport, metrics: metrics !== undefined };
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
