/**
 * Synchronized collections. J09 implements them.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * Client side of a server `INexusList<T>`. Dispatches a `changed` event (`CustomEvent`) after each change.
 * Mutations resolve to false when the server rejected them (for example a stale version).
 *
 * @template T
 */
export class NexusList extends EventTarget {
  /** @returns {import('./enums.js').NexusCollectionStateValue} */
  get state() { throw notImplemented('NexusList.state'); }

  /** @returns {boolean} */
  get isEnabled() { throw notImplemented('NexusList.isEnabled'); }

  /** Resolves after the initial state arrived. @returns {Promise<void>} */
  get ready() { throw notImplemented('NexusList.ready'); }

  /** Resolves when the list is disabled or its session ends. @returns {Promise<void>} */
  get disabled() { throw notImplemented('NexusList.disabled'); }

  /** @returns {number} */
  get count() { throw notImplemented('NexusList.count'); }

  /** @param {number} index @returns {T} */
  get(index) { throw notImplemented('NexusList.get'); }

  /** @param {T} item @returns {number} */
  indexOf(item) { throw notImplemented('NexusList.indexOf'); }

  /** @param {T} item @returns {boolean} */
  contains(item) { throw notImplemented('NexusList.contains'); }

  /** @returns {T[]} */
  toArray() { throw notImplemented('NexusList.toArray'); }

  /** @returns {Iterator<T>} */
  [Symbol.iterator]() { throw notImplemented('NexusList[Symbol.iterator]'); }

  /** Subscribes to the server list. @param {{ signal?: AbortSignal }} [options] @returns {Promise<boolean>} */
  enable(options) { throw notImplemented('NexusList.enable'); }

  /** @returns {Promise<void>} */
  disable() { throw notImplemented('NexusList.disable'); }

  /** @param {T} item @returns {Promise<boolean>} */
  add(item) { throw notImplemented('NexusList.add'); }

  /** @param {number} index @param {T} item @returns {Promise<boolean>} */
  insert(index, item) { throw notImplemented('NexusList.insert'); }

  /** @param {T} item @returns {Promise<boolean>} */
  remove(item) { throw notImplemented('NexusList.remove'); }

  /** @param {number} index @returns {Promise<boolean>} */
  removeAt(index) { throw notImplemented('NexusList.removeAt'); }

  /** @param {number} index @param {T} value @returns {Promise<boolean>} */
  replace(index, value) { throw notImplemented('NexusList.replace'); }

  /** @param {number} fromIndex @param {number} toIndex @returns {Promise<boolean>} */
  move(fromIndex, toIndex) { throw notImplemented('NexusList.move'); }

  /** @returns {Promise<boolean>} */
  clear() { throw notImplemented('NexusList.clear'); }
}
