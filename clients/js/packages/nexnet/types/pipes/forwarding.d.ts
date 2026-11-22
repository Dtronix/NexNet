/**
 * Byte-level relays between pipes (C# `NexusPipeForwardingExtensions`), for relay and gateway clients.
 * @module
 */
/**
 * Copies what `source` receives to `destination` until the source's input completes, then completes the
 * destination's writing direction. Reads one buffer at a time and sends only what the destination's credit allows
 * before consuming it, so the source's credit is granted back only as the destination drains.
 *
 * @param {import('./PipeCore.js').PipeCore} source
 * @param {import('./PipeCore.js').PipeCore} destination
 * @param {AbortSignal} [signal] Stops the forward; neither pipe is completed.
 * @param {{ halfClosed: boolean }} [state] Set before the destination is half-closed, so a bridge watching the
 *   destination's `completed` knows the completion is the normal end.
 * @returns {Promise<void>} Resolves when the source's input completed or the destination's output was completed.
 */
export declare function forwardPipe(source: import('./PipeCore.js').PipeCore, destination: import('./PipeCore.js').PipeCore, signal?: AbortSignal, state?: {
    halfClosed: boolean;
}): Promise<void>;
export type Settled = {
    key: 'ab' | 'ba' | 'a' | 'b';
    ok: boolean;
    error?: unknown;
};
/**
 * Forwards `a` to `b` and `b` to `a` (C# `BridgeAsync`); see `NexusDuplexPipe.bridge`.
 *
 * @param {{ complete(): Promise<void>, completed: Promise<void> }} a
 * @param {{ complete(): Promise<void>, completed: Promise<void> }} b
 * @param {import('./PipeCore.js').PipeCore} coreA
 * @param {import('./PipeCore.js').PipeCore} coreB
 * @param {AbortSignal} [signal]
 * @returns {Promise<void>}
 */
export declare function bridgePipes(a: {
    complete(): Promise<void>;
    completed: Promise<void>;
}, b: {
    complete(): Promise<void>;
    completed: Promise<void>;
}, coreA: import('./PipeCore.js').PipeCore, coreB: import('./PipeCore.js').PipeCore, signal?: AbortSignal): Promise<void>;
