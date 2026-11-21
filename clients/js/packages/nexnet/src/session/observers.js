/**
 * Process-wide observers of session starts, and the per-session hook points the metrics layer uses. Internal to
 * the runtime: the package entries do not export them.
 *
 * The metrics layer learns of every session through {@link addSessionObserver} as the session starts (its transport
 * connected), before any frame is read, so sessions that end during the handshake are counted too. It then attaches a
 * {@link MetricsSink} to the sessions of the clients it tracks; the session, invocation and pipe layers report their
 * event instruments through `session.recordMetric(name, value, tags)`, which costs one empty-array check when nothing
 * is attached.
 * @module
 */

/**
 * Receives the event instruments of one session: `name` is a key of `Instruments` (`src/metrics/instruments.js`),
 * `value` the counter increment or the histogram value (seconds for durations), `tags` the tags beyond the
 * session's role and transport. Called synchronously; must not throw (a throw is swallowed).
 * @callback MetricsSink
 * @param {string} name
 * @param {number} value
 * @param {Readonly<Record<string, string>> | undefined} tags
 * @returns {void}
 */

/**
 * @typedef {object} SessionObserver
 * @property {(session: import('./NexusSession.js').NexusSession) => void} started Called synchronously from
 *   `session.start()`, before the session reads from its transport. Must not throw (a throw is swallowed).
 */

/** @type {Set<SessionObserver>} */
const observers = new Set();

/**
 * Adds an observer of session starts. Returns a function that removes it.
 * @param {SessionObserver} observer
 * @returns {() => void}
 */
export function addSessionObserver(observer) {
  observers.add(observer);
  return () => {
    observers.delete(observer);
  };
}

/**
 * Tells every observer that `session` started. Internal to the session.
 * @param {import('./NexusSession.js').NexusSession} session
 * @returns {void}
 */
export function notifySessionStarted(session) {
  for (const observer of observers) {
    try {
      observer.started(session);
    } catch {
      // An observer must never break the session.
    }
  }
}
