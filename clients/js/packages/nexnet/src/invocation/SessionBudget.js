/**
 * A session's receive memory budget (protocol spec §9, C# `SessionBudget`). Counts reservations, not allocations:
 * the invocation queue, every large message being received and every pipe window are reserved from it.
 * @module
 */

export class SessionBudget {
  #limit;
  #reserved = 0;
  /** @type {Array<{ bytes: number, resolve: (ok: boolean) => void, done: boolean }>} */
  #waiters = [];

  /** @param {number} limit Total bytes that may be reserved at once. */
  constructor(limit) {
    this.#limit = limit;
  }

  /** @returns {number} */
  get limit() {
    return this.#limit;
  }

  /** Bytes currently reserved. @returns {number} */
  get reserved() {
    return this.#reserved;
  }

  /**
   * Reserves `bytes` if the budget can hold them, all or nothing.
   * @param {number} bytes
   * @returns {boolean}
   */
  tryReserve(bytes) {
    if (this.#reserved + bytes > this.#limit) return false;
    this.#reserved += bytes;
    return true;
  }

  /**
   * Reserves `bytes`, all or nothing, waiting in FIFO order behind earlier waiters until the budget has room.
   * Resolves false when `signal` aborts first or the request can never fit.
   * @param {number} bytes
   * @param {AbortSignal} [signal]
   * @returns {Promise<boolean>}
   */
  reserve(bytes, signal) {
    if (this.#waiters.length === 0 && this.tryReserve(bytes)) return Promise.resolve(true);
    if (bytes > this.#limit || signal?.aborted) return Promise.resolve(false);
    return new Promise((resolve) => {
      const waiter = { bytes, resolve, done: false };
      const onAbort = () => {
        if (waiter.done) return;
        waiter.done = true;
        resolve(false);
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      waiter.resolve = (ok) => {
        signal?.removeEventListener('abort', onAbort);
        resolve(ok);
      };
      this.#waiters.push(waiter);
    });
  }

  /**
   * Returns `bytes` reserved earlier, then serves waiting reservations that now fit.
   * @param {number} bytes
   * @returns {void}
   */
  release(bytes) {
    this.#reserved = Math.max(0, this.#reserved - bytes);
    while (this.#waiters.length > 0) {
      const next = this.#waiters[0];
      if (next.done) {
        this.#waiters.shift();
        continue;
      }
      if (!this.tryReserve(next.bytes)) break;
      this.#waiters.shift();
      next.done = true;
      next.resolve(true);
    }
  }

  /** Fails every waiting reservation (the session ended). @returns {void} */
  close() {
    const waiters = this.#waiters;
    this.#waiters = [];
    for (const waiter of waiters) {
      if (waiter.done) continue;
      waiter.done = true;
      waiter.resolve(false);
    }
  }
}
