/**
 * A manual clock for session tests: time moves only on {@link ManualClock#advance}.
 * @module
 */

/** A {@link import('../../../src/session/clock.js').Clock} that moves only when told to. */
export class ManualClock {
  #now = 0;
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
    this.#timers.set(id, { at: this.#now + Math.max(0, ms), callback, interval: 0 });
    return id;
  }

  /** @param {unknown} handle */
  clearTimeout(handle) {
    this.#timers.delete(/** @type {number} */ (handle));
  }

  /** @param {() => void} callback @param {number} ms @returns {number} */
  setInterval(callback, ms) {
    const id = this.#nextId++;
    this.#timers.set(id, { at: this.#now + ms, callback, interval: Math.max(1, ms) });
    return id;
  }

  /** @param {unknown} handle */
  clearInterval(handle) {
    this.#timers.delete(/** @type {number} */ (handle));
  }

  /** Timers scheduled and not yet fired or cleared. @returns {number} */
  get pendingTimers() {
    return this.#timers.size;
  }

  /**
   * Moves time forward by `ms`, firing due timers in time order.
   * @param {number} ms
   */
  advance(ms) {
    const target = this.#now + ms;
    for (;;) {
      /** @type {[number, { at: number, callback: () => void, interval: number }] | null} */
      let next = null;
      for (const entry of this.#timers) {
        if (entry[1].at <= target && (next === null || entry[1].at < next[1].at)) next = entry;
      }
      if (next === null) break;
      const [id, timer] = next;
      this.#now = timer.at;
      if (timer.interval > 0) timer.at += timer.interval;
      else this.#timers.delete(id);
      timer.callback();
    }
    this.#now = target;
  }
}
