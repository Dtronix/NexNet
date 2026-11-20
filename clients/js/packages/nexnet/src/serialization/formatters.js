/**
 * The built-in formatters (protocol specification §10.4) and the combinators generated code composes them with.
 *
 * Value types (C# structs: numbers, `bool`, `char`, `decimal`, the date and time types, `Guid`, `BigInteger`, tuples)
 * are never nil: reading nil throws (`type`) and writing null throws `TypeError`. Wrap them in
 * {@link nullableFormatter} for C# `T?`. Reference types (`string`, `byte[]`, `Uri`, `Version`, collections) map nil
 * to and from `null`. Writes range-check (`RangeError`) and type-check (`TypeError`) their value.
 * @module
 */

import { serializationError } from './errors.js';
import { roundToHalf } from './half.js';
import { ElementKinds, readPrimitiveArray, writePrimitiveArray } from './primitiveArrays.js';
import { NexusDecimal } from '../values/NexusDecimal.js';
import { MAX_DAY_NUMBER, MAX_TICKS, MAX_TIME_TICKS, NexusDateOnly, NexusDateTime, NexusDateTimeOffset, NexusTimeOnly, NexusTimeSpan } from '../values/time.js';
import { NexusVersion } from '../values/NexusVersion.js';
import { guidFromBytes, guidToBytes } from '../values/guid.js';

/**
 * @template T
 * @typedef {import('./FormatterRegistry.js').Formatter<T>} Formatter
 */

/** @typedef {import('./MsgPackWriter.js').MsgPackWriter} MsgPackWriter */
/** @typedef {import('./MsgPackReader.js').MsgPackReader} MsgPackReader */

/**
 * @template T
 * @param {(writer: MsgPackWriter, value: T) => void} write
 * @param {(reader: MsgPackReader) => T} read
 * @returns {Formatter<T>}
 */
function formatter(write, read) {
  return Object.freeze({ write, read });
}

/**
 * @param {unknown} value
 * @param {number} min
 * @param {number} max
 * @param {string} name
 * @returns {number}
 */
function checkInt(value, min, max, name) {
  if (typeof value !== 'number') throw new TypeError(`${name} must be a number; got ${typeof value}`);
  if (!Number.isInteger(value) || value < min || value > max) throw new RangeError(`${name} must be an integer from ${min} to ${max}; got ${value}`);
  return value;
}

/**
 * @param {unknown} value
 * @param {Function} type
 * @param {string} name
 */
function checkInstance(value, type, name) {
  if (!(value instanceof type)) throw new TypeError(`expected a ${name}; got ${value === null ? 'null' : typeof value}`);
}

/**
 * Re-throws a value-type constructor's RangeError as a `value` decode error.
 * @template T
 * @param {() => T} create
 * @returns {T}
 */
function decodeValue(create) {
  try {
    return create();
  } catch (e) {
    if (e instanceof RangeError || e instanceof SyntaxError) throw serializationError('value', e.message, e);
    throw e;
  }
}

/**
 * Reads the header of a fixed-size bin (nil is refused: the type is a struct).
 * @param {MsgPackReader} reader
 * @param {number} size
 * @param {string} name
 * @returns {Uint8Array}
 */
function readFixedBin(reader, size, name) {
  const code = reader.peekCode();
  const length = reader.readBinHeader();
  if (length < 0) throw serializationError('type', `expected ${name}, found nil (code 0x${code.toString(16)})`);
  if (length !== size) throw serializationError('size', `${name} is ${size} bytes; got ${length}`);
  return reader.readBytes(size);
}

// ------------------------------------------------------------------ Primitives

/** C# `bool`. @type {Formatter<boolean>} */
export const boolFormatter = formatter((w, v) => w.writeBool(v), (r) => r.readBool());

/** C# `sbyte`. @type {Formatter<number>} */
export const sbyteFormatter = formatter((w, v) => w.writeInt(checkInt(v, -0x80, 0x7f, 'sbyte')), (r) => r.readSByte());

/** C# `byte`. @type {Formatter<number>} */
export const byteFormatter = formatter((w, v) => w.writeInt(checkInt(v, 0, 0xff, 'byte')), (r) => r.readByte());

/** C# `short`. @type {Formatter<number>} */
export const int16Formatter = formatter((w, v) => w.writeInt(checkInt(v, -0x8000, 0x7fff, 'short')), (r) => r.readInt16());

/** C# `ushort`. @type {Formatter<number>} */
export const uint16Formatter = formatter((w, v) => w.writeInt(checkInt(v, 0, 0xffff, 'ushort')), (r) => r.readUInt16());

/** C# `int`. @type {Formatter<number>} */
export const int32Formatter = formatter((w, v) => w.writeInt(checkInt(v, -0x80000000, 0x7fffffff, 'int')), (r) => r.readInt32());

/** C# `uint`. @type {Formatter<number>} */
export const uint32Formatter = formatter((w, v) => w.writeInt(checkInt(v, 0, 0xffffffff, 'uint')), (r) => r.readUInt32());

/** C# `long` as `bigint` (the default mapping). @type {Formatter<bigint>} */
export const int64Formatter = formatter((w, v) => w.writeInt64(v), (r) => r.readInt64());

/** C# `ulong` as `bigint` (the default mapping). @type {Formatter<bigint>} */
export const uint64Formatter = formatter((w, v) => w.writeUInt64(v), (r) => r.readUInt64());

/**
 * C# `long` in the opt-in checked-number mapping (`[NexusJsNumber]`): a safe-integer `number`. Reading a value beyond ±(2^53 − 1)
 * throws (`range`).
 * @type {Formatter<number>}
 */
export const int64NumberFormatter = formatter(
  (w, v) => w.writeInt(checkInt(v, Number.MIN_SAFE_INTEGER, Number.MAX_SAFE_INTEGER, 'long (checked number)')),
  (r) => r.readInt(),
);

/**
 * C# `ulong` in the opt-in checked-number mapping (`[NexusJsNumber]`): a non-negative safe-integer `number`.
 * @type {Formatter<number>}
 */
export const uint64NumberFormatter = formatter(
  (w, v) => w.writeInt(checkInt(v, 0, Number.MAX_SAFE_INTEGER, 'ulong (checked number)')),
  (r) => {
    const v = r.readInt();
    if (v < 0) throw serializationError('range', `${v} is outside the ulong range`);
    return v;
  },
);

/** C# `float`: written as float32; any float or integer form reads, rounded to float32. @type {Formatter<number>} */
export const float32Formatter = formatter((w, v) => w.writeFloat32(v), (r) => Math.fround(r.readFloat()));

/** C# `double`: written as float64; any float or integer form reads. @type {Formatter<number>} */
export const float64Formatter = formatter((w, v) => w.writeFloat64(v), (r) => r.readFloat());

/**
 * C# `Half`: a `number`, written as float32 after rounding to the nearest half; any float or integer form reads,
 * rounded to float32 and then to the nearest half (ties to even; beyond the half range, ±infinity).
 * @type {Formatter<number>}
 */
export const halfFormatter = formatter(
  (w, v) => {
    if (typeof v !== 'number') throw new TypeError(`Half must be a number; got ${typeof v}`);
    w.writeFloat32(roundToHalf(v));
  },
  (r) => roundToHalf(Math.fround(r.readFloat())),
);

/** C# `char`: a 1-character string (one UTF-16 code unit, a lone surrogate included). @type {Formatter<string>} */
export const charFormatter = formatter(
  (w, v) => {
    if (typeof v !== 'string' || v.length !== 1) throw new TypeError('char must be a string of one UTF-16 code unit');
    w.writeInt(v.charCodeAt(0));
  },
  (r) => String.fromCharCode(r.readUInt16()),
);

/** C# `string`; nil is null. @type {Formatter<string | null>} */
export const stringFormatter = formatter((w, v) => w.writeString(v ?? null), (r) => r.readString());

/**
 * C# `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>`: bin; nil
 * is null. Reads accept str forms too, and return a new array.
 * @type {Formatter<Uint8Array | null>}
 */
export const binFormatter = formatter((w, v) => w.writeBin(v ?? null), (r) => r.readBin());

/**
 * C# `decimal`: bin of 16 bytes, `GetBits` as four little-endian int32 (§10.6). A wrong length throws (`size`); a
 * scale above 28 or a reserved flag bit throws (`value`).
 * @type {Formatter<NexusDecimal>}
 */
export const decimalFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusDecimal, 'NexusDecimal');
    const bits = v.toBits();
    const bytes = new Uint8Array(16);
    const view = new DataView(bytes.buffer);
    for (let i = 0; i < 4; i++) view.setInt32(i * 4, bits[i], true);
    w.writeBinHeader(16);
    w.writeRaw(bytes);
  },
  (r) => {
    const bytes = readFixedBin(r, 16, 'decimal');
    const view = new DataView(bytes.buffer, bytes.byteOffset, 16);
    return decodeValue(() => NexusDecimal.fromBits(view.getInt32(0, true), view.getInt32(4, true), view.getInt32(8, true), view.getInt32(12, true)));
  },
);

/** C# `DateTime`: the int64 of `ToBinary` (§10.6). @type {Formatter<NexusDateTime>} */
export const dateTimeFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusDateTime, 'NexusDateTime');
    w.writeInt64(v.toBinary());
  },
  (r) => {
    const binary = r.readInt64();
    return decodeValue(() => NexusDateTime.fromBinary(binary));
  },
);

/**
 * C# `DateTimeOffset`: `[UtcTicks: int64, offsetMinutes: int16]` (§10.6). Not a nesting level.
 * @type {Formatter<NexusDateTimeOffset>}
 */
export const dateTimeOffsetFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusDateTimeOffset, 'NexusDateTimeOffset');
    w.writeArrayHeader(2);
    w.writeInt64(v.utcTicks);
    w.writeInt(v.offsetMinutes);
  },
  (r) => {
    r.readArrayHeader(2);
    const utc = r.readInt64();
    const offset = r.readInt16();
    if (utc < 0n || utc > MAX_TICKS) throw serializationError('value', `DateTimeOffset UTC ticks ${utc} out of range`);
    return decodeValue(() => NexusDateTimeOffset.fromUtcTicks(utc, offset));
  },
);

/** C# `TimeSpan`: int64 ticks. @type {Formatter<NexusTimeSpan>} */
export const timeSpanFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusTimeSpan, 'NexusTimeSpan');
    w.writeInt64(v.ticks);
  },
  (r) => new NexusTimeSpan(r.readInt64()),
);

/** C# `DateOnly`: the int32 day number, 0 to 3,652,058. @type {Formatter<NexusDateOnly>} */
export const dateOnlyFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusDateOnly, 'NexusDateOnly');
    w.writeInt(v.dayNumber);
  },
  (r) => {
    const day = r.readInt32();
    if (day < 0 || day > MAX_DAY_NUMBER) throw serializationError('value', `DateOnly day number ${day} out of range`);
    return new NexusDateOnly(day);
  },
);

/** C# `TimeOnly`: int64 ticks since midnight. @type {Formatter<NexusTimeOnly>} */
export const timeOnlyFormatter = formatter(
  (w, v) => {
    checkInstance(v, NexusTimeOnly, 'NexusTimeOnly');
    w.writeInt64(v.ticks);
  },
  (r) => {
    const ticks = r.readInt64();
    if (ticks < 0n || ticks > MAX_TIME_TICKS) throw serializationError('value', `TimeOnly ticks ${ticks} out of range`);
    return new NexusTimeOnly(ticks);
  },
);

/**
 * C# `Guid`: its lower-case "D" string; bin of 16 bytes in RFC 4122 order on the wire (§10.6). Writes accept the
 * "D", "N" and "B" forms in any case.
 * @type {Formatter<string>}
 */
export const guidFormatter = formatter(
  (w, v) => {
    if (typeof v !== 'string') throw new TypeError(`Guid must be a string; got ${typeof v}`);
    w.writeBinHeader(16);
    w.writeRaw(guidToBytes(v));
  },
  (r) => guidFromBytes(readFixedBin(r, 16, 'Guid')),
);

/** C# `BigInteger`: a `bigint`; bin of its minimal little-endian two's complement bytes. @type {Formatter<bigint>} */
export const bigIntegerFormatter = formatter(
  (w, v) => {
    if (typeof v !== 'bigint') throw new TypeError(`BigInteger must be a bigint; got ${typeof v}`);
    const bytes = [];
    let x = v;
    // Minimal: stop once the rest is pure sign extension of the last byte's top bit.
    do {
      const b = Number(x & 0xffn);
      bytes.push(b);
      x >>= 8n;
    } while (!((x === 0n && (bytes[bytes.length - 1] & 0x80) === 0) || (x === -1n && (bytes[bytes.length - 1] & 0x80) !== 0)));
    w.writeBinHeader(bytes.length);
    w.writeRaw(Uint8Array.from(bytes));
  },
  (r) => {
    const code = r.peekCode();
    const length = r.readBinHeader();
    if (length < 0) throw serializationError('type', `expected BigInteger, found nil (code 0x${code.toString(16)})`);
    const bytes = r.readBytes(length);
    let v = 0n;
    for (let i = length - 1; i >= 0; i--) v = (v << 8n) | BigInt(bytes[i]);
    return length > 0 && bytes[length - 1] & 0x80 ? v - (1n << BigInt(8 * length)) : v;
  },
);

/** C# `Uri`: its string (`OriginalString`); nil is null. @type {Formatter<string | null>} */
export const uriFormatter = stringFormatter;

/** C# `Version`: its string, 2 to 4 parts; nil is null. A malformed version throws (`value`). @type {Formatter<NexusVersion | null>} */
export const versionFormatter = formatter(
  (w, v) => {
    if (v === null || v === undefined) {
      w.writeNil();
      return;
    }
    checkInstance(v, NexusVersion, 'NexusVersion');
    w.writeString(v.toString());
  },
  (r) => {
    const text = r.readString();
    return text === null ? null : decodeValue(() => NexusVersion.parse(text));
  },
);

/**
 * One embedded MessagePack value carried as its bytes (message bodies, values with a user formatter). Writes
 * `value` as it is; reads the bytes of one complete value.
 * @type {Formatter<Uint8Array>}
 */
export const rawFormatter = formatter((w, v) => w.writeRaw(v), (r) => r.readRaw());

// ------------------------------------------------------------------ Ext 78 primitive arrays

/**
 * @template T
 * @param {Readonly<import('./primitiveArrays.js').ElementKind>} element
 * @returns {Formatter<T | null>}
 */
function primitiveArray(element) {
  return formatter((w, v) => writePrimitiveArray(w, element, /** @type {any} */ (v ?? null)), (r) => readPrimitiveArray(r, element));
}

/** C# `sbyte[]` (and `List`/`Memory` of it): ext 78. @type {Formatter<Int8Array | null>} */
export const int8ArrayFormatter = primitiveArray(ElementKinds.sbyte);

/** C# `short[]`: ext 78. @type {Formatter<Int16Array | null>} */
export const int16ArrayFormatter = primitiveArray(ElementKinds.short);

/** C# `ushort[]`: ext 78. @type {Formatter<Uint16Array | null>} */
export const uint16ArrayFormatter = primitiveArray(ElementKinds.ushort);

/** C# `int[]`: ext 78. @type {Formatter<Int32Array | null>} */
export const int32ArrayFormatter = primitiveArray(ElementKinds.int);

/** C# `uint[]`: ext 78. @type {Formatter<Uint32Array | null>} */
export const uint32ArrayFormatter = primitiveArray(ElementKinds.uint);

/** C# `long[]`: ext 78. @type {Formatter<BigInt64Array | null>} */
export const int64ArrayFormatter = primitiveArray(ElementKinds.long);

/** C# `ulong[]`: ext 78. @type {Formatter<BigUint64Array | null>} */
export const uint64ArrayFormatter = primitiveArray(ElementKinds.ulong);

/** C# `float[]`: ext 78. @type {Formatter<Float32Array | null>} */
export const float32ArrayFormatter = primitiveArray(ElementKinds.float);

/** C# `double[]`: ext 78. @type {Formatter<Float64Array | null>} */
export const float64ArrayFormatter = primitiveArray(ElementKinds.double);

/** C# `char[]`: ext 78; UTF-16 code units. @type {Formatter<Uint16Array | null>} */
export const charArrayFormatter = primitiveArray(ElementKinds.char);

/** C# `Half[]`: ext 78 of binary16; JS holds the values in a `Float32Array`. @type {Formatter<Float32Array | null>} */
export const halfArrayFormatter = primitiveArray(ElementKinds.half);

// ------------------------------------------------------------------ Combinators

/**
 * C# `T?` of a value type: nil is null. (Reference-type formatters already map nil to null.)
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<T | null>}
 */
export function nullableFormatter(item) {
  return formatter(
    (w, v) => {
      if (v === null || v === undefined) w.writeNil();
      else item.write(w, v);
    },
    (r) => (r.tryReadNil() ? null : item.read(r)),
  );
}

/**
 * @param {unknown} value
 * @param {string} what
 * @returns {readonly any[]}
 */
function asArray(value, what) {
  if (Array.isArray(value)) return value;
  if (value !== null && typeof value === 'object' && Symbol.iterator in value) return Array.from(/** @type {Iterable<any>} */ (value));
  throw new TypeError(`${what} must be an array or an iterable`);
}

/**
 * An array: C# `T[]`, `List<T>` and the list interfaces, `Queue<T>` (front first), `Stack<T>` (top first, index 0)
 * of a non-primitive `T`; nil is null. One nesting level. Writes accept any iterable.
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<T[] | null>}
 */
export function arrayFormatter(item) {
  return formatter(
    (w, v) => {
      if (v === null || v === undefined) {
        w.writeNil();
        return;
      }
      const items = asArray(v, 'a sequence');
      w.writeArrayHeader(items.length);
      for (let i = 0; i < items.length; i++) item.write(w, items[i]);
    },
    (r) => {
      if (r.tryReadNil()) return null;
      r.enter();
      const n = r.readArrayHeader();
      const result = new Array(n);
      for (let i = 0; i < n; i++) result[i] = item.read(r);
      r.exit();
      return result;
    },
  );
}

/**
 * C# `HashSet<T>`: a `Set` (an array on the wire); nil is null. One nesting level. Writes accept any iterable.
 * Set members compare by identity in JS: only primitives (`number`, `string`, `bigint`, `boolean`) dedupe by value.
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<Set<T> | null>}
 */
export function setFormatter(item) {
  return formatter(
    (w, v) => {
      if (v === null || v === undefined) {
        w.writeNil();
        return;
      }
      const items = asArray(v, 'a set');
      w.writeArrayHeader(items.length);
      for (const x of items) item.write(w, x);
    },
    (r) => {
      if (r.tryReadNil()) return null;
      r.enter();
      const n = r.readArrayHeader();
      const result = new Set();
      for (let i = 0; i < n; i++) result.add(item.read(r));
      r.exit();
      return result;
    },
  );
}

/**
 * C# `Dictionary<K,V>` and the dictionary interfaces: a `Map` (never a plain object); nil is null. One nesting
 * level. Writes accept a `Map` or any iterable of `[key, value]` pairs. Keys compare by identity in JS (NEXNET046);
 * a repeated key keeps the last value.
 * @template K, V
 * @param {Formatter<K>} key
 * @param {Formatter<V>} value
 * @returns {Formatter<Map<K, V> | null>}
 */
export function mapFormatter(key, value) {
  return formatter(
    (w, v) => {
      if (v === null || v === undefined) {
        w.writeNil();
        return;
      }
      const entries = v instanceof Map ? [...v] : asArray(v, 'a map');
      w.writeMapHeader(entries.length);
      for (const entry of entries) {
        if (!Array.isArray(entry) || entry.length !== 2) throw new TypeError('a map entry must be [key, value]');
        key.write(w, entry[0]);
        value.write(w, entry[1]);
      }
    },
    (r) => {
      if (r.tryReadNil()) return null;
      r.enter();
      const n = r.readMapHeader();
      const result = new Map();
      for (let i = 0; i < n; i++) {
        const k = key.read(r);
        result.set(k, value.read(r));
      }
      r.exit();
      return result;
    },
  );
}

/**
 * C# `ValueTuple<…>` and `KeyValuePair<K,V>`: a JS array of exactly that many elements (`[a, b]`). A struct: never
 * nil. Wrap it in {@link nullableFormatter} for `Tuple<…>` (a class) and for `(A, B)?`. One nesting level.
 * @param {...Formatter<any>} formatters One formatter per element.
 * @returns {Formatter<any[]>}
 */
export function tupleFormatter(...formatters) {
  const n = formatters.length;
  return formatter(
    (w, v) => {
      if (!Array.isArray(v) || v.length !== n) throw new TypeError(`a tuple of ${n} must be an array of ${n} elements`);
      w.writeArrayHeader(n);
      for (let i = 0; i < n; i++) formatters[i].write(w, v[i]);
    },
    (r) => {
      r.enter();
      r.readArrayHeader(n);
      const result = new Array(n);
      for (let i = 0; i < n; i++) result[i] = formatters[i].read(r);
      r.exit();
      return result;
    },
  );
}

/**
 * C# `KeyValuePair<K,V>`: `[key, value]`.
 * @template K, V
 * @param {Formatter<K>} key
 * @param {Formatter<V>} value
 * @returns {Formatter<[K, V]>}
 */
export function keyValuePairFormatter(key, value) {
  return /** @type {Formatter<[K, V]>} */ (/** @type {unknown} */ (tupleFormatter(key, value)));
}
