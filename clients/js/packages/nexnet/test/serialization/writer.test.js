import { describe, expect, it } from 'vitest';
import { MsgPackWriter } from '../../src/serialization/index.js';
import { toHex } from './support/golden.js';

/** @param {(w: MsgPackWriter) => void} write @returns {string} */
function hex(write) {
  const w = new MsgPackWriter({ initialCapacity: 1 });
  write(w);
  return toHex(w.toUint8Array());
}

const encoder = new TextEncoder();

/** @param {string} s @returns {string} The expected str encoding: TextEncoder bytes after the canonical header. */
function expectedString(s) {
  const bytes = encoder.encode(s);
  const n = bytes.length;
  const header = n < 32 ? [0xa0 | n] : n <= 0xff ? [0xd9, n] : n <= 0xffff ? [0xda, n >> 8, n & 0xff] : [0xdb, n >>> 24, (n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
  return toHex(Uint8Array.of(...header)) + toHex(bytes);
}

describe('MsgPackWriter', () => {
  it('writes integers in the smallest form', () => {
    expect(hex((w) => w.writeInt(0))).toBe('00');
    expect(hex((w) => w.writeInt(-32))).toBe('e0');
    expect(hex((w) => w.writeInt(-33))).toBe('d0df');
    expect(hex((w) => w.writeInt(4294967296))).toBe('cf0000000100000000');
    expect(hex((w) => w.writeInt(-2147483649))).toBe('d3ffffffff7fffffff');
    expect(hex((w) => w.writeInt(Number.MAX_SAFE_INTEGER))).toBe('cf001fffffffffffff');
    expect(hex((w) => w.writeInt64(-1n))).toBe('ff');
    expect(hex((w) => w.writeInt64(-(2n ** 63n)))).toBe('d38000000000000000');
    expect(hex((w) => w.writeUInt64(2n ** 64n - 1n))).toBe('cfffffffffffffffff');
    expect(hex((w) => w.writeUInt64(0xffffffffn))).toBe('ceffffffff');
  });

  it('refuses values it cannot write and leaves the buffer unchanged', () => {
    const w = new MsgPackWriter();
    expect(() => w.writeInt(1.5)).toThrow(RangeError);
    expect(() => w.writeInt(2 ** 53)).toThrow(RangeError);
    expect(() => w.writeInt(/** @type {any} */ ('1'))).toThrow(TypeError);
    expect(() => w.writeInt64(/** @type {any} */ (1))).toThrow(TypeError);
    expect(() => w.writeInt64(2n ** 63n)).toThrow(RangeError);
    expect(() => w.writeUInt64(-1n)).toThrow(RangeError);
    expect(() => w.writeBool(/** @type {any} */ (1))).toThrow(TypeError);
    expect(() => w.writeString(/** @type {any} */ (1))).toThrow(TypeError);
    expect(() => w.writeBin(/** @type {any} */ ([1]))).toThrow(TypeError);
    expect(() => w.writeArrayHeader(-1)).toThrow(RangeError);
    expect(() => w.writeExtHeader(128, 1)).toThrow(RangeError);
    expect(w.length).toBe(0);
  });

  it('encodes strings exactly as TextEncoder, on both paths and at every header boundary', () => {
    const samples = [
      '', 'a', 'a'.repeat(31), 'a'.repeat(32), 'é'.repeat(15), 'é'.repeat(16), '日'.repeat(10), '日'.repeat(11),
      '😀'.repeat(7), '😀'.repeat(8), 'a'.repeat(85), 'a'.repeat(86), 'a'.repeat(255), 'a'.repeat(256), '日'.repeat(100),
      'a'.repeat(65535), 'a'.repeat(65536), '\u{d800}', 'x\u{dc00}y', '\u{d83d}', 'a\u{d83d}'.repeat(20), '\u{0}', '߿ࠀ￿',
    ];
    for (const s of samples) expect(hex((w) => w.writeString(s)), JSON.stringify(s.slice(0, 20))).toBe(expectedString(s));
  });

  it('encodes random strings as TextEncoder', () => {
    let seed = 7;
    const next = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff);
    for (let i = 0; i < 500; i++) {
      const length = next() % 70;
      let s = '';
      for (let j = 0; j < length; j++) {
        const pick = next() % 4;
        s += String.fromCharCode(pick === 0 ? next() % 0x80 : pick === 1 ? next() % 0x800 : pick === 2 ? 0xd800 + (next() % 0x800) : next() % 0x10000);
      }
      expect(hex((w) => w.writeString(s))).toBe(expectedString(s));
    }
  });

  it('writes nil for null strings and bins', () => {
    expect(hex((w) => { w.writeString(null); w.writeBin(null); })).toBe('c0c0');
  });

  it('writes canonical ext headers', () => {
    const data = (/** @type {number} */ n) => new Uint8Array(n);
    expect(hex((w) => w.writeExt(78, data(1)))).toBe('d44e00');
    expect(hex((w) => w.writeExt(-1, data(2)))).toBe('d5ff0000');
    expect(hex((w) => w.writeExt(1, data(16))).slice(0, 4)).toBe('d801');
    expect(hex((w) => w.writeExt(1, data(0)))).toBe('c70001');
    expect(hex((w) => w.writeExt(1, data(3)))).toBe('c70301000000');
    expect(hex((w) => w.writeExt(1, data(256))).slice(0, 8)).toBe('c8010001');
    expect(hex((w) => w.writeExt(1, data(65536))).slice(0, 12)).toBe('c90001000001');
  });

  it('writes container headers', () => {
    expect(hex((w) => w.writeArrayHeader(15))).toBe('9f');
    expect(hex((w) => w.writeArrayHeader(16))).toBe('dc0010');
    expect(hex((w) => w.writeArrayHeader(65536))).toBe('dd00010000');
    expect(hex((w) => w.writeMapHeader(15))).toBe('8f');
    expect(hex((w) => w.writeMapHeader(16))).toBe('de0010');
    expect(hex((w) => w.writeBinHeader(256))).toBe('c50100');
  });

  it('grows, returns a view, and resets for reuse', () => {
    const w = new MsgPackWriter({ initialCapacity: 16 });
    for (let i = 0; i < 1000; i++) w.writeInt(i);
    expect(w.length).toBe(128 + 2 * 128 + 3 * 744);
    const view = w.toUint8Array();
    expect(view.length).toBe(w.length);
    w.reset();
    expect(w.length).toBe(0);
    w.writeRaw(Uint8Array.of(1, 2, 3));
    expect(toHex(w.toUint8Array())).toBe('010203');
    expect(view[0]).toBe(1); // the old view shares the buffer
  });

  it('writes floats', () => {
    expect(hex((w) => w.writeFloat32(1.5))).toBe('ca3fc00000');
    expect(hex((w) => w.writeFloat32(0.1))).toBe('ca3dcccccd');
    expect(hex((w) => w.writeFloat64(-0))).toBe('cb8000000000000000');
  });
});
