/**
 * Client session snapshot (C# `NexusSessionSnapshot` and its parts), field names camelCased. Durations (C#
 * `TimeSpan`) are milliseconds; IDs are as on the wire (`id` is a bigint). The field names are checked against
 * `tests/golden/snapshot-fields.json`, exported from the C# records.
 *
 * Every layer provides its part (J17): `session.snapshot()` → {@link SessionSnapshotPart},
 * `invocationManager.snapshot()` → {@link NexusInvocationSnapshot}, and `session.pipeSnapshots()` /
 * `session.largeMessageSnapshots()`, which the pipe and invocation layers feed through `session.addSnapshotSource`.
 * A field is null only where the C# field is null (`endpoint` on a client, a wait or a time that has not started).
 * @module
 */

import { getInvocationManager } from '../invocation/InvocationManager.js';
import { systemClock } from '../session/clock.js';
import { transportTagOf } from '../metrics/instruments.js';

/**
 * @typedef {object} NexusBudgetSnapshot
 * @property {number} limit
 * @property {number} reserved
 */

/**
 * @typedef {object} NexusInvocationSnapshot
 * @property {number} queued
 * @property {number} executing
 * @property {number} maxExecuting
 * @property {number} inboundCredits
 * @property {number} outboundCredits
 * @property {number} creditWaits
 * @property {number} awaitingResults
 * @property {number} creditDrops
 * @property {number | null} oldestQueuedWait Null when none is queued.
 * @property {number} separatePoolQueued
 * @property {number} separatePoolExecuting
 * @property {number} separatePoolMaxExecuting
 * @property {number | null} separatePoolOldestQueuedWait Null when none is queued.
 */

/**
 * @typedef {object} NexusPartialItemSnapshot
 * @property {number} length
 * @property {number} received
 */

/**
 * @typedef {object} NexusPipeSnapshot
 * @property {number} id
 * @property {boolean} isReady
 * @property {boolean} readerCompleted
 * @property {boolean} writerCompleted
 * @property {number | null} closing
 * @property {number} window
 * @property {number} receiveCredit
 * @property {number} buffered
 * @property {number} consumedNotGranted
 * @property {number} extraOutstanding
 * @property {number | null} sinceLastData
 * @property {NexusPartialItemSnapshot | null} partialItem
 * @property {number} sendCredit
 * @property {number | null} waitingForCredit
 * @property {number} waitingBytes
 */

/**
 * @typedef {object} NexusLargeMessageSnapshot
 * @property {'inbound' | 'outbound'} direction
 * @property {'argument' | 'result'} kind
 * @property {number} invocationId
 * @property {number} length
 * @property {number} transferred
 * @property {number} sinceLastProgress
 * @property {boolean} aborting
 */

/**
 * @typedef {object} NexusSessionSnapshot
 * @property {bigint | null} id
 * @property {boolean} isServer Always false on a client.
 * @property {string} transport As in the `nexnet.transport` metric tag.
 * @property {string | null} endpoint Always null on a client.
 * @property {boolean | null} isSecure
 * @property {number | null} pingInterval Milliseconds between pings (the configured or the derived interval).
 * @property {import('../contract/enums.js').ConnectionStateValue} state
 * @property {boolean} isEstablished
 * @property {number} connected Milliseconds since the transport connected.
 * @property {number} sinceLastReceived
 * @property {number | null} sinceLastSent Null when nothing was written yet.
 * @property {number | null} flushInProgress Null when no transport write is in flight.
 * @property {NexusBudgetSnapshot} budget
 * @property {NexusInvocationSnapshot} invocations
 * @property {NexusPipeSnapshot[]} pipes
 * @property {NexusLargeMessageSnapshot[]} largeMessages
 * @property {number} framesSent
 * @property {number} bytesSent
 * @property {number} framesReceived
 * @property {number} bytesReceived
 */

/**
 * The session-layer part a session may provide through `snapshot()`.
 * @typedef {object} SessionSnapshotPart
 * @property {number} connected
 * @property {number | null} sinceLastSent
 * @property {number | null} flushInProgress
 */

/**
 * What the snapshot needs of a client. {@link import('../client/NexusClient.js').NexusClient} satisfies it.
 * @typedef {object} SnapshotClient
 * @property {import('../contract/config.js').ClientConfig} config
 * @property {boolean} isSecure
 * @property {import('../session/NexusSession.js').NexusSession | null} _session
 */

/**
 * A snapshot of the client's session, or null when it has no session that completed the handshake (C#
 * `NexusClient.GetSnapshot()`).
 * @param {SnapshotClient} client
 * @returns {NexusSessionSnapshot | null}
 */
export function getClientSnapshot(client) {
  const session = client._session;
  if (session === null || !session.isHandshakeComplete) return null;
  const options = session.options;
  const clock = options.clock ?? systemClock;
  const now = clock.now();
  const manager = getInvocationManager(session);
  const part = session.snapshot(now);

  return {
    id: session.id,
    isServer: false,
    transport: transportTagOf(client.config),
    endpoint: null,
    isSecure: client.isSecure,
    pingInterval: session.pingInterval > 0 ? session.pingInterval : null,
    state: session.state,
    isEstablished: session.isHandshakeComplete,
    connected: part.connected,
    sinceLastReceived: Math.max(0, now - session.lastReceived),
    sinceLastSent: part.sinceLastSent,
    flushInProgress: part.flushInProgress,
    budget: manager ? { limit: manager.budget.limit, reserved: manager.budget.reserved } : { limit: options.sessionBudget, reserved: 0 },
    invocations: manager ? manager.snapshot(now) : emptyInvocations(options),
    pipes: session.pipeSnapshots(now),
    largeMessages: session.largeMessageSnapshots(now),
    framesSent: session.framesSent,
    bytesSent: session.bytesSent,
    framesReceived: session.framesReceived,
    bytesReceived: session.bytesReceived,
  };
}

/**
 * The invocation part of a session without an invocation layer (never the case for a `NexusClient` session).
 * @param {import('../session/options.js').ResolvedClientOptions} options
 * @returns {NexusInvocationSnapshot}
 */
function emptyInvocations(options) {
  return {
    queued: 0,
    executing: 0,
    maxExecuting: options.maxConcurrentConnectionInvocations,
    inboundCredits: options.invocationCredits,
    outboundCredits: 0,
    creditWaits: 0,
    awaitingResults: 0,
    creditDrops: 0,
    oldestQueuedWait: null,
    separatePoolQueued: 0,
    separatePoolExecuting: 0,
    separatePoolMaxExecuting: options.maxSeparatePoolInvocations,
    separatePoolOldestQueuedWait: null,
  };
}

/**
 * Field names of each snapshot record, in C# declaration order, for the cross-language field check.
 */
export const SnapshotFields = Object.freeze({
  NexusSessionSnapshot: Object.freeze(['id', 'isServer', 'transport', 'endpoint', 'isSecure', 'pingInterval', 'state', 'isEstablished', 'connected',
    'sinceLastReceived', 'sinceLastSent', 'flushInProgress', 'budget', 'invocations', 'pipes', 'largeMessages', 'framesSent',
    'bytesSent', 'framesReceived', 'bytesReceived']),
  NexusBudgetSnapshot: Object.freeze(['limit', 'reserved']),
  NexusInvocationSnapshot: Object.freeze(['queued', 'executing', 'maxExecuting', 'inboundCredits', 'outboundCredits', 'creditWaits',
    'awaitingResults', 'creditDrops', 'oldestQueuedWait', 'separatePoolQueued', 'separatePoolExecuting', 'separatePoolMaxExecuting',
    'separatePoolOldestQueuedWait']),
  NexusPipeSnapshot: Object.freeze(['id', 'isReady', 'readerCompleted', 'writerCompleted', 'closing', 'window', 'receiveCredit',
    'buffered', 'consumedNotGranted', 'extraOutstanding', 'sinceLastData', 'partialItem', 'sendCredit', 'waitingForCredit',
    'waitingBytes']),
  NexusPartialItemSnapshot: Object.freeze(['length', 'received']),
  NexusLargeMessageSnapshot: Object.freeze(['direction', 'kind', 'invocationId', 'length', 'transferred', 'sinceLastProgress', 'aborting']),
});
