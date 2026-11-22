/**
 * The replica of one enable run of a synchronized list (protocol spec §12.4–§12.6, C# `NexusListClient.OnProcess`):
 * the reset sequence with its change buffer, the stale rule, validation, apply, and the answer to the single pending
 * request. It does no IO; {@link NexusList} feeds it the items read from the collection pipe. Each enable gets a fresh
 * replica, so nothing of a closed pipe can reach the next one.
 * @module
 */

import { CollectionMessageFlags, ListMessageTag } from './messages.js';

/**
 * `detail` of the list's `changed` event. `add`, `remove`, `replace` and `move` mirror C#
 * `NexusCollectionChangedAction`; `reset` is raised for a Clear, a completed reset and a close; `ready` follows the
 * `reset` of the initial synchronization.
 *
 * @template T
 * @typedef {object} NexusListChange
 * @property {'add' | 'remove' | 'replace' | 'move' | 'reset' | 'ready'} action
 * @property {number} [index] add, remove, replace: the index of the item.
 * @property {T} [item] add: the new item; remove: the removed item; replace: the new item; move: the moved item.
 * @property {T} [oldItem] replace: the item that was replaced.
 * @property {number} [fromIndex] move.
 * @property {number} [toIndex] move.
 */

/**
 * @template T
 * @typedef {object} ReplicaHooks
 * @property {(change: NexusListChange<T>) => void} emit Raises a change event.
 * @property {() => void} synchronized The first reset of the run completed (after its `reset` and `ready`).
 */

/** @param {number} flags */
const isAck = (flags) => (flags & CollectionMessageFlags.Ack) !== 0;

/**
 * @template T
 */
export class ListReplica {
  /** The items, in list order. Replaced, never mutated, so a snapshot taken by a reader stays valid. @type {readonly T[]} */
  items = [];
  /** The list version (§12.3). */
  version = 0;
  /** True once the first reset of this run has completed. */
  synchronized = false;
  /** The reset in progress. @type {{ values: T[], total: number, version: number } | null} */
  #resetting = null;
  /** Changes that arrived before or during a reset. @type {import('./messages.js').ListMessage<T>[] | null} */
  #buffer = null;
  /** Answers the request in flight. @type {((applied: boolean) => void) | null} */
  #pending = null;
  /** @type {ReplicaHooks<T>} */
  #hooks;

  /** @param {ReplicaHooks<T>} hooks */
  constructor(hooks) {
    this.#hooks = hooks;
  }

  /** True while a request waits for its answer. */
  get hasPending() {
    return this.#pending !== null;
  }

  /**
   * Registers the single request in flight. The promise resolves with true when its Ack change is applied, and with
   * false on a Noop, an Ack change that fails validation, or a close.
   * @returns {Promise<boolean>}
   */
  beginRequest() {
    if (this.#pending !== null) throw new Error('A collection request is already in flight.');
    return new Promise((resolve) => {
      this.#pending = resolve;
    });
  }

  /**
   * Answers the pending request, if any.
   * @param {boolean} applied
   */
  completePending(applied) {
    const pending = this.#pending;
    this.#pending = null;
    pending?.(applied);
  }

  /**
   * The pipe closed (C# `OnDisconnected`): the list empties, the version returns to 0, a partial reset is dropped and
   * the pending request completes with false. Raises nothing; the caller raises `reset`.
   */
  close() {
    this.items = [];
    this.version = 0;
    this.#resetting = null;
    this.#buffer = null;
    this.synchronized = false;
    this.completePending(false);
  }

  /**
   * Processes one item read from the pipe.
   * @param {import('./messages.js').ListMessage<T>} message
   * @returns {string | null} A protocol error (the caller closes the pipe), or null.
   */
  process(message) {
    switch (message.tag) {
      case ListMessageTag.ResetStart: {
        if (this.#resetting !== null) return 'ResetStart while a reset is in progress';
        const total = /** @type {number} */ (message.totalValues);
        if (total < 0) return `ResetStart with an invalid value count ${total}`;
        this.#resetting = { values: [], total, version: /** @type {number} */ (message.version) };
        // Changes buffered before the reset started are kept; they are replayed after it.
        this.#buffer ??= [];
        return null;
      }
      case ListMessageTag.ResetValues: {
        const resetting = this.#resetting;
        if (resetting === null) return 'ResetValues while not resetting';
        const values = message.values;
        if (values) {
          if (values.length > resetting.total - resetting.values.length) {
            return `more reset values than the ${resetting.total} announced`;
          }
          for (const value of values) resetting.values.push(value);
        }
        return null;
      }
      case ListMessageTag.ResetComplete:
        return this.#completeReset();
      default:
        break;
    }

    if (this.#resetting !== null || !this.synchronized) {
      // Resetting, or the reset has not started yet: replayed after the reset, where the stale rule applies.
      (this.#buffer ??= []).push(message);
      return null;
    }

    if (message.tag === ListMessageTag.Noop) {
      // The server made the pending request obsolete. A Noop with no request pending is ignored.
      this.completePending(false);
      return null;
    }

    const version = /** @type {number} */ (message.version);
    // Stale: already in the snapshot. Dropped without an event; an Ack on it does not answer the request.
    if (version > 0 && version <= this.version) return null;

    const error = this.#apply(message);
    if (error !== null) {
      if (isAck(message.flags)) this.completePending(false);
      return error;
    }
    if (isAck(message.flags)) this.completePending(true);
    return null;
  }

  /** @returns {string | null} */
  #completeReset() {
    const resetting = this.#resetting;
    if (resetting === null) return 'ResetComplete while not resetting';
    if (resetting.values.length !== resetting.total) {
      return `reset completed with ${resetting.values.length} values, but ${resetting.total} were announced`;
    }
    const buffer = this.#buffer ?? [];
    this.items = resetting.values;
    this.version = resetting.version;
    this.#resetting = null;
    this.#buffer = null;
    const first = !this.synchronized;
    this.synchronized = true;
    for (const buffered of buffer) {
      const error = this.process(buffered);
      if (error !== null) return error;
    }
    this.#hooks.emit({ action: 'reset' });
    // C# raises Ready and completes the enable for every reset (InitializationCompleted).
    this.#hooks.emit({ action: 'ready' });
    if (first) this.#hooks.synchronized();
    return null;
  }

  /**
   * Validates and applies a change (C# `VersionedList.ValidateOperation` and `Operation.Apply`), sets the version
   * and raises the change event.
   * @param {import('./messages.js').ListMessage<T>} message
   * @returns {string | null}
   */
  #apply(message) {
    const items = this.items;
    const n = items.length;
    const version = /** @type {number} */ (message.version);
    switch (message.tag) {
      case ListMessageTag.Clear:
        this.items = [];
        this.version = version;
        this.#hooks.emit({ action: 'reset' });
        return null;
      case ListMessageTag.Insert: {
        const index = /** @type {number} */ (message.index);
        if (index < 0 || index > n) return `Insert at ${index} is out of range for ${n} items`;
        const item = /** @type {T} */ (message.value);
        this.items = [...items.slice(0, index), item, ...items.slice(index)];
        this.version = version;
        this.#hooks.emit({ action: 'add', index, item });
        return null;
      }
      case ListMessageTag.Replace: {
        const index = /** @type {number} */ (message.index);
        if (index < 0 || index >= n) return `Replace at ${index} is out of range for ${n} items`;
        const item = /** @type {T} */ (message.value);
        const oldItem = items[index];
        const next = items.slice();
        next[index] = item;
        this.items = next;
        this.version = version;
        this.#hooks.emit({ action: 'replace', index, item, oldItem });
        return null;
      }
      case ListMessageTag.Remove: {
        const index = /** @type {number} */ (message.index);
        if (index < 0 || index >= n) return `Remove at ${index} is out of range for ${n} items`;
        const item = items[index];
        this.items = [...items.slice(0, index), ...items.slice(index + 1)];
        this.version = version;
        this.#hooks.emit({ action: 'remove', index, item });
        return null;
      }
      case ListMessageTag.Move: {
        const fromIndex = /** @type {number} */ (message.fromIndex);
        const toIndex = /** @type {number} */ (message.toIndex);
        if (fromIndex < 0 || fromIndex >= n || toIndex < 0 || toIndex >= n || fromIndex === toIndex) {
          return `Move from ${fromIndex} to ${toIndex} is invalid for ${n} items`;
        }
        const item = items[fromIndex];
        const next = items.slice();
        next.splice(fromIndex, 1);
        next.splice(toIndex, 0, item);
        this.items = next;
        this.version = version;
        this.#hooks.emit({ action: 'move', fromIndex, toIndex, item });
        return null;
      }
      default:
        return `unexpected collection item ${message.tag}`;
    }
  }
}
