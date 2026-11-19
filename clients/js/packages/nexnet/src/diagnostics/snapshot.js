/**
 * Client session snapshot (C# `NexusSessionSnapshot` and its parts), field names camelCased. Durations (C#
 * `TimeSpan`) are milliseconds; IDs are as on the wire (`id` is a bigint). The field names are checked against
 * `tests/golden/snapshot-fields.json`, exported from the C# records.
 *
 * Values a runtime layer does not expose yet are null. A layer provides its part by implementing the optional
 * provider members: `session.snapshot()` → {@link SessionSnapshotPart}, `invocationManager.snapshot()` →
 * {@link NexusInvocationSnapshot}, and `session.pipeSnapshots()` / `session.largeMessageSnapshots()`.
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
 * @property {number | null} queued
 * @property {number | null} executing
 * @property {number} maxExecuting
 * @property {number | null} inboundCredits
 * @property {number | null} outboundCredits
 * @property {number | null} creditWaits
 * @property {number | null} awaitingResults
 * @property {number | null} creditDrops
 * @property {number | null} oldestQueuedWait
 * @property {number | null} separatePoolQueued
 * @property {number | null} separatePoolExecuting
 * @property {number} separatePoolMaxExecuting
 * @property {number | null} separatePoolOldestQueuedWait
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
 * @property {number | null} connected Milliseconds since the transport connected.
 * @property {number} sinceLastReceived
 * @property {number | null} sinceLastSent
 * @property {number | null} flushInProgress
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
 * @property {number | null} [connected]
 * @property {number | null} [sinceLastSent]
 * @property {number | null} [flushInProgress]
 */

/**
 * What the snapshot needs of a client. {@link import('../client/NexusClient.js').NexusClient} satisfies it.
 * @typedef {object} SnapshotClient
 * @property {import('../contract/config.js').ClientConfig} config
 * @property {boolean} isSecure
 * @property {import('../session/NexusSession.js').NexusSession | null} _session
 */

/**
 * @param {unknown} target
 * @param {string} name
 * @returns {any}
 */
function provide(target, name) {
  const fn = /** @type {Record<string, unknown> | null} */ (target)?.[name];
  return typeof fn === 'function' ? fn.call(target) : undefined;
}

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
  /** @type {SessionSnapshotPart} */
  const part = provide(session, 'snapshot') ?? {};

  /** @type {NexusInvocationSnapshot} */
  const invocations = {
    queued: null,
    executing: null,
    maxExecuting: options.maxConcurrentConnectionInvocations,
    inboundCredits: null,
    outboundCredits: manager ? manager.availableCredits : null,
    creditWaits: null,
    awaitingResults: null,
    creditDrops: null,
    oldestQueuedWait: null,
    separatePoolQueued: null,
    separatePoolExecuting: null,
    separatePoolMaxExecuting: options.maxSeparatePoolInvocations,
    separatePoolOldestQueuedWait: null,
    ...(provide(manager, 'snapshot') ?? {}),
  };

  return {
    id: session.id,
    isServer: false,
    transport: transportTagOf(client.config),
    endpoint: null,
    isSecure: client.isSecure,
    pingInterval: session.pingInterval > 0 ? session.pingInterval : null,
    state: session.state,
    isEstablished: session.isHandshakeComplete,
    connected: part.connected ?? null,
    sinceLastReceived: Math.max(0, now - session.lastReceived),
    sinceLastSent: part.sinceLastSent ?? null,
    flushInProgress: part.flushInProgress ?? null,
    budget: manager ? { limit: manager.budget.limit, reserved: manager.budget.reserved } : { limit: options.sessionBudget, reserved: 0 },
    invocations,
    pipes: provide(session, 'pipeSnapshots') ?? [],
    largeMessages: provide(session, 'largeMessageSnapshots') ?? [],
    framesSent: session.framesSent,
    bytesSent: session.bytesSent,
    framesReceived: session.framesReceived,
    bytesReceived: session.bytesReceived,
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
