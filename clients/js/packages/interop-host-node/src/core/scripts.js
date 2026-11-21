/**
 * The scripts of a client nexus (control protocol §6.6) and the built-in named handlers (§6.9). The reference is the
 * C# `ScriptInterpreter` and `InProcNamedHandlers` (`src/NexNet.Interop`).
 * @module
 */

import { NexusDuplexPipe, NexusDuplexChannel, NexusMessageTooLargeError, cancellationError } from '@dtronix/nexnet';
import { badRequest, nativeError } from './errors.js';
import { pattern } from './encoding.js';

/** The script steps (§6.6). */
const STEPS = new Set(['return', 'throw', 'delay', 'awaitSignal', 'awaitCancel', 'invoke', 'named', 'forward']);

/**
 * What a client's scripts need from the host.
 * @typedef {object} ScriptOwner
 * @property {(ev: string, data: object) => void} event Sends an event about the client.
 * @property {(slot: string, args: readonly unknown[], signal: AbortSignal) => Promise<unknown>} forward Forwards a call
 *   to the runner (§7.3).
 * @property {(method: string, args: readonly unknown[]) => Promise<unknown>} invoke Invokes a server method by name.
 * @property {(callback: () => void, ms: number) => () => void} timer A host timer of the client (counted in the leak
 *   report while it runs); returns its cancel function.
 * @property {AbortSignal} lifetime Aborted when the client is disposed or its scope ends.
 * @property {(name: string, pipe: NexusDuplexPipe) => Promise<void>} [hold] `pipe.hold`: publishes the pipe for
 *   `pipe.accept`; resolves when the runner disposes its handle. Absent: the handler is not supported.
 */

/**
 * Validates the steps of `script.set`.
 * @param {unknown} steps
 * @returns {Record<string, any>[]}
 */
export function validateSteps(steps) {
  if (!Array.isArray(steps)) throw badRequest('script.set needs an array of steps.');
  for (const step of steps) {
    if (typeof step !== 'object' || step === null || typeof step.step !== 'string' || !STEPS.has(step.step)) {
      throw badRequest('Unknown script step ' + JSON.stringify(step) + '.');
    }
  }
  return steps;
}

/**
 * Resolves after `ms` or rejects with a cancellation when `signal` aborts first.
 * @param {ScriptOwner} owner
 * @param {number} ms
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
function delay(owner, ms, signal) {
  if (signal.aborted) return Promise.reject(cancellationError(signal));
  return new Promise((resolve, reject) => {
    const onAbort = () => {
      cancel();
      reject(cancellationError(signal));
    };
    const cancel = owner.timer(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, Math.max(0, ms));
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Resolves when `signal` aborts.
 * @param {AbortSignal} signal
 * @returns {Promise<void>}
 */
function aborted(signal) {
  if (signal.aborted) return Promise.resolve();
  return new Promise((resolve) => signal.addEventListener('abort', () => resolve(), { once: true }));
}

/** The scripts and signals of one client: the `ScriptHost` of its scriptable nexus (J11). */
export class ClientScripts {
  /** @type {ScriptOwner} */
  #owner;
  /** @type {Map<string, { steps: Record<string, any>[], times: number | null, calls: number }>} */
  #slots = new Map();
  /** @type {Set<string>} */
  #raised = new Set();
  /** @type {Map<string, Set<() => void>>} */
  #waiters = new Map();
  /** Slots that are not reported (pooled clients are not scripted). */
  #silent;
  /** @type {{ code: number | null, message: string | null } | null} */
  #invocationError;

  /**
   * @param {ScriptOwner} owner
   * @param {{ silent?: boolean, invocationError?: { code: number | null, message: string | null } | null }} [options]
   *   `invocationError`: the application error of `ClientSpec.onInvocationException` (§6.4.1), or null.
   */
  constructor(owner, options) {
    this.#owner = owner;
    this.#silent = options?.silent === true;
    this.#invocationError = options?.invocationError ?? null;
  }

  /**
   * `ScriptHost.onInvocationException`: the application error the scriptable nexus sends for a failed client nexus
   * invocation (`ClientSpec.onInvocationException`), or null for the exception's own detail.
   * @param {unknown} _error
   * @param {number} _methodId
   * @returns {{ code: number | null, message: string | null } | null}
   */
  onInvocationException(_error, _methodId) {
    const error = this.#invocationError;
    return error ? { code: error.code, message: error.message } : null;
  }

  /**
   * `script.set`.
   * @param {string} slot
   * @param {Record<string, any>[]} steps
   * @param {number | null} times
   */
  set(slot, steps, times) {
    this.#slots.set(slot, { steps, times, calls: 0 });
  }

  /** `script.clear`. @param {string} slot */
  clear(slot) {
    this.#slots.delete(slot);
  }

  /** `signal`: a raised signal stays raised. @param {string} name */
  raise(name) {
    this.#raised.add(name);
    const waiters = this.#waiters.get(name);
    if (waiters) {
      this.#waiters.delete(name);
      for (const resolve of waiters) resolve();
    }
  }

  /**
   * `ScriptHost.call`: reports the call, then runs the slot's steps (§6.6).
   * @param {import('./types.js').ScriptCall} call
   * @returns {unknown} The typed-JSON result, or a promise of it.
   */
  call(call) {
    if (this.#silent) return null;
    this.#owner.event('script.call', { slot: call.slot, args: [...call.args] });
    const state = this.#slots.get(call.slot);
    if (!state) return null;
    let steps = state.steps;
    if (state.times !== null) {
      state.calls++;
      if (state.calls > state.times) steps = [];
    }
    if (steps.length === 0) return null;
    return this.#run(steps, call);
  }

  /**
   * @param {Record<string, any>[]} steps
   * @param {import('./types.js').ScriptCall} call
   * @returns {Promise<unknown>}
   */
  async #run(steps, call) {
    const owner = this.#owner;
    const signal = call.signal ? AbortSignal.any([call.signal, owner.lifetime]) : owner.lifetime;
    for (const step of steps) {
      switch (step.step) {
        case 'return':
          return step.value ?? null;
        case 'throw':
          throw nativeError(String(step.kind), step.message);
        case 'forward':
          return owner.forward(call.slot, [...call.args], signal);
        case 'delay':
          await delay(owner, Number(step.ms) || 0, signal);
          break;
        case 'awaitSignal':
          await this.#awaitSignal(String(step.name), signal);
          break;
        case 'awaitCancel':
          await aborted(signal);
          break;
        case 'invoke': {
          const name = step.method?.name;
          if (typeof name !== 'string') throw nativeError('InvalidOperation', 'The invoke step has no method name.');
          const result = owner.invoke(name, Array.isArray(step.args) ? step.args : []);
          if (step.returnKind === 'Void') result.catch(() => {});
          else await result;
          break;
        }
        case 'named':
          await runNamed(String(step.handler), step.options ?? null, call, signal, owner);
          break;
        default:
          throw nativeError('NotSupported', 'Unknown script step ' + String(step.step) + '.');
      }
    }
    return null;
  }

  /**
   * @param {string} name
   * @param {AbortSignal} signal
   * @returns {Promise<void>}
   */
  #awaitSignal(name, signal) {
    if (this.#raised.has(name)) return Promise.resolve();
    if (signal.aborted) return Promise.reject(cancellationError(signal));
    return new Promise((resolve, reject) => {
      let waiters = this.#waiters.get(name);
      if (!waiters) {
        waiters = new Set();
        this.#waiters.set(name, waiters);
      }
      const onRaised = () => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      };
      const onAbort = () => {
        this.#waiters.get(name)?.delete(onRaised);
        reject(cancellationError(signal));
      };
      waiters.add(onRaised);
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }
}

/** The script host of pooled clients: their client nexus is not scripted (§6.11). */
export const SILENT_SCRIPTS = Object.freeze({ call: () => null });

/**
 * @param {string} handler
 * @returns {Error}
 */
function missingPipe(handler) {
  return /** @type {Error} */ (nativeError('InvalidOperation', 'The handler \'' + handler + '\' needs a method that takes a pipe or channel.'));
}

/**
 * @param {Record<string, any> | null} options
 * @param {string} handler
 * @param {string} name
 * @returns {number | null}
 */
function option(options, handler, name) {
  const value = options?.[name];
  if (value === undefined || value === null) return null;
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw nativeError('InvalidOperation', handler + ': the option \'' + name + '\' is not an integer.');
  }
  if (value < 0) throw nativeError('InvalidOperation', handler + ': the option \'' + name + '\' is negative (' + value + ').');
  return value;
}

/**
 * The item `index` of `channel.pump` for a channel of `expression`, or undefined when the type cannot be pumped.
 * @param {string} expression
 * @param {number} index
 * @param {number} size
 * @returns {unknown}
 */
export function pumpItem(expression, index, size) {
  switch (expression) {
    case 'sbyte':
      return (index << 24) >> 24;
    case 'byte':
      return index & 0xff;
    case 'short':
      return (index << 16) >> 16;
    case 'ushort':
      return index & 0xffff;
    case 'int':
      return index | 0;
    case 'uint':
      return index >>> 0;
    case 'long':
      return BigInt.asIntN(64, BigInt(index));
    case 'ulong':
      return BigInt.asUintN(64, BigInt(index));
    case 'string': {
      let text = '';
      for (let j = 0; j < size; j++) text += String.fromCharCode(97 + ((index + j) % 26));
      return text;
    }
    case 'byte[]':
      return pattern(0, size, index);
    default:
      return undefined;
  }
}

/**
 * True when a channel write failed because the channel is complete (the C# `WriteAsync` returns false), not because
 * of the item.
 * @param {unknown} error
 * @returns {boolean}
 */
export function isChannelClosed(error) {
  return !(error instanceof NexusMessageTooLargeError || error instanceof TypeError || error instanceof RangeError);
}

/**
 * Runs a built-in handler (§6.9) on the pipe or channel argument of the call.
 * @param {string} handler
 * @param {Record<string, any> | null} options
 * @param {import('./types.js').ScriptCall} call
 * @param {AbortSignal} signal
 * @param {ScriptOwner} owner
 * @returns {Promise<void>}
 */
export async function runNamed(handler, options, call, signal, owner) {
  switch (handler) {
    case 'pipe.echo': {
      if (!(call.pipe instanceof NexusDuplexPipe)) throw missingPipe(handler);
      const pipe = call.pipe;
      const reader = pipe.readable.getReader();
      const writer = pipe.writable.getWriter();
      try {
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          await writer.write(value);
        }
      } finally {
        reader.releaseLock();
        writer.releaseLock();
      }
      await pipe.complete();
      return;
    }
    case 'pipe.drain':
    case 'pipe.readExact': {
      if (!(call.pipe instanceof NexusDuplexPipe)) throw missingPipe(handler);
      let length = Number.POSITIVE_INFINITY;
      if (handler === 'pipe.readExact') {
        const value = option(options, handler, 'length');
        if (value === null) throw nativeError('InvalidOperation', 'pipe.readExact needs the option \'length\'.');
        length = value;
      }
      const pipe = call.pipe;
      const reader = pipe.readable.getReader();
      try {
        let read = 0;
        while (read < length) {
          const { value, done } = await reader.read();
          if (done) break;
          read += value.length;
        }
      } finally {
        reader.releaseLock();
      }
      await pipe.complete();
      return;
    }
    case 'pipe.hold': {
      if (!(call.pipe instanceof NexusDuplexPipe)) throw missingPipe(handler);
      const name = options?.name;
      if (typeof name !== 'string' || name.length === 0) throw nativeError('InvalidOperation', 'pipe.hold needs the option \'name\'.');
      if (!owner.hold) throw nativeError('NotSupported', 'This client cannot hold pipes.');
      // Ignores the call's cancellation (a disconnect): the runner still drives the pipe; ends with the client.
      await Promise.race([owner.hold(name, call.pipe), aborted(owner.lifetime)]);
      return;
    }
    case 'channel.echo': {
      if (!(call.pipe instanceof NexusDuplexChannel)) throw missingPipe(handler);
      const reader = await call.pipe.getReader();
      const writer = await call.pipe.getWriter();
      for await (const item of reader) await writer.write(item);
      await writer.complete();
      return;
    }
    case 'channel.pump': {
      const count = option(options, handler, 'count');
      if (count === null) throw nativeError('InvalidOperation', 'channel.pump needs the option \'count\'.');
      const size = option(options, handler, 'size') ?? 0;
      const delayMs = option(options, handler, 'delayMs') ?? 0;
      if (!(call.pipe instanceof NexusDuplexChannel)) throw missingPipe(handler);
      const expression = call.channelItem?.expression ?? '';
      if (pumpItem(expression, 0, 0) === undefined) {
        throw nativeError('NotSupported', 'channel.pump cannot make items of type ' + expression + ' (integer types, string and byte[] only).');
      }
      const writer = await call.pipe.getWriter();
      for (let i = 0; i < count; i++) {
        try {
          await writer.write(pumpItem(expression, i, size), { signal });
        } catch (error) {
          if (signal.aborted) throw error;
          if (!isChannelClosed(error)) throw error;
          break; // The server completed the channel; there is nobody left to write to.
        }
        if (delayMs > 0 && i < count - 1) await delay(owner, delayMs, signal);
      }
      await writer.complete();
      return;
    }
    default:
      throw nativeError('NotSupported', 'This host has no built-in handler \'' + handler + '\' (pipe.echo, pipe.drain, pipe.readExact, pipe.hold, channel.echo, channel.pump).');
  }
}
