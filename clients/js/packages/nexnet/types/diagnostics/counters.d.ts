/**
 * The leak-audit counters (H05): `counters()` sums the process-wide counters of every runtime layer. After every
 * client has disconnected and every pool is disposed they must all be zero.
 * @module
 */
import { getPipeCounters } from '../pipes/diagnostics.js';
export type OpsCounters = {
    /**
     * Reconnect delays and pool health-check timers running.
     */
    timers: number;
    /**
     * Event listeners the runtime holds on clients and platform objects (reconnection,
     * metrics, browser lifecycle).
     */
    subscriptions: number;
    /**
     * Clients owned by a pool (rented or idle).
     */
    pooledClients: number;
};
export type CounterSource = () => {
    pipes?: number;
    timers?: number;
    subscriptions?: number;
};
/**
 * Adjusts an ops counter. Internal to the runtime.
 * @param {keyof OpsCounters} name
 * @param {number} delta
 * @returns {void}
 */
export declare function countOps(name: keyof OpsCounters, delta: number): void;
/** @returns {OpsCounters} */
export declare function getOpsCounters(): OpsCounters;
/**
 * Registers the counters of another layer (for example open pipes). Returns a function that removes it.
 * @param {CounterSource} source
 * @returns {() => void}
 */
export declare function registerCounterSource(source: CounterSource): () => void;
export type NexusCounters = {
    /**
     * Sessions open (`openSessions`).
     */
    sessions: number;
    /**
     * Pipes open (`openPipes` of the pipe layer).
     */
    pipes: number;
    /**
     * Timers running: session timers, reconnect delays, pool health checks.
     */
    timers: number;
    /**
     * Outbound calls not yet completed plus server-to-client invocations queued or
     * running.
     */
    pendingInvocations: number;
    /**
     * Event listeners the runtime holds.
     */
    subscriptions: number;
    /**
     *   The per-layer counters the totals are made of.
     */
    detail: {
        session: import('../session/diagnostics.js').SessionCounters;
        invocation: import('../invocation/diagnostics.js').InvocationCounters;
        pipe: ReturnType<typeof getPipeCounters>;
        ops: OpsCounters;
    };
};
/**
 * @typedef {object} NexusCounters
 * @property {number} sessions Sessions open (`openSessions`).
 * @property {number} pipes Pipes open (`openPipes` of the pipe layer).
 * @property {number} timers Timers running: session timers, reconnect delays, pool health checks.
 * @property {number} pendingInvocations Outbound calls not yet completed plus server-to-client invocations queued or
 *   running.
 * @property {number} subscriptions Event listeners the runtime holds.
 * @property {{ session: import('../session/diagnostics.js').SessionCounters, invocation: import('../invocation/diagnostics.js').InvocationCounters, pipe: ReturnType<typeof getPipeCounters>, ops: OpsCounters }} detail
 *   The per-layer counters the totals are made of.
 */
/**
 * The process-wide leak-audit counters. Every field, the `detail` ones included, is zero once all clients have
 * disconnected and all pools are disposed.
 * @returns {NexusCounters}
 */
export declare function counters(): NexusCounters;
/**
 * True when every counter, the `detail` ones included, is zero.
 * @param {NexusCounters} [c] Default: {@link counters}().
 * @returns {boolean}
 */
export declare function isIdle(c?: NexusCounters): boolean;
