/**
 * Clocks of the clients a host creates: the system clock, a manual clock for `spec.fakeTime`, and a wrapper that counts
 * the timers a client has scheduled (the `timers` of the leak report, §5.1).
 * @module
 */

/**
 * The runtime's `Clock` shape (`@dtronix/nexnet` `ClientConfig.clock`).
 * @typedef {object} Clock
 * @property {() => number} now
 * @property {(callback: () => void, ms: number) => unknown} setTimeout
 * @property {(handle: unknown) => void} clearTimeout
 * @property {(callback: () => void, ms: number) => unknown} setInterval
 * @property {(handle: unknown) => void} clearInterval
 */

/** @type {Clock} */
export const systemClock = Object.freeze({
  now: () => performance.now(),
  setTimeout: (/** @type {() => void} */ callback, /** @type {number} */ ms) => globalThis.setTimeout(callback, ms),
  clearTimeout: (/** @type {any} */ handle) => globalThis.clearTimeout(handle),
  setInterval: (/** @type {() => void} */ callback, /** @type {number} */ ms) => globalThis.setInterval(callback, ms),
  clearInterval: (/** @type {any} */ handle) => globalThis.clearInterval(handle),
});

/** A clock that moves only on {@link FakeClock#advance} (C# `FakeTimeProvider`). */
export class FakeClock {
  // Not 0: some code reads a zero time as "never".
  #now = 1_000_000;
  #nextId = 1;
  /** @type {Map<number, { at: number, callback: () => void, interval: number }>} */
  #timers = new Map();

  /** @returns {number} */
  now() {
    return this.#now;
  }

  /** @param {() => void} callback @param {number} ms @returns {number} */
  setTimeout(callback, ms) {
    const id = this.#nextId++;
    this.#timers.set(id, { at: this.#now + Math.max(0, ms || 0), callback, interval: 0 });
    return id;
  }

  /** @param {unknown} handle */
  clearTimeout(handle) {
    this.#timers.delete(/** @type {number} */ (handle));
  }

  /** @param {() => void} callback @param {number} ms @returns {number} */
  setInterval(callback, ms) {
    const id = this.#nextId++;
    const interval = Math.max(1, ms || 0);
    this.#timers.set(id, { at: this.#now + interval, callback, interval });
    return id;
  }

  /** @param {unknown} handle */
  clearInterval(handle) {
    this.#timers.delete(/** @type {number} */ (handle));
  }

  /**
   * Moves time forward, running due timers in time order (a timer scheduled by a callback runs too when it is due).
   * @param {number} ms
   */
  advance(ms) {
    const target = this.#now + Math.max(0, ms);
    for (;;) {
      /** @type {[number, { at: number, callback: () => void, interval: number }] | null} */
      let next = null;
      for (const entry of this.#timers) {
        if (entry[1].at <= target && (next === null || entry[1].at < next[1].at)) next = entry;
      }
      if (next === null) break;
      const [id, timer] = next;
      this.#now = Math.max(this.#now, timer.at);
      if (timer.interval > 0) timer.at += timer.interval;
      else this.#timers.delete(id);
      try {
        timer.callback();
      } catch {
        // A timer callback's failure is the runtime's; keep time moving.
      }
    }
    this.#now = target;
  }
}

/** Wraps a clock and counts the timeouts and intervals scheduled through it and not yet fired or cleared. */
export class TrackingClock {
  /** @type {Clock} */
  #inner;
  /** @type {Set<{ handle: unknown, interval: boolean }>} */
  #active = new Set();

  /** @param {Clock} inner */
  constructor(inner) {
    this.#inner = inner;
  }

  /** Timers scheduled and not yet fired (timeouts) or cleared. @returns {number} */
  get activeTimers() {
    return this.#active.size;
  }

  /** @returns {number} */
  now() {
    return this.#inner.now();
  }

  /** @param {() => void} callback @param {number} ms @returns {unknown} */
  setTimeout(callback, ms) {
    /** @type {{ handle: unknown, interval: boolean }} */
    const token = { handle: null, interval: false };
    this.#active.add(token);
    token.handle = this.#inner.setTimeout(() => {
      this.#active.delete(token);
      callback();
    }, ms);
    return token;
  }

  /** @param {unknown} token */
  clearTimeout(token) {
    this.#clear(token);
  }

  /** @param {() => void} callback @param {number} ms @returns {unknown} */
  setInterval(callback, ms) {
    /** @type {{ handle: unknown, interval: boolean }} */
    const token = { handle: null, interval: true };
    this.#active.add(token);
    token.handle = this.#inner.setInterval(callback, ms);
    return token;
  }

  /** @param {unknown} token */
  clearInterval(token) {
    this.#clear(token);
  }

  /** @param {unknown} token */
  #clear(token) {
    const t = /** @type {{ handle: unknown, interval: boolean }} */ (token);
    if (!t || !this.#active.delete(t)) return;
    if (t.interval) this.#inner.clearInterval(t.handle);
    else this.#inner.clearTimeout(t.handle);
  }
}
