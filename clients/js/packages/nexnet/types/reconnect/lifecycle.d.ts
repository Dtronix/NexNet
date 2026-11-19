/**
 * Browser page-lifecycle triggers for reconnection: when the page becomes visible, the network comes back or a frozen
 * page resumes, a pending reconnect delay ends at once (timers of hidden or frozen pages are throttled or stopped).
 * Uses only DOM globals; a no-op where there is no `document`/`window`.
 * @module
 */
export type LifecycleTargets = {
    /**
     * `visibilitychange`, `freeze`, `resume`.
     */
    document?: (EventTarget & {
        visibilityState?: string;
    }) | null;
    /**
     * `online`.
     */
    window?: EventTarget | null;
};
export type RetryTarget = {
    retryNow: () => boolean;
};
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
export declare function attachBrowserLifecycle(controller: RetryTarget, targets?: LifecycleTargets): () => void;
