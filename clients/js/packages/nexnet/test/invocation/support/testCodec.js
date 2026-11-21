/**
 * A minimal MessagePack writer and reader with the `MsgPackWriter`/`MsgPackReader` members the invocation tests use.
 * J02's codec is not merged yet; the tests install this one with `setInvocationCodec`.
 * @module
 */

import { BodyReader, BodyWriter } from '../../../src/session/wire.js';

export class TestWriter {
  /** @type {Uint8Array[]} */
  #parts = [];
  #length = 0;

  /** @param {Uint8Array} bytes */
  #push(bytes) {
    this.#parts.push(bytes);
    this.#length += bytes.length;
  }

  /** @param {(w: BodyWriter) => void} write */
  #body(write) {
    const w = new BodyWriter(16);
    write(w);
    this.#push(w.toUint8Array());
  }

  get length() { return this.#length; }
  writeNil() { this.#push(Uint8Array.of(0xc0)); }
  /** @param {boolean} value */
  writeBool(value) { this.#push(Uint8Array.of(value ? 0xc3 : 0xc2)); }
  /** @param {number} value */
  writeInt(value) { this.#body((w) => w.writeInt(value)); }
  /** @param {bigint} value */
  writeInt64(value) { this.#body((w) => w.writeInt(value)); }
  /** @param {bigint} value */
  writeUInt64(value) { this.#body((w) => w.writeInt(value)); }
  /** @param {string | null} value */
  writeString(value) { this.#body((w) => w.writeString(value)); }
  /** @param {Uint8Array | null} value */
  writeBin(value) { this.#body((w) => w.writeBin(value)); }
  /** @param {number} count */
  writeArrayHeader(count) { this.#body((w) => w.writeArrayHeader(count)); }
  /** @param {number} count */
  writeMapHeader(count) {
    if (count < 16) this.#push(Uint8Array.of(0x80 | count));
    else this.#push(Uint8Array.of(0xde, count >> 8, count & 0xff));
  }
  /** @param {number} value */
  writeFloat32(value) {
    const b = new Uint8Array(5);
    b[0] = 0xca;
    new DataView(b.buffer).setFloat32(1, value);
    this.#push(b);
  }
  /** @param {number} value */
  writeFloat64(value) {
    const b = new Uint8Array(9);
    b[0] = 0xcb;
    new DataView(b.buffer).setFloat64(1, value);
    this.#push(b);
  }
  /** @param {number} type @param {Uint8Array} data */
  writeExt(type, data) {
    this.#push(Uint8Array.of(0xc7, data.length, type & 0xff));
    this.#push(data.slice());
  }
  /** @param {Uint8Array} bytes */
  writeRaw(bytes) { this.#push(bytes.slice()); }
  toUint8Array() {
    const out = new Uint8Array(this.#length);
    let o = 0;
    for (const p of this.#parts) {
      out.set(p, o);
      o += p.length;
    }
    return out;
  }
  reset() {
    this.#parts = [];
    this.#length = 0;
  }
}

export class TestReader {
  #r;

  /** @param {Uint8Array} bytes */
  constructor(bytes) {
    this.#r = new BodyReader(bytes);
  }

  get remaining() { return this.#r.remaining; }
  tryReadNil() { return this.#r.tryReadNil(); }
  readBool() {
    const b = this.#r.peek();
    if (b !== 0xc2 && b !== 0xc3) throw new Error('expected a bool');
    this.#r.skip();
    return b === 0xc3;
  }
  readInt() { return this.#r.readIntInRange(Number.MIN_SAFE_INTEGER, Number.MAX_SAFE_INTEGER); }
  readInt64() { return this.#r.readInt64(); }
  readString() { return this.#r.readString(); }
  readBin() { return this.#r.readBin(); }
  readArrayHeader() { return this.#r.readArrayHeader(); }
  skip() { this.#r.skip(); }
}

/** The codec to install with `setInvocationCodec`. */
export const testCodec = /** @type {import('../../../src/invocation/codec.js').InvocationCodec} */ (/** @type {unknown} */ ({
  createWriter: () => new TestWriter(),
  /** @param {Uint8Array} bytes */
  createReader: (bytes) => new TestReader(bytes),
}));

/** @type {import('../../../src/contract/codec.js').Formatter<number>} */
export const intFormatter = { write: (w, v) => w.writeInt(v), read: (r) => r.readInt() };

/** @type {import('../../../src/contract/codec.js').Formatter<string | null>} */
export const stringFormatter = { write: (w, v) => w.writeString(v), read: (r) => r.readString() };

/** @type {import('../../../src/contract/codec.js').Formatter<Uint8Array | null>} */
export const binFormatter = { write: (w, v) => w.writeBin(v), read: (r) => r.readBin() };

/**
 * Serializes values with the test writer.
 * @param {(w: TestWriter) => void} write
 * @returns {Uint8Array}
 */
export function pack(write) {
  const w = new TestWriter();
  write(w);
  return w.toUint8Array();
}
