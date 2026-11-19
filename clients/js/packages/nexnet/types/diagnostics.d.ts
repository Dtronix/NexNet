/**
 * `@dtronix/nexnet/diagnostics`: the runtime's process-wide diagnostic counters, for leak audits (the interop hosts'
 * per-test audit). After every client has disconnected, every counter must be zero. Each getter returns a snapshot
 * copy. Platform-neutral: must not import `node:*`.
 * @module
 */
export { getSessionCounters } from './session/diagnostics.js';
export { getInvocationCounters } from './invocation/diagnostics.js';
export type SessionCounters = import('./session/diagnostics.js').SessionCounters;
export type InvocationCounters = import('./invocation/diagnostics.js').InvocationCounters;
/** @typedef {import('./session/diagnostics.js').SessionCounters} SessionCounters */
/** @typedef {import('./invocation/diagnostics.js').InvocationCounters} InvocationCounters */
