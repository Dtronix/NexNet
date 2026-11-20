/**
 * Reconnection (J10). Mirrors C# `IReconnectionPolicy`/`DefaultReconnectionPolicy` and the client's reconnect loop.
 * @module
 */
export { DefaultReconnectionPolicy, DEFAULT_RECONNECT_DELAYS, DEFAULT_RECONNECT_REASONS, defaultShouldReconnect, policyShouldReconnect, } from './DefaultReconnectionPolicy.js';
export { ReconnectController } from './ReconnectController.js';
export { attachBrowserLifecycle } from './lifecycle.js';
export { isBrowserEnvironment, resolveReconnectionPolicy } from './environment.js';
