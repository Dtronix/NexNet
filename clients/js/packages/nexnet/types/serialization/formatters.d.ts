/**
 * The built-in formatters (protocol specification §10.4) and the combinators generated code composes them with.
 *
 * Value types (C# structs: numbers, `bool`, `char`, `decimal`, the date and time types, `Guid`, `BigInteger`, tuples)
 * are never nil: reading nil throws (`type`) and writing null throws `TypeError`. Wrap them in
 * {@link nullableFormatter} for C# `T?`. Reference types (`string`, `byte[]`, `Uri`, `Version`, collections) map nil
 * to and from `null`. Writes range-check (`RangeError`) and type-check (`TypeError`) their value.
 * @module
 */
import { NexusDecimal } from '../values/NexusDecimal.js';
import { NexusDateOnly, NexusDateTime, NexusDateTimeOffset, NexusTimeOnly, NexusTimeSpan } from '../values/time.js';
import { NexusVersion } from '../values/NexusVersion.js';
export type Formatter<T> = import('./FormatterRegistry.js').Formatter<T>;
export type MsgPackWriter = import('./MsgPackWriter.js').MsgPackWriter;
export type MsgPackReader = import('./MsgPackReader.js').MsgPackReader;
/** C# `bool`. @type {Formatter<boolean>} */
export declare const boolFormatter: Formatter<boolean>;
/** C# `sbyte`. @type {Formatter<number>} */
export declare const sbyteFormatter: Formatter<number>;
/** C# `byte`. @type {Formatter<number>} */
export declare const byteFormatter: Formatter<number>;
/** C# `short`. @type {Formatter<number>} */
export declare const int16Formatter: Formatter<number>;
/** C# `ushort`. @type {Formatter<number>} */
export declare const uint16Formatter: Formatter<number>;
/** C# `int`. @type {Formatter<number>} */
export declare const int32Formatter: Formatter<number>;
/** C# `uint`. @type {Formatter<number>} */
export declare const uint32Formatter: Formatter<number>;
/** C# `long` as `bigint` (the default mapping). @type {Formatter<bigint>} */
export declare const int64Formatter: Formatter<bigint>;
/** C# `ulong` as `bigint` (the default mapping). @type {Formatter<bigint>} */
export declare const uint64Formatter: Formatter<bigint>;
/**
 * C# `long` in the opt-in checked-number mapping (`[NexusJsNumber]`): a safe-integer `number`. Reading a value beyond ±(2^53 − 1)
 * throws (`range`).
 * @type {Formatter<number>}
 */
export declare const int64NumberFormatter: Formatter<number>;
/**
 * C# `ulong` in the opt-in checked-number mapping (`[NexusJsNumber]`): a non-negative safe-integer `number`.
 * @type {Formatter<number>}
 */
export declare const uint64NumberFormatter: Formatter<number>;
/** C# `float`: written as float32; any float or integer form reads, rounded to float32. @type {Formatter<number>} */
export declare const float32Formatter: Formatter<number>;
/** C# `double`: written as float64; any float or integer form reads. @type {Formatter<number>} */
export declare const float64Formatter: Formatter<number>;
/**
 * C# `Half`: a `number`, written as float32 after rounding to the nearest half; any float or integer form reads,
 * rounded to float32 and then to the nearest half (ties to even; beyond the half range, ±infinity).
 * @type {Formatter<number>}
 */
export declare const halfFormatter: Formatter<number>;
/** C# `char`: a 1-character string (one UTF-16 code unit, a lone surrogate included). @type {Formatter<string>} */
export declare const charFormatter: Formatter<string>;
/** C# `string`; nil is null. @type {Formatter<string | null>} */
export declare const stringFormatter: Formatter<string | null>;
/**
 * C# `byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, `ArraySegment<byte>`: bin; nil
 * is null. Reads accept str forms too, and return a new array.
 * @type {Formatter<Uint8Array | null>}
 */
export declare const binFormatter: Formatter<Uint8Array | null>;
/**
 * C# `decimal`: bin of 16 bytes, `GetBits` as four little-endian int32 (§10.6). A wrong length throws (`size`); a
 * scale above 28 or a reserved flag bit throws (`value`).
 * @type {Formatter<NexusDecimal>}
 */
export declare const decimalFormatter: Formatter<NexusDecimal>;
/** C# `DateTime`: the int64 of `ToBinary` (§10.6). @type {Formatter<NexusDateTime>} */
export declare const dateTimeFormatter: Formatter<NexusDateTime>;
/**
 * C# `DateTimeOffset`: `[UtcTicks: int64, offsetMinutes: int16]` (§10.6). Not a nesting level.
 * @type {Formatter<NexusDateTimeOffset>}
 */
export declare const dateTimeOffsetFormatter: Formatter<NexusDateTimeOffset>;
/** C# `TimeSpan`: int64 ticks. @type {Formatter<NexusTimeSpan>} */
export declare const timeSpanFormatter: Formatter<NexusTimeSpan>;
/** C# `DateOnly`: the int32 day number, 0 to 3,652,058. @type {Formatter<NexusDateOnly>} */
export declare const dateOnlyFormatter: Formatter<NexusDateOnly>;
/** C# `TimeOnly`: int64 ticks since midnight. @type {Formatter<NexusTimeOnly>} */
export declare const timeOnlyFormatter: Formatter<NexusTimeOnly>;
/**
 * C# `Guid`: its lower-case "D" string; bin of 16 bytes in RFC 4122 order on the wire (§10.6). Writes accept the
 * "D", "N" and "B" forms in any case.
 * @type {Formatter<string>}
 */
export declare const guidFormatter: Formatter<string>;
/** C# `BigInteger`: a `bigint`; bin of its minimal little-endian two's complement bytes. @type {Formatter<bigint>} */
export declare const bigIntegerFormatter: Formatter<bigint>;
/** C# `Uri`: its string (`OriginalString`); nil is null. @type {Formatter<string | null>} */
export declare const uriFormatter: Formatter<string | null>;
/** C# `Version`: its string, 2 to 4 parts; nil is null. A malformed version throws (`value`). @type {Formatter<NexusVersion | null>} */
export declare const versionFormatter: Formatter<NexusVersion | null>;
/**
 * One embedded MessagePack value carried as its bytes (message bodies, values with a user formatter). Writes
 * `value` as it is; reads the bytes of one complete value.
 * @type {Formatter<Uint8Array>}
 */
export declare const rawFormatter: Formatter<Uint8Array>;
/** C# `sbyte[]` (and `List`/`Memory` of it): ext 78. @type {Formatter<Int8Array | null>} */
export declare const int8ArrayFormatter: Formatter<Int8Array | null>;
/** C# `short[]`: ext 78. @type {Formatter<Int16Array | null>} */
export declare const int16ArrayFormatter: Formatter<Int16Array | null>;
/** C# `ushort[]`: ext 78. @type {Formatter<Uint16Array | null>} */
export declare const uint16ArrayFormatter: Formatter<Uint16Array | null>;
/** C# `int[]`: ext 78. @type {Formatter<Int32Array | null>} */
export declare const int32ArrayFormatter: Formatter<Int32Array | null>;
/** C# `uint[]`: ext 78. @type {Formatter<Uint32Array | null>} */
export declare const uint32ArrayFormatter: Formatter<Uint32Array | null>;
/** C# `long[]`: ext 78. @type {Formatter<BigInt64Array | null>} */
export declare const int64ArrayFormatter: Formatter<BigInt64Array | null>;
/** C# `ulong[]`: ext 78. @type {Formatter<BigUint64Array | null>} */
export declare const uint64ArrayFormatter: Formatter<BigUint64Array | null>;
/** C# `float[]`: ext 78. @type {Formatter<Float32Array | null>} */
export declare const float32ArrayFormatter: Formatter<Float32Array | null>;
/** C# `double[]`: ext 78. @type {Formatter<Float64Array | null>} */
export declare const float64ArrayFormatter: Formatter<Float64Array | null>;
/** C# `char[]`: ext 78; UTF-16 code units. @type {Formatter<Uint16Array | null>} */
export declare const charArrayFormatter: Formatter<Uint16Array | null>;
/** C# `Half[]`: ext 78 of binary16; JS holds the values in a `Float32Array`. @type {Formatter<Float32Array | null>} */
export declare const halfArrayFormatter: Formatter<Float32Array | null>;
/**
 * C# `T?` of a value type: nil is null. (Reference-type formatters already map nil to null.)
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<T | null>}
 */
export declare function nullableFormatter<T>(item: Formatter<T>): Formatter<T | null>;
/**
 * An array: C# `T[]`, `List<T>` and the list interfaces, `Queue<T>` (front first), `Stack<T>` (top first, index 0)
 * of a non-primitive `T`; nil is null. One nesting level. Writes accept any iterable.
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<T[] | null>}
 */
export declare function arrayFormatter<T>(item: Formatter<T>): Formatter<T[] | null>;
/**
 * C# `HashSet<T>`: a `Set` (an array on the wire); nil is null. One nesting level. Writes accept any iterable.
 * Set members compare by identity in JS: only primitives (`number`, `string`, `bigint`, `boolean`) dedupe by value.
 * @template T
 * @param {Formatter<T>} item
 * @returns {Formatter<Set<T> | null>}
 */
export declare function setFormatter<T>(item: Formatter<T>): Formatter<Set<T> | null>;
/**
 * C# `Dictionary<K,V>` and the dictionary interfaces: a `Map` (never a plain object); nil is null. One nesting
 * level. Writes accept a `Map` or any iterable of `[key, value]` pairs. Keys compare by identity in JS (NEXNET046);
 * a repeated key keeps the last value.
 * @template K, V
 * @param {Formatter<K>} key
 * @param {Formatter<V>} value
 * @returns {Formatter<Map<K, V> | null>}
 */
export declare function mapFormatter<K, V>(key: Formatter<K>, value: Formatter<V>): Formatter<Map<K, V> | null>;
/**
 * C# `ValueTuple<…>` and `KeyValuePair<K,V>`: a JS array of exactly that many elements (`[a, b]`). A struct: never
 * nil. Wrap it in {@link nullableFormatter} for `Tuple<…>` (a class) and for `(A, B)?`. One nesting level.
 * @param {...Formatter<any>} formatters One formatter per element.
 * @returns {Formatter<any[]>}
 */
export declare function tupleFormatter(...formatters: Formatter<any>[]): Formatter<any[]>;
/**
 * C# `KeyValuePair<K,V>`: `[key, value]`.
 * @template K, V
 * @param {Formatter<K>} key
 * @param {Formatter<V>} value
 * @returns {Formatter<[K, V]>}
 */
export declare function keyValuePairFormatter<K, V>(key: Formatter<K>, value: Formatter<V>): Formatter<[K, V]>;
