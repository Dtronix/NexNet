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
export type NexusBudgetSnapshot = {
    limit: number;
    reserved: number;
};
export type NexusInvocationSnapshot = {
    queued: number;
    executing: number;
    maxExecuting: number;
    inboundCredits: number;
    outboundCredits: number;
    creditWaits: number;
    awaitingResults: number;
    creditDrops: number;
    /**
     * Null when none is queued.
     */
    oldestQueuedWait: number | null;
    separatePoolQueued: number;
    separatePoolExecuting: number;
    separatePoolMaxExecuting: number;
    /**
     * Null when none is queued.
     */
    separatePoolOldestQueuedWait: number | null;
};
export type NexusPartialItemSnapshot = {
    length: number;
    received: number;
};
export type NexusPipeSnapshot = {
    id: number;
    isReady: boolean;
    readerCompleted: boolean;
    writerCompleted: boolean;
    closing: number | null;
    window: number;
    receiveCredit: number;
    buffered: number;
    consumedNotGranted: number;
    extraOutstanding: number;
    sinceLastData: number | null;
    partialItem: NexusPartialItemSnapshot | null;
    sendCredit: number;
    waitingForCredit: number | null;
    waitingBytes: number;
};
export type NexusLargeMessageSnapshot = {
    direction: 'inbound' | 'outbound';
    kind: 'argument' | 'result';
    invocationId: number;
    length: number;
    transferred: number;
    sinceLastProgress: number;
    aborting: boolean;
};
export type NexusSessionSnapshot = {
    id: bigint | null;
    /**
     * Always false on a client.
     */
    isServer: boolean;
    /**
     * As in the `nexnet.transport` metric tag.
     */
    transport: string;
    /**
     * Always null on a client.
     */
    endpoint: string | null;
    isSecure: boolean | null;
    /**
     * Milliseconds between pings (the configured or the derived interval).
     */
    pingInterval: number | null;
    state: import('../contract/enums.js').ConnectionStateValue;
    isEstablished: boolean;
    /**
     * Milliseconds since the transport connected.
     */
    connected: number;
    sinceLastReceived: number;
    /**
     * Null when nothing was written yet.
     */
    sinceLastSent: number | null;
    /**
     * Null when no transport write is in flight.
     */
    flushInProgress: number | null;
    budget: NexusBudgetSnapshot;
    invocations: NexusInvocationSnapshot;
    pipes: NexusPipeSnapshot[];
    largeMessages: NexusLargeMessageSnapshot[];
    framesSent: number;
    bytesSent: number;
    framesReceived: number;
    bytesReceived: number;
};
export type SessionSnapshotPart = {
    connected: number;
    sinceLastSent: number | null;
    flushInProgress: number | null;
};
export type SnapshotClient = {
    config: import('../contract/config.js').ClientConfig;
    isSecure: boolean;
    _session: import('../session/NexusSession.js').NexusSession | null;
};
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
export declare function getClientSnapshot(client: SnapshotClient): NexusSessionSnapshot | null;
/**
 * Field names of each snapshot record, in C# declaration order, for the cross-language field check.
 */
export declare const SnapshotFields: Readonly<{
    NexusSessionSnapshot: readonly string[];
    NexusBudgetSnapshot: readonly string[];
    NexusInvocationSnapshot: readonly string[];
    NexusPipeSnapshot: readonly string[];
    NexusPartialItemSnapshot: readonly string[];
    NexusLargeMessageSnapshot: readonly string[];
}>;
