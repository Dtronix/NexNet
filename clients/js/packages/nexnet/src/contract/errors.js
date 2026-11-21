/**
 * Error classes and the cancellation mapping. These are defined here, not re-exported; the runtime
 * throws them as they are.
 * @module
 */

/**
 * Base class of every NexNet error.
 */
export class NexusError extends Error {
  /**
   * @param {string} message
   * @param {{ cause?: unknown }} [options]
   */
  constructor(message, options) {
    super(message, options);
    this.name = new.target.name;
  }
}

/**
 * The transport failed (for example the client is not connected). An invocation still pending when the session ends
 * does not reject with this error directly: it rejects as canceled, with a `DOMException` named `AbortError` whose
 * `cause` is a `NexusTransportError` carrying the disconnect `reason` (protocol spec §4.5).
 */
export class NexusTransportError extends NexusError {
  /**
   * @param {string} message
   * @param {{ cause?: unknown, reason?: import('./enums.js').DisconnectReasonValue }} [options]
   */
  constructor(message, options) {
    super(message, options);
    /** Disconnect reason, when the failure is a session disconnect. */
    this.reason = options?.reason ?? 0;
  }
}

/**
 * The remote method threw. Mirrors C# `ProxyRemoteInvocationException`.
 * `remoteType` and `remoteMessage` are set only when the server sends exception detail
 * (`RemoteExceptionDetail.TypeAndMessage`).
 */
export class NexusRemoteInvocationError extends NexusError {
  /**
   * @param {string} message
   * @param {{ cause?: unknown, code?: number, remoteType?: string | null, remoteMessage?: string | null }} [options]
   */
  constructor(message, options) {
    super(message, options);
    /** Wire error code of the invocation result. */
    this.code = options?.code ?? 0;
    /** Remote exception type name, or null when not sent. */
    this.remoteType = options?.remoteType ?? null;
    /** Remote exception message, or null when not sent. */
    this.remoteMessage = options?.remoteMessage ?? null;
  }
}

/**
 * The server rejected the invocation as unauthorized.
 */
export class NexusUnauthorizedError extends NexusError {}

/**
 * The peer refused a pipe, channel or large message because a limit was reached (`MaxPipes`, session budget).
 */
export class NexusBusyError extends NexusError {}

/**
 * A message exceeded the negotiated `MaxMessageSize`.
 */
export class NexusMessageTooLargeError extends NexusError {}

/**
 * Returns the error a cancelled operation rejects with (a call canceled before it is sent rejects and is never sent). This is `signal.reason` when the signal has one,
 * otherwise a `DOMException` named `AbortError`. Invocations pending when the session ends also reject with an
 * `AbortError`, whose `cause` is the `NexusTransportError` with the disconnect reason.
 *
 * @param {AbortSignal} [signal]
 * @returns {unknown}
 */
export function cancellationError(signal) {
  if (signal?.reason !== undefined) return signal.reason;
  return new DOMException('The operation was aborted.', 'AbortError');
}

/**
 * True when `error` is a cancellation: a `DOMException` (or any error) named `AbortError` or `TimeoutError`.
 *
 * @param {unknown} error
 * @returns {boolean}
 */
export function isCancellationError(error) {
  if (typeof error !== 'object' || error === null) return false;
  const name = /** @type {{ name?: unknown }} */ (error).name;
  return name === 'AbortError' || name === 'TimeoutError';
}
