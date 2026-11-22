/**
 * Platform detection for reconnection defaults. Never imports `node:*`.
 * @module
 */
/**
 * True in a browser page or worker-less browser-like environment: a global `document` and `navigator` exist.
 * @returns {boolean}
 */
export declare function isBrowserEnvironment(): boolean;
/**
 * The policy a client uses: `config.reconnectionPolicy` when the property is present (null turns reconnection off),
 * otherwise the default policy in a browser and none in Node (`ClientConfig.reconnectionPolicy`).
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {boolean} [browser] Default {@link isBrowserEnvironment}().
 * @returns {import('../contract/config.js').ReconnectionPolicy | null}
 */
export declare function resolveReconnectionPolicy(config: import('../contract/config.js').ClientConfig, browser?: boolean): import('../contract/config.js').ReconnectionPolicy | null;
