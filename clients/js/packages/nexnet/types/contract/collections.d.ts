/**
 * Synchronized collections. Implemented in `src/collections/` (J09); this file re-exports them.
 * @module
 */
export { NexusList } from '../collections/NexusList.js';
export type NexusListChange<T> = import('../collections/ListReplica.js').NexusListChange<T>;
export type NexusListOptions<T> = import('../collections/NexusList.js').NexusListOptions<T>;
/**
 * @template T
 * @typedef {import('../collections/ListReplica.js').NexusListChange<T>} NexusListChange
 */
/**
 * @template T
 * @typedef {import('../collections/NexusList.js').NexusListOptions<T>} NexusListOptions
 */
