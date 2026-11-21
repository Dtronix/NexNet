/**
 * Client side of a server `INexusList<T>` (protocol spec §12, C# `NexusListClient` and `NexusBroadcastClient`).
 * @module
 */

import { NexusCollectionState } from '../contract/enums.js';
import { NexusError, NexusTransportError, cancellationError } from '../contract/errors.js';
import { getInvocationManager } from '../invocation/InvocationManager.js';
import { NexusChannelReader, NexusChannelWriter } from '../pipes/channels.js';
import { PipeState } from '../pipes/constants.js';
import { pipeCore } from '../pipes/NexusDuplexPipe.js';
import { invokePipe } from '../pipes/invoke.js';
import { createPipe } from '../pipes/PipeManager.js';
import { ListReplica } from './ListReplica.js';
import { ListMessageTag, listMessageFormatter } from './messages.js';
import { NexusCollectionMode } from './mode.js';

/**
 * @template T
 * @typedef {import('./ListReplica.js').NexusListChange<T>} NexusListChange
 */

/**
 * The events of a {@link NexusList}, for `addEventListener`.
 *
 * @template T
 * @typedef {object} NexusListEventMap
 * @property {CustomEvent<NexusListChange<T>>} changed Follows each change.
 */

/**
 * How the runtime builds a list. Supplied by `ProxyBase._collection` from the generated proxy.
 *
 * @template T
 * @typedef {object} NexusListOptions
 * @property {import('./mode.js').NexusCollectionModeValue} [mode] The `[NexusCollection]` mode of the server
 *   nexus property. Default `ServerToClient`, as in C#.
 * @property {(a: T, b: T) => boolean} [equals] Item equality for `remove(item)`, `indexOf` and `contains`
 *   (protocol spec §12.9). Default {@link defaultEquals}.
 * @property {import('../contract/codec.js').Formatter<T[] | null>} [valuesFormatter] The formatter of C# `T[]`, which
 *   reads the values of a reset. Needed when C# writes `T[]` as ext 78 (arrays of primitive numeric types, protocol
 *   spec §10.5). Default: a MessagePack array of items.
 */

/**
 * The default item equality, C# `EqualityComparer<T>.Default` for the JS mappings: an item with an `equals` method
 * (the runtime value types, generated structs and records) uses it; anything else compares with SameValueZero, which
 * is `===` except that NaN equals NaN, as `double.Equals` does. Objects without `equals` compare by identity.
 *
 * @param {unknown} a
 * @param {unknown} b
 * @returns {boolean}
 */
export function defaultEquals(a, b) {
  if (a === b) return true;
  if (a !== null && typeof a === 'object' && typeof (/** @type {{ equals?: unknown }} */ (a)).equals === 'function') {
    return Boolean((/** @type {{ equals(other: unknown): unknown }} */ (a)).equals(b));
  }
  // NaN: the only value not equal to itself.
  return a !== a && b !== b;
}

/** The pipe method's arguments: `[pipeId]` (C# `ValueTuple<byte>`). @type {import('../client/ProxyBase.js').PipeArgumentsWriter} */
const pipeArguments = (writer, pipeId) => {
  writer.writeArrayHeader(1);
  writer.writeInt(pipeId);
};

const noop = () => {};

/**
 * A promise with its settle functions.
 * @template V
 */
class Deferred {
  /** @type {Promise<V>} */
  promise;
  /** @type {(value: V) => void} */
  resolve = noop;
  /** @type {(error: unknown) => void} */
  reject = noop;
  settled = false;

  constructor() {
    this.promise = new Promise((resolve, reject) => {
      this.resolve = (value) => {
        this.settled = true;
        resolve(value);
      };
      this.reject = (error) => {
        this.settled = true;
        reject(error);
      };
    });
    // Nobody may be awaiting it (C# observes a faulted ReadyTask); whoever does still sees the rejection.
    this.promise.catch(noop);
  }
}

/**
 * One enable of the list: its pipe, its replica and its tasks. A closed run never touches the list again, so items
 * still buffered in its pipe cannot reach a later run.
 * @template T
 */
class EnableRun {
  /** @type {import('../pipes/NexusDuplexPipe.js').NexusDuplexPipe} */
  pipe;
  /** @type {ListReplica<T>} */
  replica;
  /** @type {NexusChannelWriter<import('./messages.js').ListMessage<T>> | null} */
  writer = null;
  /** Resolves with true once the first reset completed, false when the pipe closed first. @type {Deferred<boolean>} */
  synced = new Deferred();
  /** The list's `ready` for this run. @type {Deferred<void>} */
  ready = new Deferred();
  /** The list's `disabled` for this run. @type {Deferred<void>} */
  disabled = new Deferred();
  /** The promise `enable` returns. @type {Promise<boolean> | null} */
  enabled = null;
  /** True once the pipe opened (Ready). */
  open = false;
  /** True once the run ended: no item is processed and no request is sent after it. */
  stopped = false;
  /** The mutation lock: requests run one at a time, in call order (C# `OperationLock`). @type {Promise<unknown>} */
  #lock = Promise.resolve();

  /**
   * @param {import('../pipes/NexusDuplexPipe.js').NexusDuplexPipe} pipe
   * @param {ListReplica<T>} replica
   */
  constructor(pipe, replica) {
    this.pipe = pipe;
    this.replica = replica;
  }

  /**
   * Runs `fn` once the requests before it finished.
   * @template R
   * @param {() => Promise<R>} fn
   * @returns {Promise<R>}
   */
  exclusive(fn) {
    const result = this.#lock.then(fn);
    this.#lock = result.catch(noop);
    return result;
  }
}

/**
 * Client side of a server `INexusList<T>`. Get it from the generated proxy's collection property; the same instance
 * serves every session of the client.
 *
 * - **Enable** opens a collection pipe and resolves with true once the server's initial state arrived (`ready`); it
 *   resolves with false when the pipe closed first and rejects when the server refused it (`NexusBusyError`) or the
 *   session is not connected. Collections are not resumed after a reconnect: enable again.
 * - **Reads** (`count`, `get`, `at`, `indexOf`, `contains`, `toArray`, iteration) are local.
 * - **Mutations** (BiDirectional mode only) send one request at a time and resolve with true once the server applied
 *   it, false when the server discarded it (a concurrent change made it obsolete) or the collection closed. The list
 *   changes only when the server's change arrives; there is no optimistic local apply. A mutation made while
 *   `enable` runs waits for the initial state and is then sent with its version; it resolves with false when the enable
 *   fails. They reject with a `NexusError` in ServerToClient and Relay mode and when the list is not enabled, and with
 *   a `RangeError` for a negative index.
 * - A `changed` event (`CustomEvent` with a {@link NexusListChange} `detail`) follows each change.
 *
 * @template T
 */
export class NexusList extends EventTarget {
  /** @type {import('../client/ProxyBase.js').SessionHandle} */
  #handle;
  /** @type {number} */
  #methodId;
  /** @type {import('./mode.js').NexusCollectionModeValue} */
  #mode;
  /** @type {(a: T, b: T) => boolean} */
  #equals;
  /** @type {import('../contract/codec.js').Formatter<import('./messages.js').ListMessage<T>>} */
  #formatter;
  /** The current run, from enable until its pipe closed. @type {EnableRun<T> | null} */
  #run = null;
  /** The replica the reads see: the current run's, or an empty one. @type {ListReplica<T>} */
  #replica;
  /** @type {import('../contract/enums.js').NexusCollectionStateValue} */
  #state = NexusCollectionState.Disconnected;
  /** The last enable's `ready` outcome; resolved before the first enable. @type {Promise<void>} */
  #ready = Promise.resolve();

  /**
   * Created by the runtime (`ProxyBase._collection`).
   * @param {import('../client/ProxyBase.js').SessionHandle} handle
   * @param {number} methodId The collection ID.
   * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
   * @param {NexusListOptions<T>} [options]
   */
  constructor(handle, methodId, formatter, options) {
    super();
    if (!handle || typeof handle !== 'object' || !('session' in handle)) {
      throw new TypeError('Collections are created by the runtime: use the collection property of the server proxy.');
    }
    if (!formatter || typeof formatter.read !== 'function' || typeof formatter.write !== 'function') {
      throw new TypeError('A collection needs an item formatter ({ read, write }).');
    }
    const mode = options?.mode ?? NexusCollectionMode.ServerToClient;
    if (mode !== NexusCollectionMode.ServerToClient && mode !== NexusCollectionMode.BiDirectional && mode !== NexusCollectionMode.Relay) {
      throw new TypeError(`Unsupported collection mode ${mode}.`);
    }
    this.#handle = handle;
    this.#methodId = methodId;
    this.#mode = mode;
    this.#equals = options?.equals ?? defaultEquals;
    this.#formatter = listMessageFormatter(formatter, options?.valuesFormatter);
    this.#replica = this.#newReplica();
  }

  /** The collection mode (`NexusCollectionMode`). @returns {import('./mode.js').NexusCollectionModeValue} */
  get mode() {
    return this.#mode;
  }

  /** Connected once the initial state arrived; Disconnected before and after. @returns {import('../contract/enums.js').NexusCollectionStateValue} */
  get state() {
    return this.#state;
  }

  /** True from `enable` until the collection pipe closed. @returns {boolean} */
  get isEnabled() {
    return this.#run !== null;
  }

  /**
   * Resolves after the initial state arrived. Each `enable` starts a fresh promise; it rejects with a `NexusError`
   * when that enable fails or the collection closes before its first synchronization, and keeps its outcome after a
   * later close. Resolved before the first enable. Mirrors C# `ReadyTask`.
   * @returns {Promise<void>}
   */
  get ready() {
    return this.#ready;
  }

  /** Resolves when the list is disabled or its session ends; resolved while not enabled. @returns {Promise<void>} */
  get disabled() {
    return this.#run?.disabled.promise ?? Promise.resolve();
  }

  /** @returns {number} */
  get count() {
    return this.#replica.items.length;
  }

  /** Same as {@link NexusList#count}. @returns {number} */
  get length() {
    return this.#replica.items.length;
  }

  /** The list version the replica is at (protocol spec §12.3). @returns {number} */
  get version() {
    return this.#replica.version;
  }

  /**
   * The item at `index`. Throws `RangeError` when out of range (C# indexer).
   * @param {number} index
   * @returns {T}
   */
  get(index) {
    const items = this.#replica.items;
    if (!Number.isInteger(index) || index < 0 || index >= items.length) {
      throw new RangeError(`Index ${index} is out of range for ${items.length} items.`);
    }
    return items[index];
  }

  /**
   * The item at `index`, counting back from the end when negative; undefined when out of range (`Array.prototype.at`).
   * @param {number} index
   * @returns {T | undefined}
   */
  at(index) {
    return this.#replica.items.at(index);
  }

  /** @param {T} item @returns {number} */
  indexOf(item) {
    return this.#indexIn(this.#replica.items, item);
  }

  /** @param {T} item @returns {boolean} */
  contains(item) {
    return this.indexOf(item) !== -1;
  }

  /** A copy of the items. @returns {T[]} */
  toArray() {
    return this.#replica.items.slice();
  }

  /** Iterates a snapshot of the items. @returns {Iterator<T>} */
  [Symbol.iterator]() {
    return this.#replica.items[Symbol.iterator]();
  }

  /**
   * Subscribes to the server list (protocol spec §12.1). Resolves with true once the initial state arrived, at once
   * when already enabled (with the in-flight enable's outcome while it runs), and with false when the pipe closed
   * before the initial state. Rejects with `NexusTransportError` when not connected, `NexusBusyError` when a pipe
   * limit refused the pipe or the collection is not authorized, and the abort reason when `signal` aborts before the
   * pipe opened.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<boolean>}
   */
  enable(options) {
    const current = this.#run;
    if (current !== null) return current.enabled ?? Promise.resolve(true);
    const signal = options?.signal;
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    const session = this.#handle.session;
    const invocations = getInvocationManager(session);
    if (session === null || invocations === null || !invocations.isConnected) {
      return Promise.reject(new NexusTransportError('Session is not connected.', { reason: session?.disconnectReason ?? 0 }));
    }
    /** @type {import('../pipes/NexusDuplexPipe.js').NexusDuplexPipe} */
    let pipe;
    try {
      // Rent first: a local refusal (pipe limit or budget) has nothing to undo.
      pipe = createPipe(session);
    } catch (error) {
      return Promise.reject(error);
    }
    const run = new EnableRun(pipe, this.#newReplica());
    this.#run = run;
    this.#replica = run.replica;
    this.#ready = run.ready.promise;
    run.enabled = this.#enableRun(run, invocations, session, signal);
    return run.enabled;
  }

  /**
   * Closes the collection pipe and waits until it closed. The list empties and raises `reset`. Resolves at once when
   * not enabled.
   * @returns {Promise<void>}
   */
  async disable() {
    const run = this.#run;
    if (run === null) return;
    if (run.open) {
      await this.#closePipe(run);
    } else {
      // Still opening: give the pipe back; the enable rejects.
      await this.#releasePipe(run);
    }
    await run.disabled.promise;
  }

  /** Inserts `item` at the end. @param {T} item @returns {Promise<boolean>} */
  add(item) {
    return this.#request((replica) => ({
      tag: ListMessageTag.Insert, flags: 0, version: replica.version, index: replica.items.length, value: item,
    }));
  }

  /** @param {number} index @param {T} item @returns {Promise<boolean>} */
  insert(index, item) {
    return this.#request((replica) => ({ tag: ListMessageTag.Insert, flags: 0, version: replica.version, index, value: item }), index);
  }

  /**
   * Removes the first item equal to `item` (protocol spec §12.9). Resolves with false, sending nothing, when no item is
   * equal.
   * @param {T} item
   * @returns {Promise<boolean>}
   */
  remove(item) {
    return this.#request((replica) => {
      const index = this.#indexIn(replica.items, item);
      return index === -1 ? null : { tag: ListMessageTag.Remove, flags: 0, version: replica.version, index };
    });
  }

  /** @param {number} index @returns {Promise<boolean>} */
  removeAt(index) {
    return this.#request((replica) => ({ tag: ListMessageTag.Remove, flags: 0, version: replica.version, index }), index);
  }

  /** @param {number} index @param {T} value @returns {Promise<boolean>} */
  replace(index, value) {
    return this.#request((replica) => ({ tag: ListMessageTag.Replace, flags: 0, version: replica.version, index, value }), index);
  }

  /** @param {number} fromIndex @param {number} toIndex @returns {Promise<boolean>} */
  move(fromIndex, toIndex) {
    return this.#request(
      (replica) => ({ tag: ListMessageTag.Move, flags: 0, version: replica.version, fromIndex, toIndex }),
      fromIndex,
      toIndex,
    );
  }

  /** @returns {Promise<boolean>} */
  clear() {
    return this.#request((replica) => ({ tag: ListMessageTag.Clear, flags: 0, version: replica.version }));
  }

  // ---------------------------------------------------------------- internals

  /** @returns {ListReplica<T>} */
  #newReplica() {
    /** @type {ListReplica<T>} */
    const replica = new ListReplica({
      emit: (change) => {
        if (this.#replica === replica) this.#emit(change);
      },
      synchronized: () => {
        const run = this.#run;
        if (run === null || run.replica !== replica || run.stopped) return;
        this.#state = NexusCollectionState.Connected;
        run.ready.resolve();
        run.synced.resolve(true);
      },
    });
    return replica;
  }

  /**
   * @param {readonly T[]} items
   * @param {T} item
   * @returns {number}
   */
  #indexIn(items, item) {
    const equals = this.#equals;
    for (let i = 0; i < items.length; i++) if (equals(items[i], item)) return i;
    return -1;
  }

  /**
   * Typed for the events of {@link NexusListEventMap}; any other type takes a plain listener.
   * @template {keyof NexusListEventMap<T>} K
   * @overload
   * @param {K} type
   * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
   * @param {boolean | AddEventListenerOptions} [options]
   * @returns {void}
   */
  /**
   * @overload
   * @param {string} type
   * @param {EventListenerOrEventListenerObject | null} listener
   * @param {boolean | AddEventListenerOptions} [options]
   * @returns {void}
   */
  /**
   * @param {string} type
   * @param {EventListenerOrEventListenerObject | null} listener
   * @param {boolean | AddEventListenerOptions} [options]
   */
  addEventListener(type, listener, options) {
    super.addEventListener(type, listener, options);
  }

  /**
   * Typed like {@link NexusList#addEventListener}.
   * @template {keyof NexusListEventMap<T>} K
   * @overload
   * @param {K} type
   * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
   * @param {boolean | EventListenerOptions} [options]
   * @returns {void}
   */
  /**
   * @overload
   * @param {string} type
   * @param {EventListenerOrEventListenerObject | null} listener
   * @param {boolean | EventListenerOptions} [options]
   * @returns {void}
   */
  /**
   * @param {string} type
   * @param {EventListenerOrEventListenerObject | null} listener
   * @param {boolean | EventListenerOptions} [options]
   */
  removeEventListener(type, listener, options) {
    super.removeEventListener(type, listener, options);
  }

  /** @param {NexusListChange<T>} change */
  #emit(change) {
    this.dispatchEvent(new CustomEvent('changed', { detail: change }));
  }

  /**
   * Opens the pipe, then reads it until it closes.
   * @param {EnableRun<T>} run
   * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations
   * @param {import('../session/NexusSession.js').NexusSession} session
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<boolean>}
   */
  async #enableRun(run, invocations, session, signal) {
    const pipe = run.pipe;
    try {
      this.#log('debug', `Connecting collection ${this.#methodId}.`);
      await invokePipe(invocations, session, this.#methodId, pipe, pipeArguments, signal);
      await whenReady(pipe.ready, signal);
      if (run.stopped) throw new DOMException('The collection was disabled while it opened.', 'AbortError');
    } catch (error) {
      // Refused (a limit or authorization), canceled, disabled, or the session ended before the pipe opened.
      await this.#releasePipe(run);
      this.#endRun(run, new NexusError(`The collection could not be enabled: ${errorMessage(error)}`, { cause: error }));
      throw error;
    }

    run.open = true;
    if (this.#mode === NexusCollectionMode.BiDirectional) run.writer = new NexusChannelWriter(pipe, this.#formatter);
    const onClosed = () => this.#onPipeClosed(run);
    pipe.completed.then(onClosed, onClosed);
    void this.#readLoop(run);
    return run.synced.promise;
  }

  /**
   * Reads the pipe's items into the run's replica until the pipe closes, an item does not decode or the replica
   * reports a protocol error; then closes the pipe (protocol spec §12.8). The session stays up.
   * @param {EnableRun<T>} run
   * @returns {Promise<void>}
   */
  async #readLoop(run) {
    const core = pipeCore(run.pipe);
    const reader = new NexusChannelReader(run.pipe, this.#formatter);
    try {
      for (;;) {
        const result = await reader.read();
        if (result.done) break;
        // The pipe closed: whatever it still buffered is dropped, never applied.
        if (run.stopped || (core.state & PipeState.Complete) === PipeState.Complete) break;
        /** @type {string | null} */
        let error;
        try {
          error = run.replica.process(result.value);
        } catch (thrown) {
          error = `processing failed: ${errorMessage(thrown)}`;
        }
        if (error !== null) {
          this.#log('information', `Collection ${this.#methodId}: protocol error (${error}). Closing the pipe.`);
          break;
        }
      }
    } catch (error) {
      this.#log('information', `Collection ${this.#methodId}: an item could not be read. Closing the pipe.`, error);
    }
    await this.#closePipe(run);
  }

  /** @param {EnableRun<T>} run */
  async #closePipe(run) {
    try {
      await run.pipe.complete();
    } catch (error) {
      this.#log('debug', 'Error while closing the collection pipe.', error);
    }
  }

  /** @param {EnableRun<T>} run */
  async #releasePipe(run) {
    try {
      await run.pipe[Symbol.asyncDispose]();
    } catch (error) {
      this.#log('debug', 'Error while releasing the collection pipe.', error);
    }
  }

  /**
   * The open pipe closed, from either side or with the session (C# `Disconnected`): the list empties, the pending
   * request completes with false, `reset` is raised and the run ends.
   * @param {EnableRun<T>} run
   */
  #onPipeClosed(run) {
    if (run.stopped) return;
    this.#endRun(run, new NexusError('The collection closed before its initial synchronization completed. Enable it again to resynchronize.'));
    void this.#releasePipe(run);
  }

  /**
   * @param {EnableRun<T>} run
   * @param {Error} readyFailure
   */
  #endRun(run, readyFailure) {
    if (run.stopped) return;
    run.stopped = true;
    run.replica.close();
    if (this.#run === run) {
      this.#run = null;
      this.#state = NexusCollectionState.Disconnected;
      this.#replica = this.#newReplica();
      if (run.open) this.#emit({ action: 'reset' });
    }
    if (!run.ready.settled) run.ready.reject(readyFailure);
    if (!run.synced.settled) run.synced.resolve(false);
    run.disabled.resolve();
  }

  /**
   * Sends one request and waits for its answer (protocol spec §12.6).
   * @param {(replica: ListReplica<T>) => import('./messages.js').ListMessage<T> | null} build Builds the request from the
   *   replica's current state, or returns null to resolve with false without sending.
   * @param {...number} indexes Indexes to check before anything is sent.
   * @returns {Promise<boolean>}
   */
  #request(build, ...indexes) {
    for (const index of indexes) {
      if (!Number.isInteger(index) || index < 0 || index > 0x7fffffff) {
        return Promise.reject(new RangeError(`Index ${index} must be a non-negative int32.`));
      }
    }
    if (this.#mode !== NexusCollectionMode.BiDirectional) {
      return Promise.reject(new NexusError('The collection is read-only: only a BiDirectional collection accepts changes from a client.'));
    }
    const run = this.#run;
    if (run === null) return Promise.reject(new NexusError('The collection is not enabled.'));
    return run.exclusive(async () => {
      // A request made while the enable runs waits for the initial state, then is built from it (C# OperationLock
      // and the Java client behave the same). An enable that fails answers it with false.
      if (!(await run.synced.promise)) return false;
      const writer = run.writer;
      if (run.stopped || writer === null) return false;
      const message = build(run.replica);
      if (message === null) return false;
      const answer = run.replica.beginRequest();
      try {
        await writer.write(message);
      } catch (error) {
        this.#log('debug', 'Could not send the collection request.', error);
        run.replica.completePending(false);
      }
      return answer;
    });
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   */
  #log(level, message, error) {
    const logger = this.#handle.client?.config?.logger;
    if (!logger) return;
    try {
      logger.log(level, message, error);
    } catch {
      // A logger must never break the collection.
    }
  }
}

/**
 * Waits for `ready`, rejecting with the abort reason when `signal` aborts first.
 * @param {Promise<void>} ready
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<void>}
 */
function whenReady(ready, signal) {
  if (!signal) return ready;
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(cancellationError(signal));
    if (signal.aborted) return onAbort();
    signal.addEventListener('abort', onAbort, { once: true });
    ready.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort));
  });
}

/** @param {unknown} error @returns {string} */
function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}
