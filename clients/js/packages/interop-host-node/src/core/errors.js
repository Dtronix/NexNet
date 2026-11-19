/**
 * Error kinds of the control protocol (§8): host errors, the mapping of runtime errors to error objects, and the native
 * errors a script step or a forwarded reply raises for a kind.
 * @module
 */

import {
  NexusError,
  NexusTransportError,
  NexusRemoteInvocationError,
  NexusUnauthorizedError,
  NexusBusyError,
  NexusMessageTooLargeError,
  NexusSerializationError,
  isCancellationError,
} from '@dtronix/nexnet';

/** The message of the C# client for a call on a session that is not connected (kind `Disconnected`). */
export const NOT_CONNECTED = 'Session is not connected';

/** A failure with an explicit kind: harness kinds (`BadRequest`, `NotSupported`, …) or client kinds. */
export class HostError extends Error {
  /**
   * @param {string} kind
   * @param {string | null} message
   * @param {Record<string, unknown>} [detail]
   */
  constructor(kind, message, detail) {
    super(message ?? '');
    this.name = 'HostError';
    /** @readonly */
    this.kind = kind;
    /** Null when the message is absent (a remote invocation error without a message). @readonly */
    this.wireMessage = message;
    /** @readonly */
    this.detail = detail ?? null;
  }
}

/** @param {string} message @returns {HostError} */
export const badRequest = (message) => new HostError('BadRequest', message);
/** @param {string} message @returns {HostError} */
export const notSupported = (message) => new HostError('NotSupported', message);
/** @param {string} message @returns {HostError} */
export const unknownHandle = (message) => new HostError('UnknownHandle', message);
/** @param {string} message @returns {HostError} */
export const invalidOperation = (message) => new HostError('InvalidOperation', message);

/**
 * @typedef {object} WireError
 * @property {string} kind
 * @property {string | null} message
 * @property {Record<string, unknown>} [detail]
 */

/**
 * @param {string} kind
 * @param {string | null} message
 * @param {Record<string, unknown> | null} [detail]
 * @returns {WireError}
 */
function wire(kind, message, detail) {
  /** @type {WireError} */
  const error = { kind, message };
  if (detail) {
    /** @type {Record<string, unknown>} */
    const clean = {};
    for (const [name, value] of Object.entries(detail)) {
      if (value !== undefined && value !== null) clean[name] = value;
    }
    if (Object.keys(clean).length > 0) error.detail = clean;
  }
  return error;
}

/**
 * @param {unknown} error
 * @returns {string}
 */
export function messageOf(error) {
  if (error instanceof Error) return error.message;
  return String(error);
}

/**
 * The error object (§8) of a failure.
 * @param {unknown} error
 * @returns {WireError}
 */
export function toWireError(error) {
  if (error instanceof HostError) return wire(error.kind, error.wireMessage, error.detail);
  const e = /** @type {any} */ (error);
  if (error instanceof NexusUnauthorizedError) return wire('Unauthorized', messageOf(error));
  if (error instanceof NexusRemoteInvocationError) {
    // A code of 0 is the runtime's "none": the JS error cannot tell an absent code from 0.
    return wire('RemoteInvocation', error.remoteMessage ?? null, { type: error.remoteType, code: error.code !== 0 ? error.code : null });
  }
  if (isCancellationError(error)) return wire('Canceled', messageOf(error));
  if (error instanceof NexusMessageTooLargeError) {
    return wire('TooLarge', messageOf(error), { size: typeof e.size === 'number' ? e.size : null, limit: typeof e.limit === 'number' ? e.limit : null });
  }
  if (error instanceof NexusBusyError) return wire('Busy', messageOf(error));
  if (error instanceof NexusSerializationError) return wire('Serialization', messageOf(error));
  if (error instanceof NexusTransportError) {
    if (typeof e.transportError === 'string') return wire('Transport', messageOf(error), { reason: e.transportError });
    return wire('Disconnected', NOT_CONNECTED);
  }
  if (error instanceof RangeError) return wire('ArgumentOutOfRange', messageOf(error), { param: typeof e.param === 'string' ? e.param : null });
  if (error instanceof TypeError) return wire('BadRequest', messageOf(error));
  if (error instanceof NexusError) return wire('InvalidOperation', messageOf(error));
  if (error instanceof Error && error.name === 'NotSupportedError') return wire('NotSupported', error.message);
  return wire('Internal', messageOf(error), { stack: error instanceof Error ? error.stack ?? null : null });
}

/** @type {Map<string, new (message: string) => Error>} */
const nativeClasses = new Map();

/**
 * An Error class whose name (and so the remote type the runtime reports with `TypeAndMessage`) is the C# exception
 * type of a kind.
 * @param {string} typeName
 * @returns {new (message: string) => Error}
 */
function nativeClass(typeName) {
  let cls = nativeClasses.get(typeName);
  if (!cls) {
    const created = class extends Error {
      /** @param {string} message */
      constructor(message) {
        super(message);
        this.name = typeName;
      }
    };
    Object.defineProperty(created, 'name', { value: typeName });
    cls = created;
    nativeClasses.set(typeName, cls);
  }
  return cls;
}

/**
 * The native error of a kind, as a script `throw` step or a forwarded call's error reply raises it (§6.6). The server
 * sees it as a remote exception (the C# exception type name with `RemoteExceptionDetail.TypeAndMessage`); `Canceled`
 * cancels the call.
 * @param {string} kind
 * @param {string | null | undefined} message
 * @returns {unknown}
 */
export function nativeError(kind, message) {
  const text = message ?? '';
  switch (kind) {
    case 'Canceled':
      return new DOMException(text || 'The operation was canceled.', 'AbortError');
    case 'Transport':
      return new (nativeClass('NexNet.Transports.TransportException'))(text);
    case 'RemoteInvocation':
      return new (nativeClass('NexNet.Invocation.ProxyRemoteInvocationException'))(text);
    case 'Unauthorized':
      return new (nativeClass('NexNet.Invocation.ProxyUnauthorizedException'))(text);
    case 'Disconnected':
      return new (nativeClass('System.InvalidOperationException'))(NOT_CONNECTED);
    case 'Serialization':
      return new (nativeClass('NexNet.Serialization.NexusSerializationException'))(text);
    case 'Busy':
      return new (nativeClass('NexNet.NexusBusyException'))(text);
    case 'TooLarge':
      return new (nativeClass('NexNet.NexusMessageTooLargeException'))(text);
    case 'ArgumentOutOfRange':
      return new (nativeClass('System.ArgumentOutOfRangeException'))(text);
    case 'NotSupported':
      return new (nativeClass('System.NotSupportedException'))(text);
    case 'InvalidOperation':
    default:
      return new (nativeClass('System.InvalidOperationException'))(text);
  }
}
