/**
 * Ext 78 primitive arrays (protocol specification §10.5): one element-kind byte, then the elements' raw little-endian
 * bytes. JS uses typed arrays; a read copies the data into a new, aligned buffer.
 * @module
 */

import { serializationError } from './errors.js';
import { halfFromBits, halfToBits } from './half.js';

/** The ext type of a primitive array. */
export const PRIMITIVE_ARRAY_EXT = 78;

/** Whether the platform is little-endian: typed-array bytes are then the wire bytes. */
const LITTLE_ENDIAN = new Uint8Array(new Uint16Array([1]).buffer)[0] === 1;

/** The one-byte arrays of each kind byte. */
const KIND_BYTES = Array.from({ length: 12 }, (_, kind) => Uint8Array.of(kind));

/**
 * @typedef {Int8Array | Int16Array | Uint16Array | Int32Array | Uint32Array | BigInt64Array | BigUint64Array | Float32Array | Float64Array} PrimitiveTypedArray
 */

/**
 * One element kind.
 * @typedef {object} ElementKind
 * @property {number} kind The kind byte.
 * @property {number} size Bytes per element on the wire.
 * @property {string} name C# element type.
 * @property {any} array The typed-array constructor JS uses.
 * @property {boolean} [half] Elements are binary16 on the wire, float32 in JS.
 * @property {boolean} [bigint] Elements are bigints.
 */

/** The element kinds, by C# element type (§10.5). @type {Readonly<Record<string, Readonly<ElementKind>>>} */
export const ElementKinds = Object.freeze({
  sbyte: Object.freeze({ kind: 1, size: 1, name: 'sbyte', array: Int8Array }),
  short: Object.freeze({ kind: 2, size: 2, name: 'short', array: Int16Array }),
  ushort: Object.freeze({ kind: 3, size: 2, name: 'ushort', array: Uint16Array }),
  int: Object.freeze({ kind: 4, size: 4, name: 'int', array: Int32Array }),
  uint: Object.freeze({ kind: 5, size: 4, name: 'uint', array: Uint32Array }),
  long: Object.freeze({ kind: 6, size: 8, name: 'long', array: BigInt64Array, bigint: true }),
  ulong: Object.freeze({ kind: 7, size: 8, name: 'ulong', array: BigUint64Array, bigint: true }),
  float: Object.freeze({ kind: 8, size: 4, name: 'float', array: Float32Array }),
  double: Object.freeze({ kind: 9, size: 8, name: 'double', array: Float64Array }),
  char: Object.freeze({ kind: 10, size: 2, name: 'char', array: Uint16Array }),
  half: Object.freeze({ kind: 11, size: 2, name: 'half', array: Float32Array, half: true }),
});

/**
 * Writes `value` as an ext 78 array of `element`, or nil for null. `value` is the element's typed array or any
 * array-like of its values; typed-array conversion applies (wrapping, rounding).
 * @param {import('./MsgPackWriter.js').MsgPackWriter} writer
 * @param {Readonly<ElementKind>} element
 * @param {ArrayLike<number> | ArrayLike<bigint> | null} value
 * @returns {void}
 */
export function writePrimitiveArray(writer, element, value) {
  if (value === null) {
    writer.writeNil();
    return;
  }
  if (typeof value !== 'object' || typeof value.length !== 'number') throw new TypeError(`expected an array of ${element.name}`);
  const n = value.length;
  writer.writeExtHeader(PRIMITIVE_ARRAY_EXT, 1 + n * element.size);
  if (element.half) {
    const out = new Uint8Array(1 + n * 2);
    out[0] = element.kind;
    const view = new DataView(out.buffer);
    for (let i = 0; i < n; i++) view.setUint16(1 + i * 2, halfToBits(Number(value[i])), true);
    writer.writeRaw(out);
    return;
  }
  /** @type {PrimitiveTypedArray} */
  const typed = value instanceof element.array ? /** @type {any} */ (value) : element.array.from(/** @type {any} */ (value));
  const bytes = new Uint8Array(typed.buffer, typed.byteOffset, typed.byteLength);
  writer.writeRaw(KIND_BYTES[element.kind]);
  if (LITTLE_ENDIAN || element.size === 1) {
    writer.writeRaw(bytes);
    return;
  }
  const out = new Uint8Array(bytes.length);
  for (let i = 0; i < n; i++) {
    for (let b = 0; b < element.size; b++) out[i * element.size + b] = bytes[i * element.size + element.size - 1 - b];
  }
  writer.writeRaw(out);
}

/**
 * Reads an ext 78 array of `element` into a new typed array, or null for nil.
 * @param {import('./MsgPackReader.js').MsgPackReader} reader
 * @param {Readonly<ElementKind>} element
 * @returns {any} The element's typed array, or null.
 */
export function readPrimitiveArray(reader, element) {
  if (reader.tryReadNil()) return null;
  const { type, length } = reader.readExtHeader();
  if (type !== PRIMITIVE_ARRAY_EXT) throw serializationError('type', `expected ext ${PRIMITIVE_ARRAY_EXT}, found ext ${type}`);
  if (length < 1) throw serializationError('size', 'a primitive array without its kind byte');
  const data = reader.readBytes(length);
  if (data[0] !== element.kind) {
    throw serializationError('type', `a primitive array of kind ${data[0]} where ${element.name} (kind ${element.kind}) is expected`);
  }
  if ((length - 1) % element.size !== 0) {
    throw serializationError('size', `${length - 1} data bytes are not a whole number of ${element.size}-byte elements`);
  }
  const n = (length - 1) / element.size;
  if (element.half) {
    const view = new DataView(data.buffer, data.byteOffset + 1, length - 1);
    const result = new Float32Array(n);
    for (let i = 0; i < n; i++) result[i] = halfFromBits(view.getUint16(i * 2, true));
    return result;
  }
  // `data` is a new array; copy the elements to offset 0 of their own buffer for alignment.
  const aligned = new Uint8Array(length - 1);
  if (LITTLE_ENDIAN || element.size === 1) {
    aligned.set(data.subarray(1));
  } else {
    for (let i = 0; i < n; i++) {
      for (let b = 0; b < element.size; b++) aligned[i * element.size + b] = data[1 + i * element.size + element.size - 1 - b];
    }
  }
  return new element.array(aligned.buffer);
}
