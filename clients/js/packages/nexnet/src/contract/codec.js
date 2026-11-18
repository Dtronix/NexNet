/**
 * MessagePack codec contract: writer, reader, `Formatter<T>` and the formatter registry.
 * J02 implements these in `src/serialization/` and finalizes the formatter API.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * Reads and writes one value of type `T`. Generated code emits one formatter per type; the runtime ships the
 * built-in ones. A formatter is a plain object; it holds no per-call state.
 *
 * @template T
 * @typedef {object} Formatter
 * @property {(writer: MsgPackWriter, value: T) => void} write Writes `value`. Throws `RangeError` on an out-of-range value.
 * @property {(reader: MsgPackReader) => T} read Reads one value.
 */

/**
 * Reader hardening, mirroring C# `NexusSerializerOptions`.
 *
 * @typedef {object} SerializerOptions
 * @property {'trusted' | 'untrusted'} [preset] Preset limits; explicit fields override it. Default `'untrusted'`.
 * @property {number} [maxDepth] Maximum nesting depth of arrays and maps.
 * @property {number} [maxElements] Maximum element count of one array, map, string or binary.
 * @property {boolean} [strictUtf8] Reject invalid UTF-8 instead of replacing it.
 */

/**
 * Appends MessagePack to a growable buffer, always choosing the smallest (canonical) encoding.
 * Multi-byte MessagePack values are big-endian.
 */
export class MsgPackWriter {
  /**
   * @param {{ initialCapacity?: number }} [options]
   */
  constructor(options) {
    void options;
  }

  /** Number of bytes written so far. @returns {number} */
  get length() { throw notImplemented('MsgPackWriter.length'); }

  /** Writes nil. @returns {void} */
  writeNil() { throw notImplemented('MsgPackWriter.writeNil'); }

  /** @param {boolean} value @returns {void} */
  writeBool(value) { throw notImplemented('MsgPackWriter.writeBool'); }

  /**
   * Writes an integer that fits in a JS `number` (C# sbyte through uint, and checked-number long/ulong).
   * Throws `RangeError` when `value` is not a safe integer.
   * @param {number} value @returns {void}
   */
  writeInt(value) { throw notImplemented('MsgPackWriter.writeInt'); }

  /** Writes a signed 64-bit integer. @param {bigint} value @returns {void} */
  writeInt64(value) { throw notImplemented('MsgPackWriter.writeInt64'); }

  /** Writes an unsigned 64-bit integer. @param {bigint} value @returns {void} */
  writeUInt64(value) { throw notImplemented('MsgPackWriter.writeUInt64'); }

  /** Writes float32 (C# float/Half). @param {number} value @returns {void} */
  writeFloat32(value) { throw notImplemented('MsgPackWriter.writeFloat32'); }

  /** Writes float64 (C# double). @param {number} value @returns {void} */
  writeFloat64(value) { throw notImplemented('MsgPackWriter.writeFloat64'); }

  /** Writes a UTF-8 string, or nil for null. @param {string | null} value @returns {void} */
  writeString(value) { throw notImplemented('MsgPackWriter.writeString'); }

  /** Writes bin, or nil for null. @param {Uint8Array | null} value @returns {void} */
  writeBin(value) { throw notImplemented('MsgPackWriter.writeBin'); }

  /** @param {number} count @returns {void} */
  writeArrayHeader(count) { throw notImplemented('MsgPackWriter.writeArrayHeader'); }

  /** @param {number} count @returns {void} */
  writeMapHeader(count) { throw notImplemented('MsgPackWriter.writeMapHeader'); }

  /** Writes an ext value. @param {number} type @param {Uint8Array} data @returns {void} */
  writeExt(type, data) { throw notImplemented('MsgPackWriter.writeExt'); }

  /** Appends bytes that are already MessagePack. @param {Uint8Array} bytes @returns {void} */
  writeRaw(bytes) { throw notImplemented('MsgPackWriter.writeRaw'); }

  /** Returns a view of the bytes written so far (valid until the next write or reset). @returns {Uint8Array} */
  toUint8Array() { throw notImplemented('MsgPackWriter.toUint8Array'); }

  /** Clears the buffer for reuse. @returns {void} */
  reset() { throw notImplemented('MsgPackWriter.reset'); }
}

/**
 * Reads MessagePack from one buffer or a list of segments, enforcing {@link SerializerOptions}.
 */
export class MsgPackReader {
  /**
   * @param {Uint8Array | readonly Uint8Array[]} source
   * @param {SerializerOptions} [options]
   */
  constructor(source, options) {
    void source;
    void options;
  }

  /** Bytes not read yet. @returns {number} */
  get remaining() { throw notImplemented('MsgPackReader.remaining'); }

  /** Consumes a nil and returns true, or returns false and consumes nothing. @returns {boolean} */
  tryReadNil() { throw notImplemented('MsgPackReader.tryReadNil'); }

  /** @returns {boolean} */
  readBool() { throw notImplemented('MsgPackReader.readBool'); }

  /** Reads an integer as a `number`; throws `RangeError` when it is not a safe integer. @returns {number} */
  readInt() { throw notImplemented('MsgPackReader.readInt'); }

  /** @returns {bigint} */
  readInt64() { throw notImplemented('MsgPackReader.readInt64'); }

  /** @returns {bigint} */
  readUInt64() { throw notImplemented('MsgPackReader.readUInt64'); }

  /** Reads float32 or float64. @returns {number} */
  readFloat() { throw notImplemented('MsgPackReader.readFloat'); }

  /** @returns {string | null} */
  readString() { throw notImplemented('MsgPackReader.readString'); }

  /** @returns {Uint8Array | null} */
  readBin() { throw notImplemented('MsgPackReader.readBin'); }

  /** Reads an array header and returns its element count. @returns {number} */
  readArrayHeader() { throw notImplemented('MsgPackReader.readArrayHeader'); }

  /** Reads a map header and returns its entry count. @returns {number} */
  readMapHeader() { throw notImplemented('MsgPackReader.readMapHeader'); }

  /** @returns {{ type: number, data: Uint8Array }} */
  readExt() { throw notImplemented('MsgPackReader.readExt'); }

  /** Skips one complete value. @returns {void} */
  skip() { throw notImplemented('MsgPackReader.skip'); }
}

/**
 * Formatters by key. Generated code registers user target formatters (D12) and generic instantiations here;
 * the runtime looks up formatters it does not get passed directly.
 */
export class FormatterRegistry {
  /**
   * Registers `formatter` under `key`. Throws when the key is already registered.
   * @template T
   * @param {string} key
   * @param {Formatter<T>} formatter
   * @returns {void}
   */
  register(key, formatter) { throw notImplemented('FormatterRegistry.register'); }

  /**
   * Returns the formatter for `key`; throws when there is none.
   * @param {string} key
   * @returns {Formatter<any>}
   */
  get(key) { throw notImplemented('FormatterRegistry.get'); }

  /**
   * Returns the formatter for `key`, or undefined.
   * @param {string} key
   * @returns {Formatter<any> | undefined}
   */
  tryGet(key) { throw notImplemented('FormatterRegistry.tryGet'); }
}

/**
 * Builds the formatter of a C# `ValueTuple`/`Tuple`, which JS represents as an array (`[a, b]`).
 *
 * @param {...Formatter<any>} formatters One formatter per element.
 * @returns {Formatter<any[]>}
 */
export function tupleFormatter(...formatters) {
  throw notImplemented('tupleFormatter');
}
