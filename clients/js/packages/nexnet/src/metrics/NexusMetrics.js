/**
 * Client metrics with the C# instrument names and tags. A {@link NexusMetrics} tracks clients; listeners receive the
 * event instruments (`nexnet.session.disconnects`, `nexnet.session.duration`) as they happen, and {@link NexusMetrics#collect}
 * reads the observable ones (sessions, frames, bytes, budget) from the live sessions, as the C# observable instruments
 * do: the send and receive paths pay nothing for metrics.
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
import { systemClock } from '../session/clock.js';
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
 * @property {(type: string, listener: (event: Event) => void) => void} addEventListener
 * @property {(type: string, listener: (event: Event) => void) => void} removeEventListener
 */

/**
 * @typedef {object} Tracked
 * @property {MetricTagSet} tags
 * @property {import('../session/clock.js').Clock} clock
 * @property {import('../session/NexusSession.js').NexusSession | null} session The established session being counted.
 * @property {number} establishedAt Clock time it was seen established.
 * @property {{ framesSent: number, bytesSent: number, framesReceived: number, bytesReceived: number }} retired
 *   Final transport totals of the client's ended sessions.
 * @property {() => void} detach
 */

/**
 * Tracks clients and reports the C# instruments for them.
 *
 * Event instruments are recorded from the client's public events: `nexnet.session.duration` is measured from the
 * moment the session is seen established (the C# value starts at the transport connection), and sessions that end
 * during the handshake are not yet reported to `nexnet.session.disconnects`. Instruments that need per-session
 * counters the runtime does not expose yet (invocation queues, credit waits, pipes, large messages) are listed in
 * {@link Instruments} but not reported.
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
   * client twice is a no-op.
   * @param {MeteredClient} client
   * @returns {() => void}
   */
  track(client) {
    if (this.#clients.has(client)) return () => this.untrack(client);
    const tags = Object.freeze({ [MetricTags.role]: 'client', [MetricTags.transport]: transportTagOf(client.config) });
    /** @type {(event: Event) => void} */
    const onState = (event) => {
      const detail = /** @type {CustomEvent<{ state: number }>} */ (event).detail;
      if (detail?.state === ConnectionState.Connected) this.#onEstablished(tracked, client);
    };
    /** @type {(event: Event) => void} */
    const onDisconnected = (event) => {
      const reason = /** @type {CustomEvent<{ reason: number }>} */ (event).detail?.reason ?? 0;
      this.#onEnded(tracked, reason);
    };
    /** @type {Tracked} */
    const tracked = {
      tags,
      clock: client.config.clock ?? systemClock,
      session: null,
      establishedAt: 0,
      retired: { framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0 },
      detach: () => {
        client.removeEventListener('statechange', onState);
        client.removeEventListener('disconnected', onDisconnected);
        countOps('subscriptions', -2);
      },
    };
    client.addEventListener('statechange', onState);
    client.addEventListener('disconnected', onDisconnected);
    countOps('subscriptions', 2);
    this.#clients.set(client, tracked);
    if (client._session?.isHandshakeComplete) this.#onEstablished(tracked, client);
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
    tracked.detach();
    const totals = this.#totals(tracked);
    const key = JSON.stringify(tracked.tags);
    const retired = this.#retiredByTags.get(key) ?? { tags: tracked.tags, framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0 };
    retired.framesSent += totals.framesSent;
    retired.bytesSent += totals.bytesSent;
    retired.framesReceived += totals.framesReceived;
    retired.bytesReceived += totals.bytesReceived;
    this.#retiredByTags.set(key, retired);
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
    /** @type {Map<string, { tags: MetricTagSet, active: number, handshaking: number, framesSent: number, bytesSent: number, framesReceived: number, bytesReceived: number, reserved: number, limit: number }>} */
    const sums = new Map();
    const sumFor = (/** @type {MetricTagSet} */ tags) => {
      const key = JSON.stringify(tags);
      let s = sums.get(key);
      if (!s) {
        s = { tags, active: 0, handshaking: 0, framesSent: 0, bytesSent: 0, framesReceived: 0, bytesReceived: 0, reserved: 0, limit: 0 };
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
    for (const [client, tracked] of this.#clients) {
      const s = sumFor(tracked.tags);
      const totals = this.#totals(tracked);
      s.framesSent += totals.framesSent;
      s.bytesSent += totals.bytesSent;
      s.framesReceived += totals.framesReceived;
      s.bytesReceived += totals.bytesReceived;
      const session = client._session;
      if (session === null || session.state === ConnectionState.Disconnected) continue;
      s.active++;
      if (!session.isHandshakeComplete) s.handshaking++;
      const budget = getInvocationManager(session)?.budget;
      if (budget) {
        s.reserved += budget.reserved;
        s.limit += budget.limit;
      }
    }
    for (const s of sums.values()) {
      const sent = Object.freeze({ ...s.tags, [MetricTags.direction]: 'sent' });
      const received = Object.freeze({ ...s.tags, [MetricTags.direction]: 'received' });
      observe(Instruments.sessionActive, s.active, s.tags);
      observe(Instruments.sessionHandshaking, s.handshaking, s.tags);
      observe(Instruments.ioFrames, s.framesSent, sent);
      observe(Instruments.ioFrames, s.framesReceived, received);
      observe(Instruments.ioBytes, s.bytesSent, sent);
      observe(Instruments.ioBytes, s.bytesReceived, received);
      observe(Instruments.budgetReserved, s.reserved, s.tags);
      observe(Instruments.budgetLimit, s.limit, s.tags);
    }
  }

  /**
   * @param {Tracked} tracked
   * @param {MeteredClient} client
   */
  #onEstablished(tracked, client) {
    const session = client._session;
    if (session === null || session === tracked.session) return;
    if (tracked.session !== null) this.#retire(tracked);
    tracked.session = session;
    tracked.establishedAt = tracked.clock.now();
  }

  /**
   * @param {Tracked} tracked
   * @param {number} reason
   */
  #onEnded(tracked, reason) {
    if (tracked.session === null) return;
    const seconds = Math.max(0, tracked.clock.now() - tracked.establishedAt) / 1000;
    this.#retire(tracked);
    const reasonTag = disconnectReasonTag(reason);
    this.#emit('add', Instruments.sessionDisconnects, 1,
      Object.freeze({ ...tracked.tags, [MetricTags.disconnectReason]: reasonTag, [MetricTags.phase]: 'established' }));
    this.#emit('record', Instruments.sessionDuration, seconds,
      Object.freeze({ ...tracked.tags, [MetricTags.disconnectReason]: reasonTag }));
  }

  /** Moves the counted session's transport totals into `retired`. @param {Tracked} tracked */
  #retire(tracked) {
    const session = tracked.session;
    if (session === null) return;
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
