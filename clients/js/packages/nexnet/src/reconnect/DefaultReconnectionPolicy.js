/**
 * The default reconnection policy. Mirrors C# `DefaultReconnectionPolicy` and `IReconnectionPolicy.ShouldReconnect`.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';

/** @typedef {import('../contract/config.js').ReconnectionPolicy} ReconnectionPolicy */

/** Delays of the default policy, in milliseconds: 0, 2, 10 and 30 s (C# `DefaultReconnectionPolicy`). */
export const DEFAULT_RECONNECT_DELAYS = Object.freeze([0, 2_000, 10_000, 30_000]);

/** Disconnect reasons the default policy reconnects for: SocketError, Timeout and ServerRestarting. */
export const DEFAULT_RECONNECT_REASONS = /** @type {readonly number[]} */ (Object.freeze([
  DisconnectReason.SocketError,
  DisconnectReason.Timeout,
  DisconnectReason.ServerRestarting,
]));

/**
 * The default `shouldReconnect` of every policy (C# `IReconnectionPolicy.ShouldReconnect`): true for SocketError,
 * Timeout and ServerRestarting.
 * @param {import('../contract/enums.js').DisconnectReasonValue} reason
 * @returns {boolean}
 */
export function defaultShouldReconnect(reason) {
  return DEFAULT_RECONNECT_REASONS.includes(reason);
}

/**
 * `policy.shouldReconnect(reason)`, or {@link defaultShouldReconnect} when the policy has none.
 * @param {import('../contract/config.js').ReconnectionPolicy} policy
 * @param {import('../contract/enums.js').DisconnectReasonValue} reason
 * @returns {boolean}
 */
export function policyShouldReconnect(policy, reason) {
  return typeof policy.shouldReconnect === 'function' ? policy.shouldReconnect(reason) === true : defaultShouldReconnect(reason);
}

/**
 * Retries after 0, 2, 10 and 30 seconds, then every 30 seconds (`continuousRetry`). Reconnects only for the reasons in
 * {@link DefaultReconnectionPolicy#reconnectReasons}.
 *
 * @implements {ReconnectionPolicy}
 */
export class DefaultReconnectionPolicy {
  /** Delays in milliseconds, one per attempt. @readonly @type {readonly number[]} */
  delays;
  /** When true the last delay repeats forever; otherwise the policy stops after the last one. @readonly @type {boolean} */
  continuousRetry;
  /**
   * Disconnect reasons the client reconnects for. Add `ServerShutdown` to reconnect after the server stops and starts
   * again; `Graceful` and `Authentication` are best left out.
   * @readonly @type {ReadonlySet<number>}
   */
  reconnectReasons;

  /**
   * @param {object} [options]
   * @param {readonly number[]} [options.delays] Default {@link DEFAULT_RECONNECT_DELAYS}.
   * @param {boolean} [options.continuousRetry] Default true.
   * @param {Iterable<number>} [options.reconnectReasons] Default {@link DEFAULT_RECONNECT_REASONS}.
   */
  constructor({ delays = DEFAULT_RECONNECT_DELAYS, continuousRetry = true, reconnectReasons = DEFAULT_RECONNECT_REASONS } = {}) {
    if (!Array.isArray(delays) || delays.length === 0) throw new TypeError('delays must be a non-empty array');
    for (const d of delays) {
      if (typeof d !== 'number' || !Number.isFinite(d) || d < 0) throw new RangeError('every delay must be a finite number >= 0');
    }
    this.delays = Object.freeze([...delays]);
    this.continuousRetry = continuousRetry;
    this.reconnectReasons = new Set(reconnectReasons);
  }

  /**
   * @param {number} attempt 0-based.
   * @returns {number | null}
   */
  reconnectDelay(attempt) {
    if (attempt < this.delays.length) return this.delays[attempt];
    return this.continuousRetry ? this.delays[this.delays.length - 1] : null;
  }

  /**
   * @param {import('../contract/enums.js').DisconnectReasonValue} reason
   * @returns {boolean}
   */
  shouldReconnect(reason) {
    return this.reconnectReasons.has(reason);
  }
}
