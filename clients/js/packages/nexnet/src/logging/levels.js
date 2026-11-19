/**
 * Log levels and behaviors. Mirror C# `NexusLogLevel` and `NexusLogBehaviors`.
 * @module
 */

/**
 * Numeric order of the contract's level names (C# `NexusLogLevel` values). `none` is only a minimum level: it
 * turns a logger off.
 */
export const NexusLogLevelOrder = Object.freeze({
  trace: 0,
  debug: 1,
  information: 2,
  warning: 3,
  error: 4,
  critical: 5,
  none: 6,
});

/**
 * A level name, or `'none'` as a minimum level.
 * @typedef {import('../contract/config.js').NexusLogLevel | 'none'} NexusMinLogLevel
 */

/** Three-letter level tags written by the formatting loggers, as in C# `CoreLogger`. */
export const NexusLogLevelTag = Object.freeze({
  trace: 'TRA',
  debug: 'DEB',
  information: 'INF',
  warning: 'WAR',
  error: 'ERR',
  critical: 'CRI',
  none: 'NON',
});

/**
 * Logger behavior flags. Mirrors C# `NexusLogBehaviors`.
 */
export const NexusLogBehaviors = Object.freeze({
  Default: 0,
  /** Log proxy (client-to-server) invocations as `information` instead of `debug`. */
  ProxyInvocationsLogAsInfo: 1 << 0,
  /** Log local (server-to-client) invocations as `information` instead of `debug`. */
  LocalInvocationsLogAsInfo: 1 << 1,
  /** Log the data sent and received. Debugging only. Authentication tokens are never logged. */
  LogTransportData: 1 << 2,
});

/**
 * @param {unknown} level
 * @returns {number} The order of `level`; throws on an unknown name.
 */
export function levelOrder(level) {
  const order = /** @type {Record<string, number>} */ (NexusLogLevelOrder)[/** @type {string} */ (level)];
  if (order === undefined) throw new TypeError(`unknown log level: ${String(level)}`);
  return order;
}
