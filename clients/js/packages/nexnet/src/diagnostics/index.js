/**
 * Diagnostics: leak-audit counters and client snapshots.
 * @module
 */

import { counters, isIdle, registerCounterSource } from './counters.js';
import { getSessionCounters } from '../session/diagnostics.js';
import { getInvocationCounters } from '../invocation/diagnostics.js';
import { getPipeCounters } from '../pipes/diagnostics.js';
import { getReservedInvocationIds } from './snapshot.js';

export { counters, isIdle, registerCounterSource, getOpsCounters } from './counters.js';
export { getClientSnapshot, getReservedInvocationIds, SnapshotFields } from './snapshot.js';
export { getSessionCounters, getInvocationCounters, getPipeCounters };

/**
 * The diagnostics namespace: `diagnostics.counters()` for the interop harness leak audit.
 */
export const diagnostics = Object.freeze({
  counters,
  isIdle,
  registerCounterSource,
  getSessionCounters,
  getInvocationCounters,
  getPipeCounters,
  getReservedInvocationIds,
});
