/**
 * Value types for C# primitives that have no exact JS equivalent (design decision D14, docs/internals/multilang-decisions.md).
 * Implemented in `src/values/` (J02); this file re-exports them.
 * @module
 */
export { DateTimeKind, NexusDecimal, NexusDateTime, NexusDateTimeOffset, NexusTimeSpan, NexusDateOnly, NexusTimeOnly, NexusVersion, tuple, } from '../values/index.js';
export type DateTimeKindValue = import('../values/index.js').DateTimeKindValue;
/** @typedef {import('../values/index.js').DateTimeKindValue} DateTimeKindValue */
