/**
 * Value types for C# primitives that have no exact JS equivalent. They keep the exact C# value (ticks, kind, offset,
 * decimal bits) instead of converting to a lossy native type. Also the Guid helpers.
 * @module
 */
export { NexusDecimal } from './NexusDecimal.js';
export { DateTimeKind, NexusDateTime, NexusDateTimeOffset, NexusTimeSpan, NexusDateOnly, NexusTimeOnly, MAX_TICKS, MAX_DAY_NUMBER, MAX_TIME_TICKS, UNIX_EPOCH_TICKS, } from './time.js';
export { NexusVersion } from './NexusVersion.js';
export { EMPTY_GUID, parseGuid, guidToBytes, guidFromBytes, guidToDotNetBytes, guidFromDotNetBytes } from './guid.js';
export type DateTimeKindValue = import('./time.js').DateTimeKindValue;
/** @typedef {import('./time.js').DateTimeKindValue} DateTimeKindValue */
/**
 * Builds a tuple value. C# `ValueTuple`/`Tuple` map to JS arrays; this helper only fixes the TS tuple type.
 *
 * @template {unknown[]} T
 * @param {T} items
 * @returns {T}
 */
export declare function tuple<T extends unknown[]>(...items: T): T;
