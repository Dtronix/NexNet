/**
 * Error classes and the cancellation mapping. These are defined here, not re-exported; the runtime
 * throws them as they are.
 * @module
 */
/**
 * Base class of every NexNet error.
 */
export declare class NexusError extends Error {
    /**
     * @param {string} message
     * @param {{ cause?: unknown }} [options]
     */
    constructor(message: string, options?: {
        cause?: unknown;
    });
}
/**
 * The transport failed (for example the client is not connected). An invocation still pending when the session ends
 * does not reject with this error directly: it rejects as canceled, with a `DOMException` named `AbortError` whose
 * `cause` is a `NexusTransportError` carrying the disconnect `reason` (amendment C-2, protocol spec §4.5).
 */
export declare class NexusTransportError extends NexusError {
    /** Disconnect reason, when the failure is a session disconnect. */
    reason: number;
    /**
     * @param {string} message
     * @param {{ cause?: unknown, reason?: import('./enums.js').DisconnectReasonValue }} [options]
     */
    constructor(message: string, options?: {
        cause?: unknown;
        reason?: import('./enums.js').DisconnectReasonValue;
    });
}
/**
 * The remote method threw. Mirrors C# `ProxyRemoteInvocationException`.
 * `remoteType` and `remoteMessage` are set only when the server sends exception detail
 * (`RemoteExceptionDetail.TypeAndMessage`).
 */
export declare class NexusRemoteInvocationError extends NexusError {
    /** Wire error code of the invocation result. */
    code: number;
    /** Remote exception type name, or null when not sent. */
    remoteType: string | null;
    /** Remote exception message, or null when not sent. */
    remoteMessage: string | null;
    /**
     * @param {string} message
     * @param {{ cause?: unknown, code?: number, remoteType?: string | null, remoteMessage?: string | null }} [options]
     */
    constructor(message: string, options?: {
        cause?: unknown;
        code?: number;
        remoteType?: string | null;
        remoteMessage?: string | null;
    });
}
/**
 * The server rejected the invocation as unauthorized.
 */
export declare class NexusUnauthorizedError extends NexusError {
}
/**
 * The peer refused a pipe, channel or large message because a limit was reached (`MaxPipes`, session budget).
 */
export declare class NexusBusyError extends NexusError {
}
/**
 * A message exceeded the negotiated `MaxMessageSize`.
 */
export declare class NexusMessageTooLargeError extends NexusError {
}
/**
 * Returns the error a cancelled operation rejects with (D17). This is `signal.reason` when the signal has one,
 * otherwise a `DOMException` named `AbortError`. Invocations pending when the session ends also reject with an
 * `AbortError`, whose `cause` is the `NexusTransportError` with the disconnect reason (amendment C-2).
 *
 * @param {AbortSignal} [signal]
 * @returns {unknown}
 */
export declare function cancellationError(signal?: AbortSignal): unknown;
/**
 * True when `error` is a cancellation: a `DOMException` (or any error) named `AbortError` or `TimeoutError`.
 *
 * @param {unknown} error
 * @returns {boolean}
 */
export declare function isCancellationError(error: unknown): boolean;
