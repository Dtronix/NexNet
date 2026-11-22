/**
 * Platform detection for reconnection defaults. Never imports `node:*`.
 * @module
 */

import { DefaultReconnectionPolicy } from './DefaultReconnectionPolicy.js';

/**
 * True in a browser page or worker-less browser-like environment: a global `document` and `navigator` exist.
 * @returns {boolean}
 */
export function isBrowserEnvironment() {
  const g = /** @type {any} */ (globalThis);
  return typeof g.document === 'object' && g.document !== null && typeof g.navigator === 'object' && g.navigator !== null;
}

/**
 * The policy a client uses: `config.reconnectionPolicy` when the property is present (null turns reconnection off),
 * otherwise the default policy in a browser and none in Node (`ClientConfig.reconnectionPolicy`).
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {boolean} [browser] Default {@link isBrowserEnvironment}().
 * @returns {import('../contract/config.js').ReconnectionPolicy | null}
 */
export function resolveReconnectionPolicy(config, browser = isBrowserEnvironment()) {
  if (config.reconnectionPolicy !== undefined) return config.reconnectionPolicy;
  return browser ? new DefaultReconnectionPolicy() : null;
}
