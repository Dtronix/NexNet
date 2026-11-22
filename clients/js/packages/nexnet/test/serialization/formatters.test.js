import { describe, expect, it } from 'vitest';
import {
  FormatterRegistry, MsgPackReader, MsgPackWriter, NexusSerializationError, arrayFormatter, bigIntegerFormatter,
  byteFormatter, charFormatter, guidFormatter, halfArrayFormatter, halfFormatter, halfFromBits, halfToBits, int16Formatter,
  int32ArrayFormatter, int32Formatter, int64ArrayFormatter, int64Formatter, int64NumberFormatter, keyValuePairFormatter,
  mapFormatter, nullableFormatter, roundToHalf, sbyteFormatter, setFormatter, stringFormatter, tupleFormatter,
  uint16Formatter, uint32Formatter, uint64NumberFormatter, decimalFormatter, dateTimeFormatter, versionFormatter,
  float32Formatter,
} from '../../src/serialization/index.js';
import { DateTimeKind, NexusDateTime, NexusDecimal, NexusVersion } from '../../src/values/index.js';
import { toHex } from './support/golden.js';

/**
 * @template T
 * @param {import('../../src/serialization/index.js').Formatter<T>} f
 * @param {T} value
 * @returns {Uint8Array}
 */
function encode(f, value) {
  const w = new MsgPackWriter();
  f.write(w, value);
  return w.toUint8Array().slice();
}

/**
 * @template T
 * @param {import('../../src/serialization/index.js').Formatter<T>} f
 * @param {Uint8Array} bytes
 * @returns {T}
 */
function decode(f, bytes) {
  const r = new MsgPackReader(bytes);
  const value = f.read(r);
  r.ensureEnd();
  return value;
}

/**
 * @template T
 * @param {import('../../src/serialization/index.js').Formatter<T>} f
 * @param {T} value
 * @returns {T}
 */
const roundTrip = (f, value) => decode(f, encode(f, value));

describe('integer formatters', () => {
  it('range-check on write', () => {
    /** @type {[import('../../src/serialization/index.js').Formatter<number>, number, number][]} */
    const cases = [
      [sbyteFormatter, -128, 127], [byteFormatter, 0, 255], [int16Formatter, -32768, 32767], [uint16Formatter, 0, 65535],
      [int32Formatter, -(2 ** 31), 2 ** 31 - 1], [uint32Formatter, 0, 2 ** 32 - 1],
    ];
    for (const [f, min, max] of cases) {
      expect(roundTrip(f, min)).toBe(min);
      expect(roundTrip(f, max)).toBe(max);
      expect(() => encode(f, min - 1)).toThrow(RangeError);
      expect(() => encode(f, max + 1)).toThrow(RangeError);
      expect(() => encode(f, 0.5)).toThrow(RangeError);
      expect(() => encode(f, /** @type {any} */ ('1'))).toThrow(TypeError);
    }
  });

  it('long and ulong are bigint by default, with an opt-in checked number (D13)', () => {
    expect(roundTrip(int64Formatter, -(2n ** 63n))).toBe(-(2n ** 63n));
    expect(() => encode(int64Formatter, /** @type {any} */ (1))).toThrow(TypeError);
    expect(roundTrip(int64NumberFormatter, Number.MIN_SAFE_INTEGER)).toBe(Number.MIN_SAFE_INTEGER);
    expect(() => encode(int64NumberFormatter, 2 ** 53)).toThrow(RangeError);
    expect(() => decode(int64NumberFormatter, encode(int64Formatter, 2n ** 53n))).toThrow(NexusSerializationError);
    expect(roundTrip(uint64NumberFormatter, Number.MAX_SAFE_INTEGER)).toBe(Number.MAX_SAFE_INTEGER);
    expect(() => encode(uint64NumberFormatter, -1)).toThrow(RangeError);
    expect(() => decode(uint64NumberFormatter, Uint8Array.of(0xff))).toThrow(NexusSerializationError);
  });
});

describe('char, half and float', () => {
  it('char is a 1-character string, lone surrogates included', () => {
    expect(roundTrip(charFormatter, 'a')).toBe('a');
    expect(roundTrip(charFormatter, '\u{d800}')).toBe('\u{d800}');
    expect(() => encode(charFormatter, 'ab')).toThrow(TypeError);
    expect(() => encode(charFormatter, '')).toThrow(TypeError);
  });

  it('float rounds to float32 on read', () => {
    expect(decode(float32Formatter, encode(float32Formatter, 0.1))).toBe(Math.fround(0.1));
  });

  it('half rounds to the nearest half, ties to even', () => {
    expect(roundTrip(halfFormatter, 1.5)).toBe(1.5);
    expect(roundTrip(halfFormatter, 0.1)).toBe(halfFromBits(0x2e66));
    expect(roundTrip(halfFormatter, 70000)).toBe(Infinity);
    expect(halfToBits(65504)).toBe(0x7bff);
    expect(halfToBits(65519.99)).toBe(0x7bff);
    expect(halfToBits(65520)).toBe(0x7c00);
    expect(halfToBits(2 ** -24)).toBe(1);
    expect(halfToBits(2 ** -25)).toBe(0); // tie to even
    expect(halfToBits(3 * 2 ** -25)).toBe(2); // tie to even
    expect(halfToBits(-0)).toBe(0x8000);
    expect(halfToBits(NaN) & 0x7e00).toBe(0x7e00);
    expect(Number.isNaN(halfFromBits(0xfe00))).toBe(true);
  });

  it('the half fallback matches Math.f16round where the platform has it', () => {
    const native = /** @type {any} */ (Math).f16round;
    if (typeof native !== 'function') return;
    let seed = 11;
    const next = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff);
    const view = new DataView(new ArrayBuffer(8));
    for (let i = 0; i < 20000; i++) {
      // Random doubles around the half range, plus every half and its neighbours.
      view.setUint32(0, 0x3e000000 + (next() % 0x05000000) * (next() % 2 ? 1 : -1));
      view.setUint32(4, next());
      const x = view.getFloat64(0) * (next() % 2 ? 1 : -1);
      expect(halfFromBits(halfToBits(x)), String(x)).toBe(native(x));
      const h = halfFromBits(i % 0x10000);
      if (!Number.isNaN(h)) expect(halfToBits(h)).toBe(i % 0x10000);
    }
    expect(roundToHalf(0.1)).toBe(native(0.1));
  });

  it('half arrays hold float32 values', () => {
    const value = roundTrip(halfArrayFormatter, Float32Array.of(1, -2.5, 0.1));
    expect(value).toBeInstanceOf(Float32Array);
    expect(Array.from(/** @type {Float32Array} */ (value))).toEqual([1, -2.5, Math.fround(halfFromBits(0x2e66))]);
  });
});

describe('ext 78 arrays', () => {
  it('accept typed arrays and plain arrays, and read aligned typed arrays', () => {
    expect(toHex(encode(int32ArrayFormatter, /** @type {any} */ ([1, -2, 3])))).toBe('c70d4e0401000000feffffff03000000');
    const bytes = encode(int64ArrayFormatter, BigInt64Array.of(1n, -1n));
    // An odd offset: the read must copy for alignment.
    const shifted = new Uint8Array(bytes.length + 1);
    shifted.set(bytes, 1);
    const value = decode(int64ArrayFormatter, shifted.subarray(1));
    expect(value).toBeInstanceOf(BigInt64Array);
    expect(Array.from(/** @type {BigInt64Array} */ (value))).toEqual([1n, -1n]);
    expect(roundTrip(int32ArrayFormatter, null)).toBeNull();
  });
});

describe('value-type formatters', () => {
  it('refuse nil and null', () => {
    for (const f of [/** @type {any} */ (int32Formatter), guidFormatter, decimalFormatter, dateTimeFormatter, bigIntegerFormatter, tupleFormatter(int32Formatter)]) {
      expect(() => decode(f, Uint8Array.of(0xc0))).toThrow(NexusSerializationError);
      expect(() => encode(f, null)).toThrow(TypeError);
    }
  });

  it('nullable maps nil to null', () => {
    const f = nullableFormatter(int32Formatter);
    expect(toHex(encode(f, null))).toBe('c0');
    expect(decode(f, Uint8Array.of(0xc0))).toBeNull();
    expect(roundTrip(f, 5)).toBe(5);
  });

  it('round-trip decimal, DateTime, Guid, BigInteger and Version', () => {
    const d = NexusDecimal.parse('-123.4500');
    expect(roundTrip(decimalFormatter, d).toString()).toBe('-123.4500');
    const dt = new NexusDateTime(637134336000000000n, DateTimeKind.Utc);
    expect(roundTrip(dateTimeFormatter, dt).equals(dt)).toBe(true);
    expect(roundTrip(guidFormatter, '{00112233-4455-6677-8899-AABBCCDDEEFF}')).toBe('00112233-4455-6677-8899-aabbccddeeff');
    expect(() => encode(guidFormatter, 'nope')).toThrow(SyntaxError);
    for (const n of [0n, 1n, -1n, 127n, 128n, -128n, -129n, 255n, 2n ** 64n, -(2n ** 100n), 10n ** 30n, -(10n ** 30n) + 1n]) {
      expect(roundTrip(bigIntegerFormatter, n)).toBe(n);
    }
    expect(roundTrip(versionFormatter, new NexusVersion(1, 2, 3))?.toString()).toBe('1.2.3');
    expect(roundTrip(versionFormatter, null)).toBeNull();
  });
});

describe('combinators', () => {
  it('round-trip arrays, sets, maps and tuples', () => {
    expect(roundTrip(arrayFormatter(stringFormatter), ['a', null, 'c'])).toEqual(['a', null, 'c']);
    expect(roundTrip(arrayFormatter(int32Formatter), null)).toBeNull();
    expect(toHex(encode(arrayFormatter(int32Formatter), /** @type {any} */ (new Set([1, 2]))))).toBe('920102');
    expect(roundTrip(setFormatter(int32Formatter), new Set([3, 1]))).toEqual(new Set([3, 1]));
    const map = new Map([['a', 1], ['b', 2]]);
    expect(roundTrip(mapFormatter(stringFormatter, int32Formatter), map)).toEqual(map);
    expect(toHex(encode(mapFormatter(stringFormatter, int32Formatter), /** @type {any} */ ([['x', 1]])))).toBe('81a17801');
    expect(roundTrip(tupleFormatter(int32Formatter, stringFormatter), [1, 'a'])).toEqual([1, 'a']);
    expect(() => encode(tupleFormatter(int32Formatter, stringFormatter), [1])).toThrow(TypeError);
    expect(roundTrip(keyValuePairFormatter(stringFormatter, int32Formatter), ['k', 2])).toEqual(['k', 2]);
  });

  it('count one nesting level per container', () => {
    const f = arrayFormatter(arrayFormatter(arrayFormatter(int32Formatter)));
    const bytes = encode(f, [[[1]]]);
    expect(() => f.read(new MsgPackReader(bytes, { maxDepth: 2 }))).toThrow(NexusSerializationError);
    const r = new MsgPackReader(bytes, { maxDepth: 3 });
    expect(f.read(r)).toEqual([[[1]]]);
    expect(r.depth).toBe(0);
  });
});

describe('FormatterRegistry', () => {
  it('registers once and looks up', () => {
    const registry = new FormatterRegistry();
    registry.register('int', int32Formatter);
    expect(registry.get('int')).toBe(int32Formatter);
    expect(registry.tryGet('long')).toBeUndefined();
    expect(() => registry.get('long')).toThrow(/no formatter/);
    expect(() => registry.register('int', int32Formatter)).toThrow(/already/);
    expect(() => registry.register('x', /** @type {any} */ ({}))).toThrow(TypeError);
  });
});
