/**
 * Validates a {@link import('../contract/config.js').ClientConfig} and resolves its defaults (C# `ConfigBase` and
 * `ClientConfig`). Durations are milliseconds.
 * @module
 */

import { TransportKinds } from '../contract/config.js';
import { systemClock } from './clock.js';

/**
 * @typedef {object} ResolvedClientOptions
 * @property {import('../contract/config.js').TransportKind} kind The configured transport property.
 * @property {string} transportName Transport name for logs and the plaintext warning (`tcp`, `ws://`, …).
 * @property {boolean} isSecure True for TLS, WSS, HTTPS and UDS.
 * @property {import('./clock.js').Clock} clock
 * @property {import('../contract/config.js').NexusLogger | null} logger
 * @property {import('../contract/config.js').AuthenticateCallback | null} authenticate
 * @property {import('../contract/config.js').ReconnectionPolicy | null} reconnectionPolicy
 * @property {number} connectionTimeout
 * @property {number} handshakeTimeout
 * @property {number} timeout
 * @property {number | null} pingInterval Null: a third of the server's `idleTimeoutMs` (at least 1,000), else 10,000.
 * @property {number} disconnectDelay
 * @property {number} maxConcurrentConnectionInvocations
 * @property {number} maxSeparatePoolInvocations
 * @property {number} invocationQueueWarningThreshold
 * @property {number} sessionBudget
 * @property {number} maxPipes
 * @property {number} pipeWindow
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 * @property {number} progressTimeout
 * @property {import('../contract/enums.js').RemoteExceptionDetailValue} remoteExceptionDetail
 * @property {import('../contract/codec.js').SerializerOptions} serializerOptions The configured options; an unset (or 0)
 *   `maxElements` is replaced by {@link defaultMaxElements} of `maxMessageSize` (C# `ConfigBase.SerializerOptions`).
 */

/** Ping interval when neither the config nor the server sets one (C# `ClientConfig.DefaultPingInterval`). */
export const DEFAULT_PING_INTERVAL = 10_000;

/** Shortest ping interval derived from the server's advertised idle timeout (C# `ClientConfig.MinimumDerivedPingInterval`). */
export const MIN_DERIVED_PING_INTERVAL = 1_000;

/**
 * The ping interval for a server that advertised `idleTimeoutMs` (S04; C# `ClientConfig.GetEffectivePingInterval`).
 * An explicit interval is used as is. Otherwise a third of the advertised idle timeout, at least
 * {@link MIN_DERIVED_PING_INTERVAL}; {@link DEFAULT_PING_INTERVAL} when the server advertised none (null or 0).
 * @param {number | null} configured The resolved `pingInterval` option; null to derive it.
 * @param {number | null} idleTimeoutMs The advertised idle timeout (uint32), or null/0 when none.
 * @returns {number}
 */
export function effectivePingInterval(configured, idleTimeoutMs) {
  if (configured !== null) return configured;
  if (idleTimeoutMs === null || idleTimeoutMs <= 0) return DEFAULT_PING_INTERVAL;
  return Math.max(MIN_DERIVED_PING_INTERVAL, Math.floor(idleTimeoutMs / 3));
}

/** Smallest default `maxElements` (protocol specification §11). */
export const MIN_DEFAULT_MAX_ELEMENTS = 65_536;

/**
 * The reader's `maxElements` when the config leaves it unset: `max(65536, maxMessageSize / 16)` (spec §11; C#
 * `ConfigBase.SerializerOptions`).
 * @param {number} maxMessageSize
 * @returns {number}
 */
export function defaultMaxElements(maxMessageSize) {
  return Math.max(MIN_DEFAULT_MAX_ELEMENTS, Math.floor(maxMessageSize / 16));
}

/**
 * The serializer options the session's readers use: `configured`, with an unset (undefined, null or 0)
 * `maxElements` filled in from `maxMessageSize`. As in C#, 0 means "use the default", not "no limit".
 * @param {import('../contract/codec.js').SerializerOptions | null | undefined} configured
 * @param {number} maxMessageSize
 * @returns {import('../contract/codec.js').SerializerOptions}
 */
export function sessionSerializerOptions(configured, maxMessageSize) {
  const options = configured ?? {};
  if (options.maxElements !== undefined && options.maxElements !== null && options.maxElements !== 0) return options;
  return Object.freeze({ ...options, maxElements: defaultMaxElements(maxMessageSize) });
}

const KiB = 1024;
const MiB = 1024 * KiB;
const GiB = 1024 * MiB;

/**
 * @param {Record<string, unknown>} config
 * @param {string} name
 * @param {number} fallback
 * @param {number} min
 * @param {number} max
 * @returns {number}
 */
function integer(config, name, fallback, min, max) {
  const value = config[name];
  if (value === undefined || value === null) return fallback;
  if (typeof value !== 'number' || !Number.isInteger(value)) throw new TypeError(`ClientConfig.${name} must be an integer`);
  if (value < min || value > max) throw new RangeError(`ClientConfig.${name} must be between ${min} and ${max}; got ${value}`);
  return value;
}

/**
 * @param {unknown} url
 * @param {string} kind
 * @returns {URL}
 */
function parseUrl(url, kind) {
  if (url instanceof URL) return url;
  if (typeof url !== 'string') throw new TypeError(`ClientConfig.${kind}.url must be a string or URL`);
  try {
    return new URL(url);
  } catch {
    throw new TypeError(`ClientConfig.${kind}.url is not a valid URL: ${url}`);
  }
}

/**
 * Name and security of the configured transport, known before it connects (as C# `TransportName`/`IsSecureTransport`).
 * @param {import('../contract/config.js').TransportKind} kind
 * @param {any} transportConfig
 * @returns {{ name: string, isSecure: boolean }}
 */
export function describeTransport(kind, transportConfig) {
  switch (kind) {
    case 'webSocket': {
      const url = parseUrl(transportConfig?.url, kind);
      if (url.protocol !== 'ws:' && url.protocol !== 'wss:') {
        throw new TypeError(`ClientConfig.webSocket.url must be ws: or wss:; got ${url.protocol}`);
      }
      return { name: `${url.protocol}//`, isSecure: url.protocol === 'wss:' };
    }
    case 'httpSocket': {
      const url = parseUrl(transportConfig?.url, kind);
      if (url.protocol !== 'http:' && url.protocol !== 'https:') {
        throw new TypeError(`ClientConfig.httpSocket.url must be http: or https:; got ${url.protocol}`);
      }
      return { name: `${url.protocol}//`, isSecure: url.protocol === 'https:' };
    }
    case 'tcp':
      return { name: 'tcp', isSecure: false };
    case 'tcpTls':
      return { name: 'tls', isSecure: true };
    case 'uds':
      return { name: 'uds', isSecure: true };
    default:
      // Unreachable for a validated kind; any transport that does not declare itself secure is not (F05).
      return { name: String(kind), isSecure: false };
  }
}

/**
 * Validates `config` and returns it with defaults applied.
 *
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {Readonly<Partial<Record<import('../contract/config.js').TransportKind, unknown>>>} supported Transport factories of the entry point.
 * @returns {ResolvedClientOptions}
 * @throws {TypeError} No transport, several transports, an unsupported transport, or a malformed option.
 * @throws {RangeError} An option out of range, or a session budget that can't hold the fixed reservations.
 */
export function resolveClientOptions(config, supported) {
  if (typeof config !== 'object' || config === null) throw new TypeError('ClientConfig must be an object');
  const c = /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (config));

  const kinds = TransportKinds.filter((k) => c[k] !== undefined && c[k] !== null);
  if (kinds.length === 0) throw new TypeError(`ClientConfig needs exactly one transport (${TransportKinds.join(', ')}); none is set`);
  if (kinds.length > 1) throw new TypeError(`ClientConfig needs exactly one transport; several are set: ${kinds.join(', ')}`);
  const kind = kinds[0];
  if (!Object.hasOwn(supported, kind) || typeof supported[kind] !== 'function') {
    throw new TypeError(`transport '${kind}' is not supported by this entry point (supported: ${Object.keys(supported).join(', ')}); Node transports are in '@dtronix/nexnet/node'`);
  }
  const { name, isSecure } = describeTransport(kind, c[kind]);

  if (c.authenticate !== undefined && c.authenticate !== null && typeof c.authenticate !== 'function') {
    throw new TypeError('ClientConfig.authenticate must be a function');
  }
  const trusted = config.serializerOptions?.preset === 'trusted';
  const maxMessageSize = integer(c, 'maxMessageSize', trusted ? 16 * MiB : 4 * MiB, 64 * KiB, GiB);

  const options = {
    kind,
    transportName: name,
    isSecure,
    clock: config.clock ?? systemClock,
    logger: config.logger ?? null,
    authenticate: config.authenticate ?? null,
    reconnectionPolicy: config.reconnectionPolicy ?? null,
    connectionTimeout: integer(c, 'connectionTimeout', 50_000, 1, 2_147_483_647),
    handshakeTimeout: integer(c, 'handshakeTimeout', 15_000, 50, 60_000),
    timeout: integer(c, 'timeout', 30_000, 50, 300_000),
    pingInterval: c.pingInterval === undefined || c.pingInterval === null ? null : integer(c, 'pingInterval', 0, 1, 2_147_483_647),
    disconnectDelay: integer(c, 'disconnectDelay', 200, -2_147_483_648, 10_000),
    maxConcurrentConnectionInvocations: integer(c, 'maxConcurrentConnectionInvocations', trusted ? 32 : 2, 1, 1000),
    maxSeparatePoolInvocations: integer(c, 'maxSeparatePoolInvocations', trusted ? 16 : 4, 1, 1000),
    invocationQueueWarningThreshold: integer(c, 'invocationQueueWarningThreshold', 5000, 0, 600_000),
    sessionBudget: integer(c, 'sessionBudget', trusted ? 64 * MiB : 16 * MiB, 64 * KiB, GiB),
    maxPipes: integer(c, 'maxPipes', 32, 1, 255),
    pipeWindow: integer(c, 'pipeWindow', trusted ? MiB : 256 * KiB, 4 * KiB, GiB),
    maxMessageSize,
    invocationCredits: integer(c, 'invocationCredits', trusted ? 128 : 32, 1, 10_000),
    progressTimeout: integer(c, 'progressTimeout', 30_000, 50, 600_000),
    remoteExceptionDetail: config.remoteExceptionDetail ?? 0,
    serializerOptions: sessionSerializerOptions(config.serializerOptions, maxMessageSize),
  };

  // C# ConfigBase.ValidateFlowControl: the budget must hold every pipe window, one large message and the invocation queue.
  const required = options.maxPipes * options.pipeWindow + options.maxMessageSize + options.invocationCredits * 64 * KiB;
  if (required > options.sessionBudget) {
    throw new RangeError(`ClientConfig.sessionBudget (${options.sessionBudget} bytes) is smaller than maxPipes x pipeWindow + `
      + `maxMessageSize + invocationCredits x 64KB (${required} bytes)`);
  }
  return options;
}
