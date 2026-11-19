/**
 * The NexNet instruments: names, kinds, units and tags identical to C# `NexusMetrics` (docs/articles/metrics.md).
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';

/** Tag keys. */
export const MetricTags = Object.freeze({
  role: 'nexnet.role',
  transport: 'nexnet.transport',
  endpoint: 'nexnet.endpoint',
  direction: 'nexnet.direction',
  disconnectReason: 'nexnet.disconnect.reason',
  phase: 'nexnet.session.phase',
  rejectionReason: 'nexnet.rejection.reason',
  resetReason: 'nexnet.reset.reason',
  refusalSide: 'nexnet.refusal.side',
  abortReason: 'nexnet.abort.reason',
  invocationPool: 'nexnet.invocation.pool',
});

/** Name of the meter (C# `NexusMeter.Name`). */
export const METER_NAME = 'NexNet';

/**
 * How an instrument reports. `observable*` instruments are read by {@link import('./NexusMetrics.js').NexusMetrics#collect};
 * `counter` and `histogram` measurements are pushed to listeners as they happen.
 * @typedef {'observableCounter' | 'observableUpDownCounter' | 'counter' | 'histogram'} InstrumentKind
 */

/**
 * @typedef {object} InstrumentDescriptor
 * @property {string} name
 * @property {InstrumentKind} kind
 * @property {string} unit
 * @property {string} description
 * @property {readonly string[]} tags Tag keys beyond `nexnet.role` and `nexnet.transport` (and `nexnet.endpoint` on servers).
 * @property {boolean} serverOnly True when only a server records it.
 * @property {readonly number[]} [buckets] Histogram bucket boundaries advised by C#, in the instrument's unit.
 */

const SESSION_BUCKETS = Object.freeze([0.1, 1, 10, 60, 300, 1800, 3600, 4 * 3600, 24 * 3600]);
const WAIT_BUCKETS = Object.freeze([0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60]);

/**
 * @param {string} name
 * @param {InstrumentKind} kind
 * @param {string} unit
 * @param {string} description
 * @param {readonly string[]} [tags]
 * @param {{ serverOnly?: boolean, buckets?: readonly number[] }} [extra]
 * @returns {Readonly<InstrumentDescriptor>}
 */
function instrument(name, kind, unit, description, tags = [], extra = {}) {
  return Object.freeze({ name, kind, unit, description, tags: Object.freeze([...tags]), serverOnly: extra.serverOnly ?? false, ...(extra.buckets ? { buckets: extra.buckets } : {}) });
}

/** Every instrument of the C# meter, by camelCase key. */
export const Instruments = Object.freeze({
  sessionActive: instrument('nexnet.session.active', 'observableUpDownCounter', '{session}',
    'Sessions open, from transport connection until disconnect.'),
  sessionHandshaking: instrument('nexnet.session.handshaking', 'observableUpDownCounter', '{session}',
    'Sessions that have not completed the handshake (and, on a server, authentication).'),
  sessionDisconnects: instrument('nexnet.session.disconnects', 'counter', '{session}',
    'Sessions ended, by disconnect reason and by whether the handshake had completed.', [MetricTags.disconnectReason, MetricTags.phase]),
  sessionDuration: instrument('nexnet.session.duration', 'histogram', 's',
    'Time from transport connection to disconnect.', [MetricTags.disconnectReason], { buckets: SESSION_BUCKETS }),
  connectionRejections: instrument('nexnet.connection.rejections', 'counter', '{connection}',
    'Connections a server closed before starting a session: handshake limit or rate limiting.', [MetricTags.rejectionReason], { serverOnly: true }),
  ioFrames: instrument('nexnet.io.frames', 'observableCounter', '{frame}',
    'Frames written to and read from the transport.', [MetricTags.direction]),
  ioBytes: instrument('nexnet.io.bytes', 'observableCounter', 'By',
    'Bytes written to and read from the transport, frame headers included.', [MetricTags.direction]),
  budgetReserved: instrument('nexnet.budget.reserved', 'observableUpDownCounter', 'By',
    'Session budget reserved: pipe windows, invocation queues and large values.'),
  budgetLimit: instrument('nexnet.budget.limit', 'observableUpDownCounter', 'By', 'Session budget in total.'),
  pipeOpen: instrument('nexnet.pipe.open', 'observableUpDownCounter', '{pipe}',
    'Pipes holding a receive window: open, or closing until both sides confirmed.'),
  pipeWindow: instrument('nexnet.pipe.window', 'observableUpDownCounter', 'By', 'Receive windows reserved by open pipes.'),
  pipeResets: instrument('nexnet.pipe.resets', 'counter', '{pipe}',
    'Pipes reset because the peer sent a channel item that breaks the framing or exceeds MaxMessageSize.', [MetricTags.resetReason]),
  pipeRefusals: instrument('nexnet.pipe.refusals', 'counter', '{pipe}',
    'Pipe opens refused because a session limit was reached.', [MetricTags.refusalSide]),
  invocationQueued: instrument('nexnet.invocation.queued', 'observableUpDownCounter', '{invocation}',
    'Invocations received and waiting for an execution slot, by invocation pool.', [MetricTags.invocationPool]),
  invocationExecuting: instrument('nexnet.invocation.executing', 'observableUpDownCounter', '{invocation}',
    'Invocations running, by invocation pool.', [MetricTags.invocationPool]),
  invocationCreditWaits: instrument('nexnet.invocation.credit_waits', 'observableUpDownCounter', '{invocation}',
    'Calls waiting for the peer to return invocation credit.'),
  invocationBusy: instrument('nexnet.invocation.busy', 'counter', '{invocation}',
    'Invocations to several sessions that found a session busy on the first pass.', [], { serverOnly: true }),
  invocationCreditDrops: instrument('nexnet.invocation.credit_drops', 'counter', '{invocation}',
    'Invocations skipped because the peer returned no invocation credit in time.'),
  invocationQueueWait: instrument('nexnet.invocation.queue_wait', 'histogram', 's',
    'Time an invocation waited from being queued until it took an execution slot.', [MetricTags.invocationPool], { buckets: WAIT_BUCKETS }),
  pipeCreditWait: instrument('nexnet.pipe.credit_wait', 'histogram', 's',
    'Time a pipe writer waited for the peer to grant window credit, recorded when the wait ends.', [], { buckets: WAIT_BUCKETS }),
  largeMessageInFlight: instrument('nexnet.large_message.in_flight', 'observableUpDownCounter', '{message}',
    'Arguments and results larger than one frame being received or sent.', [MetricTags.direction]),
  largeMessageAborts: instrument('nexnet.large_message.aborts', 'counter', '{message}',
    'Arguments and results larger than one frame stopped before completing.', [MetricTags.direction, MetricTags.abortReason]),
});

/** @typedef {keyof typeof Instruments} InstrumentKey */

/**
 * C# `NexusMetrics.ToSnakeCase`: `SocketError` → `socket_error`.
 * @param {string} name
 * @returns {string}
 */
export function toSnakeCase(name) {
  return name.replace(/[A-Z]/g, (c, i) => (i > 0 ? '_' : '') + c.toLowerCase());
}

/**
 * The `nexnet.disconnect.reason` tag value of a disconnect code: the `DisconnectReason` name in snake case, or
 * `unknown` for an unnamed code.
 * @param {number} reason
 * @returns {string}
 */
export function disconnectReasonTag(reason) {
  const names = /** @type {Record<string, number>} */ (DisconnectReason);
  const name = Object.keys(names).find((key) => names[key] === reason);
  return name === undefined ? 'unknown' : toSnakeCase(name);
}

/** `nexnet.transport` tag value per {@link import('../contract/config.js').TransportKind} (C# `TransportName`). */
export const TransportTags = Object.freeze({
  webSocket: 'websocket',
  httpSocket: 'httpsocket',
  tcp: 'tcp',
  tcpTls: 'tls',
  uds: 'uds',
});

/**
 * The `nexnet.transport` tag value of a client configuration; `custom` when no known transport is set.
 * @param {import('../contract/config.js').ClientConfig} config
 * @returns {string}
 */
export function transportTagOf(config) {
  for (const [kind, tag] of Object.entries(TransportTags)) {
    const value = /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (config))[kind];
    if (value !== undefined && value !== null) return tag;
  }
  return 'custom';
}
