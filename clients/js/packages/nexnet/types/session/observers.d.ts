/**
 * Process-wide observers of session starts, and the per-session hook points the metrics layer (J17) uses. Internal to
 * the runtime: the package entries do not export them.
 *
 * The metrics layer learns of every session through {@link addSessionObserver} as the session starts (its transport
 * connected), before any frame is read, so sessions that end during the handshake are counted too. It then attaches a
 * {@link MetricsSink} to the sessions of the clients it tracks; the session, invocation and pipe layers report their
 * event instruments through `session.recordMetric(name, value, tags)`, which costs one empty-array check when nothing
 * is attached.
 * @module
 */
export type MetricsSink = (name: string, value: number, tags: Readonly<Record<string, string>> | undefined) => void;
export type SessionObserver = {
    /**
     * Called synchronously from
     * `session.start()`, before the session reads from its transport. Must not throw (a throw is swallowed).
     */
    started: (session: import('./NexusSession.js').NexusSession) => void;
};
/**
 * Adds an observer of session starts. Returns a function that removes it.
 * @param {SessionObserver} observer
 * @returns {() => void}
 */
export declare function addSessionObserver(observer: SessionObserver): () => void;
/**
 * Tells every observer that `session` started. Internal to the session.
 * @param {import('./NexusSession.js').NexusSession} session
 * @returns {void}
 */
export declare function notifySessionStarted(session: import('./NexusSession.js').NexusSession): void;
