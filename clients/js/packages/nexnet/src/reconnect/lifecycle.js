/**
 * Browser page-lifecycle triggers for reconnection: when the page becomes visible, the network comes back or a frozen
 * page resumes, a pending reconnect delay ends at once (timers of hidden or frozen pages are throttled or stopped).
 * Uses only DOM globals; a no-op where there is no `document`/`window`.
 * @module
 */

import { countOps } from '../diagnostics/counters.js';

/**
 * Event targets to listen on. Default: the global `document` and `window`.
 * @typedef {object} LifecycleTargets
 * @property {EventTarget & { visibilityState?: string } | null} [document] `visibilitychange`, `freeze`, `resume`.
 * @property {EventTarget | null} [window] `online`.
 */

/**
 * @typedef {object} RetryTarget
 * @property {() => boolean} retryNow
 */

/**
 * Listens for the lifecycle events and calls `controller.retryNow()` on each. Returns a function that removes the
 * listeners.
 * @param {RetryTarget} controller
 * @param {LifecycleTargets} [targets]
 * @returns {() => void}
 */
export function attachBrowserLifecycle(controller, targets) {
  const g = /** @type {any} */ (globalThis);
  const doc = targets?.document !== undefined ? targets.document : (g.document ?? null);
  const win = targets?.window !== undefined ? targets.window : (typeof g.addEventListener === 'function' && g.document ? g : null);

  /** @type {Array<[EventTarget, string, () => void]>} */
  const listeners = [];
  const retry = () => {
    controller.retryNow();
  };
  if (doc) {
    listeners.push([doc, 'visibilitychange', () => {
      if (doc.visibilityState === undefined || doc.visibilityState === 'visible') retry();
    }]);
    listeners.push([doc, 'resume', retry]);
    // A page is frozen with its timers; nothing to do on freeze but note it, the resume retries.
    listeners.push([doc, 'freeze', () => {}]);
  }
  if (win) listeners.push([win, 'online', retry]);

  for (const [target, type, fn] of listeners) target.addEventListener(type, fn);
  countOps('subscriptions', listeners.length);
  let attached = true;
  return () => {
    if (!attached) return;
    attached = false;
    for (const [target, type, fn] of listeners) target.removeEventListener(type, fn);
    countOps('subscriptions', -listeners.length);
  };
}
