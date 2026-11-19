/**
 * Diagnostics (J10): leak-audit counters and client snapshots.
 * @module
 */
import { counters, isIdle, registerCounterSource } from './counters.js';
import { getSessionCounters } from '../session/diagnostics.js';
import { getInvocationCounters } from '../invocation/diagnostics.js';
import { getPipeCounters } from '../pipes/diagnostics.js';
export { counters, isIdle, registerCounterSource, getOpsCounters } from './counters.js';
export { getClientSnapshot, SnapshotFields } from './snapshot.js';
export { getSessionCounters, getInvocationCounters, getPipeCounters };
/**
 * The diagnostics namespace: `diagnostics.counters()` for the H05 leak audit.
 */
export declare const diagnostics: Readonly<{
    counters: typeof counters;
    isIdle: typeof isIdle;
    registerCounterSource: typeof registerCounterSource;
    getSessionCounters: typeof getSessionCounters;
    getInvocationCounters: typeof getInvocationCounters;
    getPipeCounters: typeof getPipeCounters;
}>;
