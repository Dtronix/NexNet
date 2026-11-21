import { describe, expect, it } from 'vitest';
import { MsgPackReader, MsgPackWriter, NexusSerializationError, NexusSerializerOptions, resolveSerializerOptions } from '../../src/serialization/index.js';
import { fromHex, toHex } from './support/golden.js';

/**
 * @param {() => unknown} action
 * @returns {string} The kind of the NexusSerializationError thrown.
 */
function kindOf(action) {
  try {
    action();
  } catch (e) {
    expect(e).toBeInstanceOf(NexusSerializationError);
    return /** @type {NexusSerializationError} */ (e).kind;
  }
  throw new Error('no error');
}

/** @param {string} hex @param {import('../../src/serialization/index.js').SerializerOptions} [options] */
const reader = (hex, options) => new MsgPackReader(fromHex(hex), options);

describe('options', () => {
  it('resolves presets and overrides', () => {
    expect(resolveSerializerOptions(undefined)).toBe(NexusSerializerOptions.Untrusted);
    expect(resolveSerializerOptions({})).toEqual({ preset: 'untrusted', maxDepth: 64, maxElements: 0, strictUtf8: true });
    expect(resolveSerializerOptions({ preset: 'trusted' })).toEqual({ preset: 'trusted', maxDepth: Infinity, maxElements: 0, strictUtf8: false });
    expect(resolveSerializerOptions({ preset: 'trusted', maxDepth: 5, strictUtf8: true })).toEqual({ preset: 'trusted', maxDepth: 5, maxElements: 0, strictUtf8: true });
    const options = { maxElements: 10 };
    expect(resolveSerializerOptions(options)).toBe(resolveSerializerOptions(options));
    expect(() => resolveSerializerOptions({ maxDepth: -1 })).toThrow(RangeError);
  });

  it('reads lenient UTF-8 under the trusted preset', () => {
    expect(reader('a3618062', { preset: 'trusted' }).readString()).toBe('a\u{fffd}b');
    expect(kindOf(() => reader('a3618062').readString())).toBe('utf8');
  });
});

describe('MsgPackReader', () => {
  it('reads integers of every form, with range checks', () => {
    expect(reader('d1ff7f').readInt()).toBe(-129);
    expect(reader('cf001fffffffffffff').readInt()).toBe(Number.MAX_SAFE_INTEGER);
    expect(kindOf(() => reader('cf0020000000000000').readInt())).toBe('range');
    expect(reader('cf0020000000000000').readInt64()).toBe(2n ** 53n);
    expect(kindOf(() => reader('cf8000000000000000').readInt64())).toBe('range');
    expect(kindOf(() => reader('ff').readUInt64())).toBe('range');
    expect(kindOf(() => reader('cc80').readSByte())).toBe('range');
    expect(reader('d001').readByte()).toBe(1);
    expect(kindOf(() => reader('c0').readInt())).toBe('type');
    expect(kindOf(() => reader('c1').readInt())).toBe('reserved');
    expect(kindOf(() => reader('cd01').readInt())).toBe('truncated');
    expect(kindOf(() => reader('').readInt())).toBe('truncated');
  });

  it('reads floats from float and integer forms', () => {
    expect(reader('ca3fc00000').readFloat()).toBe(1.5);
    expect(reader('05').readFloat()).toBe(5);
    expect(reader('cfffffffffffffffff').readFloat()).toBe(2 ** 64);
    expect(kindOf(() => reader('a131').readFloat())).toBe('type');
    expect(kindOf(() => reader('c1').readFloat())).toBe('reserved');
  });

  it('peeks without consuming and checks the end', () => {
    const r = reader('c00102');
    expect(r.isNil).toBe(true);
    expect(r.peekCode()).toBe(0xc0);
    expect(r.tryReadNil()).toBe(true);
    expect(r.tryReadNil()).toBe(false);
    expect(r.readInt()).toBe(1);
    expect(r.consumed).toBe(2);
    expect(r.remaining).toBe(1);
    expect(kindOf(() => r.ensureEnd())).toBe('trailing');
    r.skip();
    expect(() => r.ensureEnd()).not.toThrow();
    expect(kindOf(() => r.peekCode())).toBe('truncated');
  });

  it('checks counts and lengths against the remaining bytes before allocating', () => {
    expect(kindOf(() => reader('dd7fffffff').readArrayHeader())).toBe('length');
    expect(kindOf(() => reader('df7fffffff').readMapHeader())).toBe('length');
    expect(kindOf(() => reader('c67fffffff').readBin())).toBe('length');
    expect(kindOf(() => reader('dbffffffff61').readString())).toBe('length');
    expect(kindOf(() => reader('c9ffffffff4e').readExtHeader())).toBe('length');
    expect(kindOf(() => reader('920102').readArrayHeader(3))).toBe('count');
  });

  it('limits depth and elements', () => {
    const r = reader('c0', { maxDepth: 2 });
    r.enter();
    r.enter();
    expect(kindOf(() => r.enter())).toBe('depth');
    expect(r.depth).toBe(2);
    r.exit();
    r.exit();
    const e = reader('93010203' + '92' + '0405', { maxElements: 4 });
    e.readArrayHeader();
    e.skip();
    e.skip();
    e.skip();
    expect(kindOf(() => e.readArrayHeader())).toBe('elements');
  });

  it('skips deeply nested values without recursion', () => {
    const depth = 100_000;
    const bytes = new Uint8Array(depth + 1).fill(0x91);
    bytes[depth] = 0xc0;
    const r = new MsgPackReader(bytes);
    r.skip();
    expect(r.remaining).toBe(0);
    bytes[depth] = 0x91;
    expect(kindOf(() => new MsgPackReader(bytes).skip())).toBe('truncated');
  });

  it('reads across segments, including empty ones', () => {
    const w = new MsgPackWriter();
    w.writeArrayHeader(4);
    w.writeString('héllo wörld, a string longer than thirty-two bytes');
    w.writeInt64(-(2n ** 40n));
    w.writeFloat64(Math.PI);
    w.writeBin(Uint8Array.of(1, 2, 3, 4, 5));
    const bytes = w.toUint8Array().slice();
    for (const size of [1, 2, 3, 5, 8]) {
      /** @type {Uint8Array[]} */
      const parts = [new Uint8Array(0)];
      for (let i = 0; i < bytes.length; i += size) parts.push(bytes.subarray(i, i + size), new Uint8Array(0));
      const r = new MsgPackReader(parts);
      expect(r.remaining).toBe(bytes.length);
      expect(r.readArrayHeader()).toBe(4);
      expect(r.readString()).toBe('héllo wörld, a string longer than thirty-two bytes');
      expect(r.readInt64()).toBe(-(2n ** 40n));
      expect(r.readFloat()).toBe(Math.PI);
      expect(Array.from(/** @type {Uint8Array} */ (r.readBin()))).toEqual([1, 2, 3, 4, 5]);
      r.ensureEnd();
      const raw = new MsgPackReader(parts).readRaw();
      expect(toHex(raw)).toBe(toHex(bytes));
    }
  });

  it('returns bins and raw values as copies', () => {
    const bytes = fromHex('c40201029201c0');
    const r = new MsgPackReader(bytes);
    const bin = /** @type {Uint8Array} */ (r.readBin());
    const raw = r.readRaw();
    bytes.fill(0);
    expect(Array.from(bin)).toEqual([1, 2]);
    expect(toHex(raw)).toBe('9201c0');
  });

  it('reads ext values', () => {
    const r = reader('d5ff0102');
    const ext = r.readExt();
    expect(ext.type).toBe(-1);
    expect(Array.from(ext.data)).toEqual([1, 2]);
  });

  it('refuses a source that is not bytes', () => {
    expect(() => new MsgPackReader(/** @type {any} */ ('x'))).toThrow(TypeError);
    expect(() => new MsgPackReader(/** @type {any} */ ([[1]]))).toThrow(TypeError);
  });
});
