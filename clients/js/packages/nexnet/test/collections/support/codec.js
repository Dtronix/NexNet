/**
 * Codec and fixture helpers of the collection tests. J02's codec is not merged yet; the tests install the invocation
 * tests' writer and a reader that also reads ext values (C# writes `int[]` as ext 78, protocol spec §10.5).
 * @module
 */

import { readFileSync } from 'node:fs';
import { BodyReader } from '../../../src/session/wire.js';
import { TestWriter } from '../../invocation/support/testCodec.js';

/** A MessagePack reader with the members the collection runtime and these tests use. */
export class CollectionTestReader {
  /** @type {Uint8Array} */
  #bytes;
  /** @type {BodyReader} */
  #r;

  /** @param {Uint8Array} bytes */
  constructor(bytes) {
    this.#bytes = bytes;
    this.#r = new BodyReader(bytes);
  }

  get remaining() { return this.#r.remaining; }
  tryReadNil() { return this.#r.tryReadNil(); }
  readInt() { return this.#r.readIntInRange(Number.MIN_SAFE_INTEGER, Number.MAX_SAFE_INTEGER); }
  readString() { return this.#r.readString(); }
  readBin() { return this.#r.readBin(); }
  readArrayHeader() { return this.#r.readArrayHeader(); }
  skip() { this.#r.skip(); }

  /** @returns {{ type: number, data: Uint8Array }} */
  readExt() {
    const bytes = this.#bytes;
    const pos = bytes.length - this.#r.remaining;
    const code = bytes[pos];
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    /** @type {Record<number, number>} */
    const fixed = { 0xd4: 1, 0xd5: 2, 0xd6: 4, 0xd7: 8, 0xd8: 16 };
    let length;
    let header;
    if (code in fixed) {
      length = fixed[code];
      header = 1;
    } else if (code === 0xc7) {
      length = bytes[pos + 1];
      header = 2;
    } else if (code === 0xc8) {
      length = view.getUint16(pos + 1);
      header = 3;
    } else if (code === 0xc9) {
      length = view.getUint32(pos + 1);
      header = 5;
    } else {
      throw new Error(`expected an ext value, got 0x${code.toString(16)}`);
    }
    const type = view.getInt8(pos + header);
    const data = bytes.slice(pos + header + 1, pos + header + 1 + length);
    this.#r.skip();
    return { type, data };
  }
}

/** The codec to install with `setInvocationCodec`. */
export const collectionTestCodec = /** @type {import('../../../src/invocation/codec.js').InvocationCodec} */ (/** @type {unknown} */ ({
  createWriter: () => new TestWriter(),
  /** @param {Uint8Array} bytes */
  createReader: (bytes) => new CollectionTestReader(bytes),
}));

/** @type {import('../../../src/contract/codec.js').Formatter<number>} */
export const intFormatter = { write: (w, v) => w.writeInt(v), read: (r) => r.readInt() };

/** @type {import('../../../src/contract/codec.js').Formatter<string | null>} */
export const stringFormatter = { write: (w, v) => w.writeString(v), read: (r) => r.readString() };

/**
 * C# `int[]`: ext 78 with element kind 4 and little-endian int32 elements, or nil (protocol spec §10.5). Read only.
 * @type {import('../../../src/contract/codec.js').Formatter<number[] | null>}
 */
export const int32ArrayFormatter = {
  write: () => {
    throw new Error('not used');
  },
  read: (reader) => {
    if (reader.tryReadNil()) return null;
    const { type, data } = /** @type {CollectionTestReader} */ (/** @type {unknown} */ (reader)).readExt();
    if (type !== 78 || data[0] !== 4 || (data.length - 1) % 4 !== 0) throw new Error('not an int32 ext 78 array');
    const view = new DataView(data.buffer, data.byteOffset + 1, data.length - 1);
    /** @type {number[]} */
    const values = [];
    for (let i = 0; i < view.byteLength; i += 4) values.push(view.getInt32(i, true));
    return values;
  },
};

/** @param {string} hex @returns {Uint8Array} */
export function fromHex(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return out;
}

/** @param {Uint8Array} bytes @returns {string} */
export function toHex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * @typedef {object} MessageVector
 * @property {'message'} kind
 * @property {string} name
 * @property {Record<string, unknown>} fields
 * @property {string} hex
 */

/**
 * @typedef {object} StreamStep
 * @property {string} [hex] The item; absent on a close step.
 * @property {boolean} [close]
 * @property {boolean} [disconnect] True when the item was a protocol error.
 * @property {number[]} list
 * @property {number} version
 * @property {string[]} events
 * @property {boolean | null} answer
 */

/**
 * @typedef {object} StreamVector
 * @property {'stream'} kind
 * @property {string} name
 * @property {StreamStep[]} steps
 */

/** The recorded C# vectors (fixtures/collections.jsonl). @returns {Array<MessageVector | StreamVector>} */
export function loadVectors() {
  return readFileSync(new URL('../fixtures/collections.jsonl', import.meta.url), 'utf8')
    .split('\n')
    .filter((line) => line.trim() !== '')
    .map((line) => JSON.parse(line));
}
