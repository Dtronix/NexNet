/**
 * Client metrics with the C# instrument names and tags. A {@link NexusMetrics} tracks clients; listeners receive the
 * event instruments (counters and histograms) as they happen, and {@link NexusMetrics#collect} reads the observable
 * ones (sessions, frames, bytes, budget, pipes, invocation queues, credit waits, large messages) from the live
 * sessions, as the C# observable instruments do: the send and receive paths pay nothing for metrics.
 *
 * Bridging to OpenTelemetry (`@opentelemetry/api`, not a dependency of this package):
 *
 * ```js
 * const meter = metrics.getMeter(METER_NAME);
 * const otel = new Map();
 * for (const d of Object.values(Instruments)) {
 *   const options = { unit: d.unit, description: d.description };
 *   otel.set(d.name, d.kind === 'counter' ? meter.createCounter(d.name, options)
 *     : d.kind === 'histogram' ? meter.createHistogram(d.name, { ...options, advice: { explicitBucketBoundaries: d.buckets } })
 *     : d.kind === 'observableCounter' ? meter.createObservableCounter(d.name, options)
 *     : meter.createObservableUpDownCounter(d.name, options));
 * }
 * nexusMetrics.addListener({
 *   add: (d, value, tags) => otel.get(d.name).add(value, tags),
 *   record: (d, value, tags) => otel.get(d.name).record(value, tags),
 * });
 * meter.addBatchObservableCallback((result) => {
 *   nexusMetrics.collect((d, value, tags) => result.observe(otel.get(d.name), value, tags));
 * }, [...otel.values()].filter((i) => typeof i.addCallback === 'function'));
 * ```
 * @module
 */

import { ConnectionState } from '../contract/enums.js';
import { getInvocationManager } from '../invocation/InvocationManager.js';
import { getPipeManager } from '../pipes/PipeManager.js';
import { addSessionObserver } from '../session/observers.js';
import { countOps } from '../diagnostics/counters.js';
import { Instruments, MetricTags, disconnectReasonTag, transportTagOf } from './instruments.js';

/** @typedef {import('./instruments.js').InstrumentDescriptor} InstrumentDescriptor */
/** @typedef {Readonly<Record<string, string>>} MetricTagSet */

/**
 * Receives the event instruments. Either member may be omitted.
 * @typedef {object} NexusMetricsListener
 * @property {(instrument: InstrumentDescriptor, value: number, tags: MetricTagSet) => void} [add] Counter increments.
 * @property {(instrument: InstrumentDescriptor, value: number, tags: MetricTagSet) => void} [record] Histogram values.
 */

/**
 * Called once per observable measurement by {@link NexusMetrics#collect}. Counter values are cumulative totals;
 * up-down counter values are the current level.
 * @callback ObserveCallback
 * @param {InstrumentDescriptor} instrument
 * @param {number} value
 * @param {MetricTagSet} tags
 * @returns {void}
 */

/**
 * What metrics need of a client. {@link import('../client/NexusClient.js').NexusClient} satisfies it.
 * @typedef {object} MeteredClient
 * @property {import('../contract/config.js').ClientConfig} config
 * @property {import('../session/NexusSession.js').NexusSession | null} _session
 */

/** @typedef {import('../session/NexusSession.js').NexusSession} NexusSession */

/**
 * @typedef {object} Tracked
 * @property {MetricTagSet} tags
 * @property {NexusSession | null} session The client's session being counted, from its start until it ends.
 * @property {(() => void) | null} detachSink
 * @property {{ framesSent: number, bytesSent: number, framesReceived: number, bytesReceived: number }} retired
 *   Final transport totals of the client's ended sessions.
 */

/**
 * @typedef {object} LiveSums
 * @property {MetricTagSet} tags
 * @property {number} active
 * @property {number} handshaking
 * @property {number} framesSent
 * @property {number} bytesSent
 * @property {number} framesReceived
 * @property {number} bytesReceived
 * @property {number} reserved
 * @property {number} limit
 * @property {number} pipesOpen
 * @property {number} pipeWindow
 * @property {number} queued
 * @property {number} executing
 * @property {number} separatePoolQueued
 * @property {number} separatePoolExecuting
 * @property {number} creditWaits
 * @property {number} largeInbound
 * @property {number} largeOutbound
 */

const EVENT_KINDS = Object.freeze({ counter: 'add', histogram: 'record' });

/**
 * Tracks clients and reports the C# instruments for them.
 *
 * A tracked client's sessions are counted from the moment their transport connects (the session starts) until they
 * end, the handshake included: `nexnet.session.duration` runs from the transport connection, and a session that ends
 * during the handshake is reported to `nexnet.session.disconnects` with `nexnet.session.phase=handshake`. The
 * session, invocation and pipe layers report the other event instruments (pipe resets and refusals, queue and credit
 * waits, large-message aborts) through the session's metrics hook. `nexnet.connection.rejections` and
 * `nexnet.invocation.busy` are server-only, and a client never records `nexnet.invocation.credit_drops` (only a
 * server's multi-session invocations drop calls for lack of credit).
 *
 * Listeners are called synchronously where the event happens, which may be the transport's data callback: keep them
 * cheap. A listener that throws is ignored.
 */
export class NexusMetrics {
  /** @type {NexusMetrics | null} */
  static #shared = null;

  /** A process-wide instance (like the C# shared meter). @returns {NexusMetrics} */
  static get shared() {
    return (NexusMetrics.#shared ??= new NexusMetrics());
  }

  /** @type {Set<NexusMetricsListener>} */
  #listeners = new Set();
  /** @type {Map<MeteredClient, Tracked>} */
  #clients = new Map();
  /** Transport totals of untracked clients, so the counters stay monotonic. @type {Map<string, Tracked['retired'] & { tags: MetricTagSet }>} */
  #retiredByTags = new Map();
  /** Removes the session observer; set while at least one client is tracked. @type {(() => void) | null} */
  #removeObserver = null;

  /**
   * Adds a listener for the event instruments. Returns a function that removes it.
   * @param {NexusMetricsListener} listener
   * @returns {() => void}
   */
  addListener(listener) {
    this.#listeners.add(listener);
    return () => {
      this.#listeners.delete(listener);
    };
  }

  /** Clients being tracked. @returns {number} */
  get trackedClients() {
    return this.#clients.size;
  }

  /**
   * Starts reporting `client`. Returns a function that stops (also {@link NexusMetrics#untrack}). Tracking the same
   * client twice is a no-op. A session the client already has is counted from now on; its duration still runs from
   * its transport connection.
   * @param {MeteredClient} client
   * @returns {() => void}
   */
  track(client) {
    if (this.#clients.has(client)) return () => this.untrack(client);
    const tags = Object.freeze({ [MetricTags.role]: 'client', [MetricTags.transport]: transportTagOf(client.config) });
    /** @type {Tracked} */
    const tracked = { tags, session: null, detachSink: null, retired: { framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0 } };
    this.#clients.set(client, tracked);
    if (this.#removeObserver === null) {
      this.#removeObserver = addSessionObserver({ started: (session) => this.#onSessionStarted(session) });
      countOps('subscriptions', 1);
    }
    const session = client._session;
    if (session !== null && session.state !== ConnectionState.Disconnected) this.#attach(tracked, session);
    return () => this.untrack(client);
  }

  /**
   * Stops reporting `client`. Its transport totals stay in the cumulative counters.
   * @param {MeteredClient} client
   * @returns {void}
   */
  untrack(client) {
    const tracked = this.#clients.get(client);
    if (!tracked) return;
    this.#clients.delete(client);
    this.#retire(tracked);
    const totals = tracked.retired;
    const key = JSON.stringify(tracked.tags);
    const retired = this.#retiredByTags.get(key) ?? { tags: tracked.tags, framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0 };
    retired.framesSent += totals.framesSent;
    retired.bytesSent += totals.bytesSent;
    retired.framesReceived += totals.framesReceived;
    retired.bytesReceived += totals.bytesReceived;
    this.#retiredByTags.set(key, retired);
    if (this.#clients.size === 0 && this.#removeObserver !== null) {
      this.#removeObserver();
      this.#removeObserver = null;
      countOps('subscriptions', -1);
    }
  }

  /** Stops tracking every client and removes every listener. @returns {void} */
  dispose() {
    for (const client of [...this.#clients.keys()]) this.untrack(client);
    this.#listeners.clear();
  }

  /**
   * Reads the observable instruments, one measurement per instrument and tag set.
   * @param {ObserveCallback} observe
   * @returns {void}
   */
  collect(observe) {
    /** @type {Map<string, LiveSums>} */
    const sums = new Map();
    const sumFor = (/** @type {MetricTagSet} */ tags) => {
      const key = JSON.stringify(tags);
      let s = sums.get(key);
      if (!s) {
        s = {
          tags, active: 0, handshaking: 0, framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0, reserved: 0, limit: 0,
          pipesOpen: 0, pipeWindow: 0, queued: 0, executing: 0, separatePoolQueued: 0, separatePoolExecuting: 0, creditWaits: 0,
          largeInbound: 0, largeOutbound: 0,
        };
        sums.set(key, s);
      }
      return s;
    };
    for (const retired of this.#retiredByTags.values()) {
      const s = sumFor(retired.tags);
      s.framesSent += retired.framesSent;
      s.bytesSent += retired.bytesSent;
      s.framesReceived += retired.framesReceived;
      s.bytesReceived += retired.bytesReceived;
    }
    for (const tracked of this.#clients.values()) {
      const s = sumFor(tracked.tags);
      const totals = this.#totals(tracked);
      s.framesSent += totals.framesSent;
      s.bytesSent += totals.bytesSent;
      s.framesReceived += totals.framesReceived;
      s.bytesReceived += totals.bytesReceived;
      const session = tracked.session;
      if (session === null || session.state === ConnectionState.Disconnected) continue;
      s.active++;
      if (!session.isHandshakeComplete) s.handshaking++;
      const invocations = getInvocationManager(session);
      if (invocations) {
        s.reserved += invocations.budget.reserved;
        s.limit += invocations.budget.limit;
        const pools = invocations.poolCounts;
        s.queued += pools.queued;
        s.executing += pools.executing;
        s.separatePoolQueued += pools.separatePoolQueued;
        s.separatePoolExecuting += pools.separatePoolExecuting;
        s.creditWaits += invocations.creditWaits;
        const large = invocations.largeMessageCounts;
        s.largeInbound += large.inbound;
        s.largeOutbound += large.outbound;
      }
      const pipes = getPipeManager(session)?.openPipes ?? 0;
      s.pipesOpen += pipes;
      s.pipeWindow += pipes * session.options.pipeWindow;
    }
    for (const s of sums.values()) {
      const tagged = (/** @type {string} */ key, /** @type {string} */ value) => Object.freeze({ ...s.tags, [key]: value });
      const sent = tagged(MetricTags.direction, 'sent');
      const received = tagged(MetricTags.direction, 'received');
      const normal = tagged(MetricTags.invocationPool, 'normal');
      const separate = tagged(MetricTags.invocationPool, 'separate');
      observe(Instruments.sessionActive, s.active, s.tags);
      observe(Instruments.sessionHandshaking, s.handshaking, s.tags);
      observe(Instruments.ioFrames, s.framesSent, sent);
      observe(Instruments.ioFrames, s.framesReceived, received);
      observe(Instruments.ioBytes, s.bytesSent, sent);
      observe(Instruments.ioBytes, s.bytesReceived, received);
      observe(Instruments.budgetReserved, s.reserved, s.tags);
      observe(Instruments.budgetLimit, s.limit, s.tags);
      observe(Instruments.pipeOpen, s.pipesOpen, s.tags);
      observe(Instruments.pipeWindow, s.pipeWindow, s.tags);
      observe(Instruments.invocationQueued, s.queued, normal);
      observe(Instruments.invocationQueued, s.separatePoolQueued, separate);
      observe(Instruments.invocationExecuting, s.executing, normal);
      observe(Instruments.invocationExecuting, s.separatePoolExecuting, separate);
      observe(Instruments.invocationCreditWaits, s.creditWaits, s.tags);
      observe(Instruments.largeMessageInFlight, s.largeInbound, tagged(MetricTags.direction, 'inbound'));
      observe(Instruments.largeMessageInFlight, s.largeOutbound, tagged(MetricTags.direction, 'outbound'));
    }
  }

  /**
   * A session started (its transport connected): counts it if it belongs to a tracked client.
   * @param {NexusSession} session
   */
  #onSessionStarted(session) {
    for (const [client, tracked] of this.#clients) {
      if (client._session === session) {
        this.#attach(tracked, session);
        return;
      }
    }
  }

  /**
   * @param {Tracked} tracked
   * @param {NexusSession} session
   */
  #attach(tracked, session) {
    if (tracked.session === session) return;
    this.#retire(tracked);
    tracked.session = session;
    tracked.detachSink = session.addMetricsSink((name, value, extra) => this.#onMeasurement(tracked, name, value, extra));
    session.onClose((reason) => {
      if (tracked.session === session) this.#onEnded(tracked, session, reason);
    });
  }

  /**
   * An event instrument reported by a session layer.
   * @param {Tracked} tracked
   * @param {string} name
   * @param {number} value
   * @param {Readonly<Record<string, string>> | undefined} extra
   */
  #onMeasurement(tracked, name, value, extra) {
    const instrument = /** @type {Record<string, InstrumentDescriptor>} */ (Instruments)[name];
    if (instrument === undefined) return;
    const kind = /** @type {Record<string, 'add' | 'record'>} */ (EVENT_KINDS)[instrument.kind];
    if (kind === undefined) return;
    this.#emit(kind, instrument, value, extra ? Object.freeze({ ...tracked.tags, ...extra }) : tracked.tags);
  }

  /**
   * The session ended: records its duration (from the transport connection) and its disconnect, in the C# order.
   * @param {Tracked} tracked
   * @param {NexusSession} session
   * @param {number} reason
   */
  #onEnded(tracked, session, reason) {
    const seconds = Math.max(0, session.options.clock.now() - session.connectedAt) / 1000;
    const phase = session.isHandshakeComplete ? 'established' : 'handshake';
    this.#retire(tracked);
    const reasonTag = disconnectReasonTag(reason);
    this.#emit('record', Instruments.sessionDuration, seconds,
      Object.freeze({ ...tracked.tags, [MetricTags.disconnectReason]: reasonTag }));
    this.#emit('add', Instruments.sessionDisconnects, 1,
      Object.freeze({ ...tracked.tags, [MetricTags.disconnectReason]: reasonTag, [MetricTags.phase]: phase }));
  }

  /** Stops counting the tracked session and moves its transport totals into `retired`. @param {Tracked} tracked */
  #retire(tracked) {
    const session = tracked.session;
    if (session === null) return;
    tracked.detachSink?.();
    tracked.detachSink = null;
    tracked.retired.framesSent += session.framesSent;
    tracked.retired.bytesSent += session.bytesSent;
    tracked.retired.framesReceived += session.framesReceived;
    tracked.retired.bytesReceived += session.bytesReceived;
    tracked.session = null;
  }

  /**
   * @param {Tracked} tracked
   * @returns {Tracked['retired']}
   */
  #totals(tracked) {
    const s = tracked.session;
    const r = tracked.retired;
    if (s === null) return { ...r };
    return {
      framesSent: r.framesSent + s.framesSent,
      bytesSent: r.bytesSent + s.bytesSent,
      framesReceived: r.framesReceived + s.framesReceived,
      bytesReceived: r.bytesReceived + s.bytesReceived,
    };
  }

  /**
   * @param {'add' | 'record'} kind
   * @param {InstrumentDescriptor} instrument
   * @param {number} value
   * @param {MetricTagSet} tags
   */
  #emit(kind, instrument, value, tags) {
    for (const listener of this.#listeners) {
      try {
        listener[kind]?.(instrument, value, tags);
      } catch {
        // A listener must never break the client.
      }
    }
  }
}
