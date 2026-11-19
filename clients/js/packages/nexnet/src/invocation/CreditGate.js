/**
 * Counting gate for outbound invocation credits (protocol spec §8, C# `CreditGate`). Waiters are served in FIFO
 * order; a credit released while calls wait goes to the oldest one, so a credit that is available is never one a
 * waiter is owed.
 * @module
 */

import { cancellationError } from '../contract/errors.js';
import { countInvocation } from './diagnostics.js';

/**
 * @typedef {object} Waiter
 * @property {(acquired: boolean) => void} resolve
 * @property {(error: unknown) => void} reject
 * @property {AbortSignal | undefined} signal
 * @property {(() => void) | null} onAbort
 */

export class CreditGate {
  #count = 0;
  #closed = false;
  /** @type {Waiter[]} */
  #waiters = [];

  /** Credits available now. @returns {number} */
  get available() {
    return this.#count;
  }

  /** Calls waiting for a credit. @returns {number} */
  get waiting() {
    return this.#waiters.length;
  }

  /** True once {@link CreditGate#close} ran. @returns {boolean} */
  get closed() {
    return this.#closed;
  }

  /**
   * Takes a credit if one is available, without waiting.
   * @returns {boolean}
   */
  tryTake() {
    if (this.#count <= 0) return false;
    this.#count--;
    return true;
  }

  /**
   * Takes a credit, waiting in FIFO order. Resolves true once taken, false when the gate closes first (the session
   * ended). Rejects with the cancellation error of `signal` when it aborts first; the wait then holds no place.
   * @param {AbortSignal} [signal]
   * @returns {Promise<boolean>}
   */
  wait(signal) {
    if (this.tryTake()) return Promise.resolve(true);
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    if (this.#closed) return Promise.resolve(false);
    return new Promise((resolve, reject) => {
      /** @type {Waiter} */
      const waiter = { resolve, reject, signal, onAbort: null };
      if (signal) {
        waiter.onAbort = () => {
          const index = this.#waiters.indexOf(waiter);
          if (index < 0) return;
          this.#waiters.splice(index, 1);
          countInvocation('creditWaiters', -1);
          reject(cancellationError(signal));
        };
        signal.addEventListener('abort', waiter.onAbort, { once: true });
      }
      this.#waiters.push(waiter);
      countInvocation('creditWaiters', 1);
    });
  }

  /**
   * Releases credits: waiters are served first, in FIFO order; the rest become available.
   * @param {number} [credits]
   * @returns {void}
   */
  release(credits = 1) {
    while (credits > 0 && this.#waiters.length > 0) {
      this.#settle(/** @type {Waiter} */ (this.#waiters.shift()), true);
      credits--;
    }
    if (credits > 0) this.#count += credits;
  }

  /**
   * Ends every wait with false; later waits that would park resolve false at once. Credits still available can be
   * taken.
   * @returns {void}
   */
  close() {
    this.#closed = true;
    const waiters = this.#waiters;
    this.#waiters = [];
    for (const waiter of waiters) this.#settle(waiter, false);
  }

  /** @param {Waiter} waiter @param {boolean} acquired */
  #settle(waiter, acquired) {
    if (waiter.onAbort) waiter.signal?.removeEventListener('abort', waiter.onAbort);
    countInvocation('creditWaiters', -1);
    waiter.resolve(acquired);
  }
}
