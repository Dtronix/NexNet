/**
 * The MessagePack reader with the decoding limits of protocol specification §11 (`NexusSerializerOptions`).
 * @module
 */

import { serializationError } from './errors.js';

/**
 * Reader hardening, mirroring C# `NexusSerializerOptions`.
 *
 * @typedef {object} SerializerOptions
 * @property {'trusted' | 'untrusted'} [preset] Preset limits; explicit fields override it. Default `'untrusted'`.
 * @property {number} [maxDepth] Most nesting in one value (64 untrusted, unlimited trusted). Each array or map of a
 *   sequence, set, map, tuple, `KeyValuePair`, object or union is one level.
 * @property {number} [maxElements] Most array elements plus map keys and values, counted over the whole value read
 *   by one reader. 0 (the default) is no limit.
 * @property {boolean} [strictUtf8] Reject invalid UTF-8 instead of replacing it (true untrusted, false trusted).
 */

/**
 * Options with every member set.
 * @typedef {object} ResolvedSerializerOptions
 * @property {'trusted' | 'untrusted'} preset
 * @property {number} maxDepth
 * @property {number} maxElements
 * @property {boolean} strictUtf8
 */

/** The presets of C# `NexusSerializerOptions`. */
export const NexusSerializerOptions = Object.freeze({
  /** Untrusted peer (the default): depth 64, strict UTF-8. @type {Readonly<ResolvedSerializerOptions>} */
  Untrusted: Object.freeze({ preset: 'untrusted', maxDepth: 64, maxElements: 0, strictUtf8: true }),
  /** Trusted peer: no depth limit, invalid UTF-8 replaced by U+FFFD. @type {Readonly<ResolvedSerializerOptions>} */
  Trusted: Object.freeze({ preset: 'trusted', maxDepth: Infinity, maxElements: 0, strictUtf8: false }),
});

/** @type {WeakMap<object, Readonly<ResolvedSerializerOptions>>} */
const resolved = new WeakMap();

/**
 * Applies the preset and the defaults to `options`.
 * @param {SerializerOptions | null | undefined} options
 * @returns {Readonly<ResolvedSerializerOptions>}
 */
export function resolveSerializerOptions(options) {
  if (options === null || options === undefined) return NexusSerializerOptions.Untrusted;
  const cached = resolved.get(options);
  if (cached) return cached;
  const preset = options.preset === 'trusted' ? NexusSerializerOptions.Trusted : NexusSerializerOptions.Untrusted;
  const maxDepth = options.maxDepth ?? preset.maxDepth;
  const maxElements = options.maxElements ?? preset.maxElements;
  if (typeof maxDepth !== 'number' || !(maxDepth >= 0)) throw new RangeError(`maxDepth must be a number of at least 0; got ${String(maxDepth)}`);
  if (typeof maxElements !== 'number' || !(maxElements >= 0)) throw new RangeError(`maxElements must be a number of at least 0; got ${String(maxElements)}`);
  const result = Object.freeze({
    preset: preset.preset,
    maxDepth,
    maxElements,
    strictUtf8: options.strictUtf8 ?? preset.strictUtf8,
  });
  resolved.set(options, result);
  return result;
}

const strictDecoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const lenientDecoder = new TextDecoder('utf-8', { ignoreBOM: true });

/** Strings up to this many bytes that are all ASCII are decoded by a loop instead of `TextDecoder`. */
const SHORT_STRING = 32;

const EMPTY = new Uint8Array(0);

/**
 * @param {number} code
 * @param {string} expected
 * @returns {Error}
 */
function unexpected(code, expected) {
  return code === 0xc1
    ? serializationError('reserved', 'the reserved code 0xc1 is never valid')
    : serializationError('type', `expected ${expected}, found code 0x${code.toString(16).padStart(2, '0')}`);
}

/** @returns {Error} */
function truncated() {
  return serializationError('truncated', 'the input ends inside the value');
}

/**
 * Reads MessagePack from one buffer or a list of segments, enforcing {@link SerializerOptions}.
 *
 * Every read checks the bytes it needs and throws {@link import('./errors.js').NexusSerializationError} for input it
 * refuses; its `kind` says why. Counts and lengths are checked against the remaining bytes before anything is
 * allocated. Skipping is iterative, so nesting costs no stack.
 */
export class MsgPackReader {
  /** @type {readonly Uint8Array[]} */
  #segments;
  #segmentIndex = 0;
  /** Current segment. @type {Uint8Array} */
  #seg;
  /** @type {DataView} */
  #view;
  /** Offset in the current segment. */
  #pos = 0;
  /** Bytes of the segments before the current one. */
  #base = 0;
  /** Total bytes. */
  #total = 0;
  #depth = 0;
  #elements = 0;
  /** @type {Readonly<ResolvedSerializerOptions>} */
  #options;
  /** View of the last fixed-size read: the current segment's, or the scratch one when the bytes span segments. @type {DataView} */
  #fixedView;
  #scratch = new Uint8Array(8);
  #scratchView = new DataView(this.#scratch.buffer);
  /** Whether the last {@link #take} copied. */
  #tookCopy = false;

  /**
   * @param {Uint8Array | readonly Uint8Array[]} source One buffer, or segments read in order. Not copied: do not change
   *   the bytes while reading.
   * @param {SerializerOptions} [options]
   */
  constructor(source, options) {
    const segments = source instanceof Uint8Array ? [source] : Array.isArray(source) ? source : null;
    if (segments === null) throw new TypeError('MsgPackReader needs a Uint8Array or an array of Uint8Array segments');
    for (const s of segments) {
      if (!(s instanceof Uint8Array)) throw new TypeError('every segment must be a Uint8Array');
      this.#total += s.length;
    }
    this.#segments = segments;
    this.#seg = segments.length > 0 ? segments[0] : EMPTY;
    this.#view = new DataView(this.#seg.buffer, this.#seg.byteOffset, this.#seg.byteLength);
    this.#fixedView = this.#view;
    this.#options = resolveSerializerOptions(options);
  }

  /** The options in effect, with defaults applied. @returns {Readonly<ResolvedSerializerOptions>} */
  get options() {
    return this.#options;
  }

  /** Bytes not read yet. @returns {number} */
  get remaining() {
    return this.#total - this.#base - this.#pos;
  }

  /** Bytes read so far. @returns {number} */
  get consumed() {
    return this.#base + this.#pos;
  }

  /** Current nesting depth (see {@link enter}). @returns {number} */
  get depth() {
    return this.#depth;
  }

  // ---------------------------------------------------------------- Cursor

  /** Moves to the next non-empty segment; false at the end. @returns {boolean} */
  #nextSegment() {
    while (this.#segmentIndex + 1 < this.#segments.length) {
      this.#base += this.#seg.length;
      this.#seg = this.#segments[++this.#segmentIndex];
      this.#pos = 0;
      if (this.#seg.length > 0) {
        this.#view = new DataView(this.#seg.buffer, this.#seg.byteOffset, this.#seg.byteLength);
        return true;
      }
    }
    return false;
  }

  /** @returns {number} */
  #u8() {
    if (this.#pos < this.#seg.length) return this.#seg[this.#pos++];
    if (!this.#nextSegment()) throw truncated();
    return this.#seg[this.#pos++];
  }

  /** @returns {number} The next byte, not consumed; -1 at the end. */
  #peek() {
    if (this.#pos < this.#seg.length) return this.#seg[this.#pos];
    if (this.#base + this.#pos >= this.#total) return -1;
    this.#nextSegment();
    return this.#seg[this.#pos];
  }

  /**
   * Consumes `n` (at most 8) bytes and returns their offset in {@link #fixedView}.
   * @param {number} n
   * @returns {number}
   */
  #fixed(n) {
    const p = this.#pos;
    if (p + n <= this.#seg.length) {
      this.#pos = p + n;
      this.#fixedView = this.#view;
      return p;
    }
    this.#copyInto(this.#scratch, n);
    this.#fixedView = this.#scratchView;
    return 0;
  }

  /**
   * Consumes `n` bytes into `dest[0..n)`, across segments.
   * @param {Uint8Array} dest
   * @param {number} n
   */
  #copyInto(dest, n) {
    if (n > this.remaining) throw truncated();
    let o = 0;
    while (o < n) {
      if (this.#pos >= this.#seg.length) this.#nextSegment();
      const take = Math.min(n - o, this.#seg.length - this.#pos);
      dest.set(this.#seg.subarray(this.#pos, this.#pos + take), o);
      this.#pos += take;
      o += take;
    }
  }

  /**
   * Consumes `n` bytes. Returns a view into the source when they lie in one segment, else a copy.
   * @param {number} n
   * @returns {Uint8Array}
   */
  #take(n) {
    if (n > this.remaining) throw serializationError('length', `${n} bytes declared, ${this.remaining} remain`);
    this.#tookCopy = false;
    if (n === 0) return EMPTY;
    if (this.#pos >= this.#seg.length) this.#nextSegment();
    const p = this.#pos;
    if (p + n <= this.#seg.length) {
      this.#pos = p + n;
      return this.#seg.subarray(p, p + n);
    }
    const out = new Uint8Array(n);
    this.#copyInto(out, n);
    this.#tookCopy = true;
    return out;
  }

  /** @param {number} n */
  #advance(n) {
    if (n > this.remaining) throw truncated();
    let left = n;
    while (left > 0) {
      if (this.#pos >= this.#seg.length) this.#nextSegment();
      const take = Math.min(left, this.#seg.length - this.#pos);
      this.#pos += take;
      left -= take;
    }
  }

  /** @returns {number} */
  #u16() {
    const o = this.#fixed(2);
    return this.#fixedView.getUint16(o);
  }

  /** @returns {number} */
  #u32() {
    const o = this.#fixed(4);
    return this.#fixedView.getUint32(o);
  }

  // ---------------------------------------------------------------- Depth and limits

  /**
   * Enters a nested container: call before reading the array or map of a sequence, set, map, tuple,
   * `KeyValuePair`, object or union, and {@link exit} after it. Throws (`depth`) past `maxDepth`.
   * @returns {void}
   */
  enter() {
    if (++this.#depth > this.#options.maxDepth) {
      this.#depth--;
      throw serializationError('depth', `nesting deeper than maxDepth ${this.#options.maxDepth}`);
    }
  }

  /** Leaves the container entered last. @returns {void} */
  exit() {
    this.#depth--;
  }

  /** @param {number} count */
  #countElements(count) {
    const max = this.#options.maxElements;
    if (max > 0 && (this.#elements += count) > max) {
      throw serializationError('elements', `more than maxElements ${max} array elements and map entries`);
    }
  }

  /** Throws (`trailing`) unless every byte was read. @returns {void} */
  ensureEnd() {
    if (this.remaining > 0) throw serializationError('trailing', `${this.remaining} bytes after the value`);
  }

  // ---------------------------------------------------------------- Nil and bool

  /** The next type code, not consumed. Throws (`truncated`) at the end. @returns {number} */
  peekCode() {
    const b = this.#peek();
    if (b < 0) throw truncated();
    return b;
  }

  /** True when the next value is nil (not consumed). @returns {boolean} */
  get isNil() {
    return this.#peek() === 0xc0;
  }

  /** Consumes a nil and returns true, or returns false and consumes nothing. @returns {boolean} */
  tryReadNil() {
    if (this.#peek() !== 0xc0) return false;
    this.#pos++;
    return true;
  }

  /** Reads a nil. @returns {void} */
  readNil() {
    const code = this.#u8();
    if (code !== 0xc0) throw unexpected(code, 'nil');
  }

  /** @returns {boolean} */
  readBool() {
    const code = this.#u8();
    if (code === 0xc3) return true;
    if (code === 0xc2) return false;
    throw unexpected(code, 'a bool');
  }

  // ---------------------------------------------------------------- Integers

  /**
   * Reads any integer form as a bigint (int64 or uint64 range).
   * @returns {bigint}
   */
  #readBigInteger() {
    const code = this.#u8();
    if (code <= 0x7f) return BigInt(code);
    if (code >= 0xe0) return BigInt(code - 0x100);
    switch (code) {
      case 0xcc: return BigInt(this.#u8());
      case 0xcd: return BigInt(this.#u16());
      case 0xce: return BigInt(this.#u32());
      case 0xcf: { const o = this.#fixed(8); return this.#fixedView.getBigUint64(o); }
      case 0xd0: { const o = this.#fixed(1); return BigInt(this.#fixedView.getInt8(o)); }
      case 0xd1: { const o = this.#fixed(2); return BigInt(this.#fixedView.getInt16(o)); }
      case 0xd2: { const o = this.#fixed(4); return BigInt(this.#fixedView.getInt32(o)); }
      case 0xd3: { const o = this.#fixed(8); return this.#fixedView.getBigInt64(o); }
      default: throw unexpected(code, 'an integer');
    }
  }

  /**
   * Reads any integer form as a `number`. Throws (`range`) when the value is not a safe integer.
   * @returns {number}
   */
  readInt() {
    const code = this.#peek();
    if (code >= 0 && code <= 0x7f) {
      this.#pos++;
      return code;
    }
    if (code >= 0xe0) {
      this.#pos++;
      return code - 0x100;
    }
    switch (code) {
      case 0xcc: this.#pos++; return this.#u8();
      case 0xcd: this.#pos++; return this.#u16();
      case 0xce: this.#pos++; return this.#u32();
      case 0xd0: { this.#pos++; const o = this.#fixed(1); return this.#fixedView.getInt8(o); }
      case 0xd1: { this.#pos++; const o = this.#fixed(2); return this.#fixedView.getInt16(o); }
      case 0xd2: { this.#pos++; const o = this.#fixed(4); return this.#fixedView.getInt32(o); }
    }
    const v = this.#readBigInteger();
    if (v < BigInt(Number.MIN_SAFE_INTEGER) || v > BigInt(Number.MAX_SAFE_INTEGER)) {
      throw serializationError('range', `the integer ${v} is not a safe integer`);
    }
    return Number(v);
  }

  /**
   * Reads an integer and checks it is within `[min, max]` (`range`).
   * @param {number} min
   * @param {number} max
   * @param {string} name Type name for the message.
   * @returns {number}
   */
  #readRanged(min, max, name) {
    const code = this.#peek();
    let v;
    if (code >= 0 && code <= 0x7f) {
      this.#pos++;
      v = code;
    } else {
      v = this.readInt();
    }
    if (v < min || v > max) throw serializationError('range', `${v} is outside the ${name} range`);
    return v;
  }

  /** Reads a C# `sbyte`. @returns {number} */
  readSByte() { return this.#readRanged(-0x80, 0x7f, 'sbyte'); }

  /** Reads a C# `byte`. @returns {number} */
  readByte() { return this.#readRanged(0, 0xff, 'byte'); }

  /** Reads a C# `short`. @returns {number} */
  readInt16() { return this.#readRanged(-0x8000, 0x7fff, 'short'); }

  /** Reads a C# `ushort` (also `char` and union tags). @returns {number} */
  readUInt16() { return this.#readRanged(0, 0xffff, 'ushort'); }

  /** Reads a C# `int`. @returns {number} */
  readInt32() { return this.#readRanged(-0x80000000, 0x7fffffff, 'int'); }

  /** Reads a C# `uint`. @returns {number} */
  readUInt32() { return this.#readRanged(0, 0xffffffff, 'uint'); }

  /** Reads a C# `long`. @returns {bigint} */
  readInt64() {
    const v = this.#readBigInteger();
    if (v > 0x7fffffffffffffffn) throw serializationError('range', `${v} is outside the long range`);
    return v;
  }

  /** Reads a C# `ulong`. @returns {bigint} */
  readUInt64() {
    const v = this.#readBigInteger();
    if (v < 0n) throw serializationError('range', `${v} is outside the ulong range`);
    return v;
  }

  // ---------------------------------------------------------------- Floats

  /**
   * Reads float32 or float64, or any integer form converted to the nearest double (§10.6). The caller rounds to
   * float32 or half when the declared type is narrower.
   * @returns {number}
   */
  readFloat() {
    const code = this.#peek();
    if (code === 0xcb) {
      this.#pos++;
      const o = this.#fixed(8);
      return this.#fixedView.getFloat64(o);
    }
    if (code === 0xca) {
      this.#pos++;
      const o = this.#fixed(4);
      return this.#fixedView.getFloat32(o);
    }
    if (code < 0) throw truncated();
    try {
      return Number(this.#readBigInteger());
    } catch (e) {
      throw /** @type {any} */ (e).kind === 'type' ? unexpected(code, 'a float') : e;
    }
  }

  // ---------------------------------------------------------------- Strings and binary

  /**
   * Reads a string, or null for nil. Invalid UTF-8 throws (`utf8`) with `strictUtf8`; otherwise each maximal
   * invalid subpart becomes U+FFFD.
   * @returns {string | null}
   */
  readString() {
    const code = this.#u8();
    let n;
    if (code >= 0xa0 && code <= 0xbf) n = code & 0x1f;
    else if (code === 0xc0) return null;
    else if (code === 0xd9) n = this.#u8();
    else if (code === 0xda) n = this.#u16();
    else if (code === 0xdb) n = this.#u32();
    else throw unexpected(code, 'a string');
    if (n === 0) return '';
    const bytes = this.#take(n);
    if (n <= SHORT_STRING) {
      let s = '';
      let i = 0;
      for (; i < n; i++) {
        const b = bytes[i];
        if (b >= 0x80) break;
        s += String.fromCharCode(b);
      }
      if (i === n) return s;
    }
    if (!this.#options.strictUtf8) return lenientDecoder.decode(bytes);
    try {
      return strictDecoder.decode(bytes);
    } catch (e) {
      throw serializationError('utf8', 'invalid UTF-8 in a string', e);
    }
  }

  /**
   * Reads the length of a bin, or -1 for nil, leaving the reader at the bytes. Also accepts the str forms (older
   * writers sent bytes as raw strings).
   * @returns {number}
   */
  readBinHeader() {
    const code = this.#u8();
    let n;
    switch (code) {
      case 0xc0: return -1;
      case 0xc4: case 0xd9: n = this.#u8(); break;
      case 0xc5: case 0xda: n = this.#u16(); break;
      case 0xc6: case 0xdb: n = this.#u32(); break;
      default:
        if (code >= 0xa0 && code <= 0xbf) {
          n = code & 0x1f;
          break;
        }
        throw unexpected(code, 'bin');
    }
    if (n > this.remaining) throw serializationError('length', `a bin of ${n} bytes; ${this.remaining} remain`);
    return n;
  }

  /** Reads bin (or str) as a new array, or null for nil. @returns {Uint8Array | null} */
  readBin() {
    const n = this.readBinHeader();
    if (n < 0) return null;
    return this.readBytes(n);
  }

  /**
   * Consumes `length` raw bytes (not a MessagePack value) as a new array.
   * @param {number} length
   * @returns {Uint8Array}
   */
  readBytes(length) {
    const bytes = this.#take(length);
    return this.#tookCopy ? bytes : bytes.slice();
  }

  // ---------------------------------------------------------------- Containers

  /**
   * Reads an array header and returns its element count, checked against the remaining bytes (`length`) and
   * `maxElements` (`elements`). With `expected`, a different count throws (`count`).
   * @param {number} [expected]
   * @returns {number}
   */
  readArrayHeader(expected) {
    const code = this.#u8();
    let n;
    if (code >= 0x90 && code <= 0x9f) n = code & 0x0f;
    else if (code === 0xdc) n = this.#u16();
    else if (code === 0xdd) n = this.#u32();
    else throw unexpected(code, 'an array');
    if (n > this.remaining) throw serializationError('length', `an array of ${n} elements; ${this.remaining} bytes remain`);
    this.#countElements(n);
    if (expected !== undefined && n !== expected) {
      throw serializationError('count', `an array of ${n} elements where ${expected} are expected`);
    }
    return n;
  }

  /**
   * Reads a map header and returns its entry count, checked against the remaining bytes (`length`) and
   * `maxElements` (each entry counts its key and its value).
   * @returns {number}
   */
  readMapHeader() {
    const code = this.#u8();
    let n;
    if (code >= 0x80 && code <= 0x8f) n = code & 0x0f;
    else if (code === 0xde) n = this.#u16();
    else if (code === 0xdf) n = this.#u32();
    else throw unexpected(code, 'a map');
    if (n * 2 > this.remaining) throw serializationError('length', `a map of ${n} entries; ${this.remaining} bytes remain`);
    this.#countElements(n * 2);
    return n;
  }

  /**
   * Reads an ext header and returns its type (-128–127) and data length, checked against the remaining bytes.
   * @returns {{ type: number, length: number }}
   */
  readExtHeader() {
    const code = this.#u8();
    let n;
    switch (code) {
      case 0xd4: n = 1; break;
      case 0xd5: n = 2; break;
      case 0xd6: n = 4; break;
      case 0xd7: n = 8; break;
      case 0xd8: n = 16; break;
      case 0xc7: n = this.#u8(); break;
      case 0xc8: n = this.#u16(); break;
      case 0xc9: n = this.#u32(); break;
      default: throw unexpected(code, 'ext');
    }
    const t = this.#u8();
    if (n > this.remaining) throw serializationError('length', `an ext of ${n} bytes; ${this.remaining} remain`);
    return { type: t > 0x7f ? t - 0x100 : t, length: n };
  }

  /** Reads an ext value; `data` is a new array. @returns {{ type: number, data: Uint8Array }} */
  readExt() {
    const { type, length } = this.readExtHeader();
    return { type, data: this.readBytes(length) };
  }

  // ---------------------------------------------------------------- Skip and raw values

  /**
   * Skips one complete value of any type. Iterative: nesting costs no stack. Counts neither depth nor elements.
   * @returns {void}
   */
  skip() {
    let pending = 1;
    while (pending > 0) {
      pending--;
      const code = this.#u8();
      if (code <= 0x7f || code >= 0xe0 || code === 0xc0 || code === 0xc2 || code === 0xc3) continue;
      if (code <= 0x8f) { pending += (code & 0x0f) * 2; continue; }
      if (code <= 0x9f) { pending += code & 0x0f; continue; }
      if (code <= 0xbf) { this.#advance(code & 0x1f); continue; }
      switch (code) {
        case 0xcc: case 0xd0: this.#advance(1); break;
        case 0xcd: case 0xd1: this.#advance(2); break;
        case 0xce: case 0xd2: case 0xca: this.#advance(4); break;
        case 0xcf: case 0xd3: case 0xcb: this.#advance(8); break;
        case 0xc4: case 0xd9: this.#advance(this.#u8()); break;
        case 0xc5: case 0xda: this.#advance(this.#u16()); break;
        case 0xc6: case 0xdb: this.#advance(this.#u32()); break;
        case 0xd4: this.#advance(2); break;
        case 0xd5: this.#advance(3); break;
        case 0xd6: this.#advance(5); break;
        case 0xd7: this.#advance(9); break;
        case 0xd8: this.#advance(17); break;
        case 0xc7: this.#advance(this.#u8() + 1); break;
        case 0xc8: this.#advance(this.#u16() + 1); break;
        case 0xc9: this.#advance(this.#u32() + 1); break;
        case 0xdc: pending += this.#u16(); break;
        case 0xdd: pending += this.#u32(); break;
        case 0xde: pending += this.#u16() * 2; break;
        case 0xdf: pending += this.#u32() * 2; break;
        default: throw unexpected(code, 'a value');
      }
      // Every pending value needs at least one byte: a count beyond the input is truncated input.
      if (pending > this.remaining) throw truncated();
    }
  }

  /**
   * Consumes one complete value and returns its bytes (a new array): an embedded value carried as is, such as the
   * arguments of an invocation.
   * @returns {Uint8Array}
   */
  readRaw() {
    const startSegment = this.#segmentIndex;
    const startPos = this.#pos;
    const start = this.consumed;
    this.skip();
    const length = this.consumed - start;
    const first = this.#segments[startSegment] ?? EMPTY;
    if (startPos + length <= first.length) return first.slice(startPos, startPos + length);
    const out = new Uint8Array(length);
    let o = 0;
    let index = startSegment;
    let pos = startPos;
    while (o < length) {
      const seg = this.#segments[index];
      const take = Math.min(length - o, seg.length - pos);
      out.set(seg.subarray(pos, pos + take), o);
      o += take;
      index++;
      pos = 0;
    }
    return out;
  }
}
