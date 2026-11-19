/**
 * Value types for C# primitives that have no exact JS equivalent (01-context §5, D14).
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
