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
export type InstrumentDescriptor = import('./instruments.js').InstrumentDescriptor;
export type MetricTagSet = Readonly<Record<string, string>>;
export type NexusMetricsListener = {
    /**
     * Counter increments.
     */
    add?: (instrument: InstrumentDescriptor, value: number, tags: MetricTagSet) => void;
    /**
     * Histogram values.
     */
    record?: (instrument: InstrumentDescriptor, value: number, tags: MetricTagSet) => void;
};
export type ObserveCallback = (instrument: InstrumentDescriptor, value: number, tags: MetricTagSet) => void;
export type MeteredClient = {
    config: import('../contract/config.js').ClientConfig;
    _session: import('../session/NexusSession.js').NexusSession | null;
};
export type NexusSession = import('../session/NexusSession.js').NexusSession;
export type Tracked = {
    tags: MetricTagSet;
    /**
     * The client's session being counted, from its start until it ends.
     */
    session: NexusSession | null;
    detachSink: (() => void) | null;
    /**
     *   Final transport totals of the client's ended sessions.
     */
    retired: {
        framesSent: number;
        bytesSent: number;
        framesReceived: number;
        bytesReceived: number;
    };
};
export type LiveSums = {
    tags: MetricTagSet;
    active: number;
    handshaking: number;
    framesSent: number;
    bytesSent: number;
    framesReceived: number;
    bytesReceived: number;
    reserved: number;
    limit: number;
    pipesOpen: number;
    pipeWindow: number;
    queued: number;
    executing: number;
    separatePoolQueued: number;
    separatePoolExecuting: number;
    creditWaits: number;
    largeInbound: number;
    largeOutbound: number;
};
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
export declare class NexusMetrics {
    #private;
    /** A process-wide instance (like the C# shared meter). @returns {NexusMetrics} */
    static get shared(): NexusMetrics;
    /**
     * Adds a listener for the event instruments. Returns a function that removes it.
     * @param {NexusMetricsListener} listener
     * @returns {() => void}
     */
    addListener(listener: NexusMetricsListener): () => void;
    /** Clients being tracked. @returns {number} */
    get trackedClients(): number;
    /**
     * Starts reporting `client`. Returns a function that stops (also {@link NexusMetrics#untrack}). Tracking the same
     * client twice is a no-op. A session the client already has is counted from now on; its duration still runs from
     * its transport connection.
     * @param {MeteredClient} client
     * @returns {() => void}
     */
    track(client: MeteredClient): () => void;
    /**
     * Stops reporting `client`. Its transport totals stay in the cumulative counters.
     * @param {MeteredClient} client
     * @returns {void}
     */
    untrack(client: MeteredClient): void;
    /** Stops tracking every client and removes every listener. @returns {void} */
    dispose(): void;
    /**
     * Reads the observable instruments, one measurement per instrument and tag set.
     * @param {ObserveCallback} observe
     * @returns {void}
     */
    collect(observe: ObserveCallback): void;
}
