/**
 * Time source for the session's timers. Tests inject a manual clock through `ClientConfig.clock`.
 * @module
 */

/**
 * A monotonic clock and timer functions. `now()` is in milliseconds. Handles are opaque.
 *
 * @typedef {object} Clock
 * @property {() => number} now Milliseconds on a monotonic clock (`performance.now()` by default).
 * @property {(callback: () => void, ms: number) => unknown} setTimeout
 * @property {(handle: unknown) => void} clearTimeout
 * @property {(callback: () => void, ms: number) => unknown} setInterval
 * @property {(handle: unknown) => void} clearInterval
 */

/** The platform clock: `performance.now()` and the global timers. @type {Clock} */
export const systemClock = Object.freeze({
  now: () => performance.now(),
  setTimeout: (callback, ms) => globalThis.setTimeout(callback, ms),
  clearTimeout: (handle) => globalThis.clearTimeout(/** @type {any} */ (handle)),
  setInterval: (callback, ms) => globalThis.setInterval(callback, ms),
  clearInterval: (handle) => globalThis.clearInterval(/** @type {any} */ (handle)),
});

/**
 * Resolves after `ms` on `clock`.
 * @param {Clock} clock
 * @param {number} ms
 * @returns {Promise<void>}
 */
export function delay(clock, ms) {
  return new Promise((resolve) => {
    clock.setTimeout(resolve, ms);
  });
}

/** A repeating timer. @typedef {{ stop(): void }} IntervalTimer */

// Worker source for the browser ping timer: main-thread timers are throttled to once a minute (or frozen) in
// background tabs, which would let the server's idle timeout expire. Dedicated workers are not throttled that way.
const WORKER_SOURCE = 'let h;onmessage=(e)=>{clearInterval(h);if(e.data>0)h=setInterval(()=>postMessage(0),e.data);};';

/**
 * Starts a repeating timer. With the system clock in a browser it runs in a dedicated Worker when one can be created
 * (a CSP may forbid blob workers), and falls back to `setInterval`. With any other clock it uses `clock.setInterval`.
 *
 * @param {Clock} clock
 * @param {number} ms
 * @param {() => void} callback
 * @returns {IntervalTimer}
 */
export function startIntervalTimer(clock, ms, callback) {
  if (clock === systemClock) {
    const worker = tryStartWorker(ms, callback);
    if (worker) return worker;
  }
  const handle = clock.setInterval(callback, ms);
  return {
    stop() {
      clock.clearInterval(handle);
    },
  };
}

/**
 * @param {number} ms
 * @param {() => void} callback
 * @returns {IntervalTimer | null}
 */
function tryStartWorker(ms, callback) {
  const g = /** @type {any} */ (globalThis);
  // Node has no global Worker; browsers (and Deno/Bun) do.
  if (typeof g.Worker !== 'function' || typeof g.Blob !== 'function' || typeof g.URL?.createObjectURL !== 'function') {
    return null;
  }
  /** @type {string | null} */
  let url = null;
  try {
    url = URL.createObjectURL(new Blob([WORKER_SOURCE], { type: 'text/javascript' }));
    /** @type {Worker} */
    const worker = new g.Worker(url);
    worker.onmessage = () => callback();
    worker.postMessage(ms);
    const created = url;
    return {
      stop() {
        worker.terminate();
        URL.revokeObjectURL(created);
      },
    };
  } catch {
    if (url) URL.revokeObjectURL(url);
    return null;
  }
}
