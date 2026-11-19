/**
 * The MessagePack codec: writer, reader, `Formatter<T>`, the built-in formatters and the formatter registry
 * (the formatter API contract of J02; protocol specification §10, §11).
 * @module
 */

export { MsgPackWriter } from './MsgPackWriter.js';
export { MsgPackReader, NexusSerializerOptions, resolveSerializerOptions } from './MsgPackReader.js';
export { NexusSerializationError } from './errors.js';
export { FormatterRegistry } from './FormatterRegistry.js';
export { halfToBits, halfFromBits, roundToHalf } from './half.js';
export { PRIMITIVE_ARRAY_EXT, ElementKinds, readPrimitiveArray, writePrimitiveArray } from './primitiveArrays.js';
export {
  boolFormatter,
  sbyteFormatter,
  byteFormatter,
  int16Formatter,
  uint16Formatter,
  int32Formatter,
  uint32Formatter,
  int64Formatter,
  uint64Formatter,
  int64NumberFormatter,
  uint64NumberFormatter,
  float32Formatter,
  float64Formatter,
  halfFormatter,
  charFormatter,
  stringFormatter,
  binFormatter,
  decimalFormatter,
  dateTimeFormatter,
  dateTimeOffsetFormatter,
  timeSpanFormatter,
  dateOnlyFormatter,
  timeOnlyFormatter,
  guidFormatter,
  bigIntegerFormatter,
  uriFormatter,
  versionFormatter,
  rawFormatter,
  int8ArrayFormatter,
  int16ArrayFormatter,
  uint16ArrayFormatter,
  int32ArrayFormatter,
  uint32ArrayFormatter,
  int64ArrayFormatter,
  uint64ArrayFormatter,
  float32ArrayFormatter,
  float64ArrayFormatter,
  charArrayFormatter,
  halfArrayFormatter,
  nullableFormatter,
  arrayFormatter,
  setFormatter,
  mapFormatter,
  tupleFormatter,
  keyValuePairFormatter,
} from './formatters.js';

/**
 * @template T
 * @typedef {import('./FormatterRegistry.js').Formatter<T>} Formatter
 */
/** @typedef {import('./MsgPackReader.js').SerializerOptions} SerializerOptions */
/** @typedef {import('./MsgPackReader.js').ResolvedSerializerOptions} ResolvedSerializerOptions */
/** @typedef {import('./errors.js').SerializationErrorKind} SerializationErrorKind */
