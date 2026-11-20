/**
 * Value types for C# primitives that have no exact JS equivalent. They keep the exact C# value (ticks, kind, offset,
 * decimal bits) instead of converting to a lossy native type.
 * Implemented in `src/values/` (J02); this file re-exports them.
 * @module
 */

export {
  DateTimeKind,
  NexusDecimal,
  NexusDateTime,
  NexusDateTimeOffset,
  NexusTimeSpan,
  NexusDateOnly,
  NexusTimeOnly,
  NexusVersion,
  tuple,
} from '../values/index.js';

/** @typedef {import('../values/index.js').DateTimeKindValue} DateTimeKindValue */
