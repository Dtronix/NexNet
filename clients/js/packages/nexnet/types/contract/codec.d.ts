/**
 * MessagePack codec contract: writer, reader, `Formatter<T>` and the formatter registry.
 * Implemented in `src/serialization/`; this file re-exports it.
 * @module
 */
export { MsgPackWriter, MsgPackReader, FormatterRegistry, tupleFormatter } from '../serialization/index.js';
export type Formatter<T> = import('../serialization/index.js').Formatter<T>;
export type SerializerOptions = import('../serialization/index.js').SerializerOptions;
/**
 * @template T
 * @typedef {import('../serialization/index.js').Formatter<T>} Formatter
 */
/** @typedef {import('../serialization/index.js').SerializerOptions} SerializerOptions */
