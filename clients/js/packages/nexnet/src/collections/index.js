/**
 * Synchronized collections (protocol spec §12). `ProxyBase._collection` resolves the collection properties of
 * generated proxies through {@link proxyCollection}.
 * @module
 */

import { NexusList } from './NexusList.js';

export { NexusList, defaultEquals } from './NexusList.js';
export { NexusCollectionMode } from './mode.js';

/** Collections by proxy, then by collection ID. @type {WeakMap<object, Map<number, NexusList<any>>>} */
const byProxy = new WeakMap();

/**
 * The list behind a collection property of `proxy`: created on first use, then the same instance for the proxy's
 * lifetime (the client's). It follows the client's current session.
 *
 * @template T
 * @param {object} proxy
 * @param {import('../client/ProxyBase.js').SessionHandle} handle
 * @param {number} methodId The collection ID.
 * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
 * @param {import('./NexusList.js').NexusListOptions<T>} [options]
 * @returns {NexusList<T>}
 */
export function proxyCollection(proxy, handle, methodId, formatter, options) {
  let lists = byProxy.get(proxy);
  if (lists === undefined) {
    lists = new Map();
    byProxy.set(proxy, lists);
  }
  let list = lists.get(methodId);
  if (list === undefined) {
    list = new NexusList(handle, methodId, formatter, options);
    lists.set(methodId, list);
  }
  return list;
}
