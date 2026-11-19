/**
 * MessagePack codec contract: writer, reader, `Formatter<T>` and the formatter registry.
 * Implemented in `src/serialization/` (J02); this file re-exports it.
 * @module
 */

export { MsgPackWriter, MsgPackReader, FormatterRegistry, tupleFormatter } from '../serialization/index.js';

/**
 * @template T
 * @typedef {import('../serialization/index.js').Formatter<T>} Formatter
 */
/** @typedef {import('../serialization/index.js').SerializerOptions} SerializerOptions */
