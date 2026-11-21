/**
 * The leak-audit counters: `counters()` sums the process-wide counters of every runtime layer. After every
 * client has disconnected and every pool is disposed they must all be zero.
 * @module
 */

import { getSessionCounters } from '../session/diagnostics.js';
import { getInvocationCounters } from '../invocation/diagnostics.js';
import { getPipeCounters } from '../pipes/diagnostics.js';

/**
 * Counters of the client features: reconnection, pool, metrics and browser lifecycle.
 * @typedef {object} OpsCounters
 * @property {number} timers Reconnect delays and pool health-check timers running.
 * @property {number} subscriptions Event listeners the runtime holds on clients and platform objects (reconnection,
 *   metrics, browser lifecycle).
 * @property {number} pooledClients Clients owned by a pool (rented or idle).
 */

const ops = { timers: 0, subscriptions: 0, pooledClients: 0 };

/**
 * A counter source of another layer that registers itself.
 * @typedef {() => { pipes?: number, timers?: number, subscriptions?: number }} CounterSource
 */

/** @type {Set<CounterSource>} */
const sources = new Set();

/**
 * Adjusts an ops counter. Internal to the runtime.
 * @param {keyof OpsCounters} name
 * @param {number} delta
 * @returns {void}
 */
export function countOps(name, delta) {
  ops[name] += delta;
}

/** @returns {OpsCounters} */
export function getOpsCounters() {
  return { ...ops };
}

/**
 * Registers the counters of another layer (for example open pipes). Returns a function that removes it.
 * @param {CounterSource} source
 * @returns {() => void}
 */
export function registerCounterSource(source) {
  sources.add(source);
  return () => {
    sources.delete(source);
  };
}

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
export function counters() {
  const session = getSessionCounters();
  const invocation = getInvocationCounters();
  const pipe = getPipeCounters();
  const opsNow = getOpsCounters();
  let pipes = pipe.openPipes;
  let timers = session.timers + opsNow.timers;
  let subscriptions = opsNow.subscriptions;
  for (const source of sources) {
    const c = source();
    pipes += c.pipes ?? 0;
    timers += c.timers ?? 0;
    subscriptions += c.subscriptions ?? 0;
  }
  return {
    sessions: session.openSessions,
    pipes,
    timers,
    pendingInvocations: invocation.pendingInvocations + invocation.inboundInvocations,
    subscriptions,
    detail: { session, invocation, pipe, ops: opsNow },
  };
}

/**
 * True when every counter, the `detail` ones included, is zero.
 * @param {NexusCounters} [c] Default: {@link counters}().
 * @returns {boolean}
 */
export function isIdle(c = counters()) {
  const values = [c.sessions, c.pipes, c.timers, c.pendingInvocations, c.subscriptions,
    ...Object.values(c.detail.session), ...Object.values(c.detail.invocation), ...Object.values(c.detail.pipe), ...Object.values(c.detail.ops)];
  return values.every((v) => v === 0);
}
