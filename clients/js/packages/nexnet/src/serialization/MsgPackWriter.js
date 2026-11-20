/**
 * The MessagePack writer (protocol specification §10.1): canonical, smallest-form encoding into a growable buffer.
 * @module
 */

const encoder = new TextEncoder();

/** Strings shorter than this many UTF-16 units are encoded by a loop; longer ones by `TextEncoder.encodeInto`. */
const SHORT_STRING = 32;

const UINT32_MAX = 0xffffffff;
const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;
const UINT64_MAX = 2n ** 64n - 1n;

/**
 * Encodes `value` (fewer than {@link SHORT_STRING} units) as UTF-8 at `buf[offset]`, as `TextEncoder` does: a lone
 * surrogate becomes U+FFFD. The caller reserves 3 bytes per unit.
 * @param {string} value
 * @param {Uint8Array} buf
 * @param {number} offset
 * @returns {number} Bytes written.
 */
function encodeShort(value, buf, offset) {
  let p = offset;
  const n = value.length;
  for (let i = 0; i < n; i++) {
    let c = value.charCodeAt(i);
    if (c < 0x80) {
      buf[p++] = c;
    } else if (c < 0x800) {
      buf[p++] = 0xc0 | (c >> 6);
      buf[p++] = 0x80 | (c & 0x3f);
    } else {
      if (c >= 0xd800 && c <= 0xdfff) {
        const next = i + 1 < n ? value.charCodeAt(i + 1) : 0;
        if (c <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) {
          const cp = 0x10000 + ((c - 0xd800) << 10) + (next - 0xdc00);
          i++;
          buf[p++] = 0xf0 | (cp >> 18);
          buf[p++] = 0x80 | ((cp >> 12) & 0x3f);
          buf[p++] = 0x80 | ((cp >> 6) & 0x3f);
          buf[p++] = 0x80 | (cp & 0x3f);
          continue;
        }
        c = 0xfffd;
      }
      buf[p++] = 0xe0 | (c >> 12);
      buf[p++] = 0x80 | ((c >> 6) & 0x3f);
      buf[p++] = 0x80 | (c & 0x3f);
    }
  }
  return p - offset;
}

/**
 * @param {number} length A str length in bytes.
 * @returns {number} The size of its header.
 */
function strHeaderSize(length) {
  return length < 32 ? 1 : length <= 0xff ? 2 : length <= 0xffff ? 3 : 5;
}

/**
 * @param {unknown} count
 * @param {string} what
 */
function checkCount(count, what) {
  if (typeof count !== 'number' || !Number.isInteger(count) || count < 0 || count > UINT32_MAX) {
    throw new RangeError(`${what} count must be an integer from 0 to ${UINT32_MAX}; got ${String(count)}`);
  }
}

/**
 * Appends MessagePack to a growable buffer, always choosing the smallest (canonical) encoding (§10.1).
 * Multi-byte MessagePack values are big-endian. Methods throw `TypeError` for a value of the wrong JS type and
 * `RangeError` for one outside the MessagePack or target range; the buffer is unchanged then.
 */
export class MsgPackWriter {
  /** @type {Uint8Array} */
  #buf;
  /** @type {DataView} */
  #view;
  #len = 0;

  /**
   * @param {{ initialCapacity?: number }} [options] `initialCapacity` defaults to 256 bytes.
   */
  constructor(options) {
    const capacity = Math.max(16, options?.initialCapacity ?? 256);
    this.#buf = new Uint8Array(capacity);
    this.#view = new DataView(this.#buf.buffer);
  }

  /** Number of bytes written so far. @returns {number} */
  get length() {
    return this.#len;
  }

  /** @param {number} n */
  #ensure(n) {
    if (this.#len + n > this.#buf.length) this.#grow(n);
  }

  /** @param {number} n */
  #grow(n) {
    let size = this.#buf.length * 2;
    while (size < this.#len + n) size *= 2;
    const next = new Uint8Array(size);
    next.set(this.#buf.subarray(0, this.#len));
    this.#buf = next;
    this.#view = new DataView(next.buffer);
  }

  /** @param {number} b */
  #byte(b) {
    this.#ensure(1);
    this.#buf[this.#len++] = b;
  }

  /** @param {number} code @param {number} b */
  #code8(code, b) {
    this.#ensure(2);
    this.#buf[this.#len] = code;
    this.#buf[this.#len + 1] = b;
    this.#len += 2;
  }

  /** @param {number} code @param {number} v */
  #code16(code, v) {
    this.#ensure(3);
    this.#buf[this.#len] = code;
    this.#view.setUint16(this.#len + 1, v);
    this.#len += 3;
  }

  /** @param {number} code @param {number} v */
  #code32(code, v) {
    this.#ensure(5);
    this.#buf[this.#len] = code;
    this.#view.setUint32(this.#len + 1, v);
    this.#len += 5;
  }

  /** Writes nil. @returns {void} */
  writeNil() {
    this.#byte(0xc0);
  }

  /** @param {boolean} value @returns {void} */
  writeBool(value) {
    if (typeof value !== 'boolean') throw new TypeError(`expected a boolean; got ${typeof value}`);
    this.#byte(value ? 0xc3 : 0xc2);
  }

  /**
   * Writes an integer that fits in a JS `number`: C# sbyte through uint, and `long`/`ulong` in their opt-in
   * checked-number mapping (`[NexusJsNumber]`). The target range is the formatter's job; this checks only that `value` is a safe
   * integer.
   * @param {number} value
   * @returns {void}
   */
  writeInt(value) {
    if (typeof value !== 'number') throw new TypeError(`expected an integer number; got ${typeof value}`);
    if (!Number.isSafeInteger(value)) throw new RangeError(`not a safe integer: ${value}`);
    if (value >= 0) {
      if (value < 0x80) this.#byte(value);
      else if (value <= 0xff) this.#code8(0xcc, value);
      else if (value <= 0xffff) this.#code16(0xcd, value);
      else if (value <= UINT32_MAX) this.#code32(0xce, value);
      else this.#u64(0xcf, BigInt(value));
    } else if (value >= -32) {
      this.#byte(value & 0xff);
    } else if (value >= -0x80) {
      this.#code8(0xd0, value & 0xff);
    } else if (value >= -0x8000) {
      this.#code16(0xd1, value & 0xffff);
    } else if (value >= -0x80000000) {
      this.#code32(0xd2, value >>> 0);
    } else {
      this.#u64(0xd3, BigInt.asUintN(64, BigInt(value)));
    }
  }

  /** @param {number} code @param {bigint} bits */
  #u64(code, bits) {
    this.#ensure(9);
    this.#buf[this.#len] = code;
    this.#view.setBigUint64(this.#len + 1, bits);
    this.#len += 9;
  }

  /** Writes a signed 64-bit integer (C# `long`). @param {bigint} value @returns {void} */
  writeInt64(value) {
    if (typeof value !== 'bigint') throw new TypeError(`expected a bigint; got ${typeof value}`);
    if (value < INT64_MIN || value > INT64_MAX) throw new RangeError(`outside the int64 range: ${value}`);
    if (value >= -0x80000000n && value <= 0xffffffffn) this.writeInt(Number(value));
    else this.#u64(value >= 0n ? 0xcf : 0xd3, BigInt.asUintN(64, value));
  }

  /** Writes an unsigned 64-bit integer (C# `ulong`). @param {bigint} value @returns {void} */
  writeUInt64(value) {
    if (typeof value !== 'bigint') throw new TypeError(`expected a bigint; got ${typeof value}`);
    if (value < 0n || value > UINT64_MAX) throw new RangeError(`outside the uint64 range: ${value}`);
    if (value <= 0xffffffffn) this.writeInt(Number(value));
    else this.#u64(0xcf, value);
  }

  /** Writes float32 (C# `float`, and `Half` widened). The value is rounded to float32. @param {number} value @returns {void} */
  writeFloat32(value) {
    if (typeof value !== 'number') throw new TypeError(`expected a number; got ${typeof value}`);
    this.#ensure(5);
    this.#buf[this.#len] = 0xca;
    this.#view.setFloat32(this.#len + 1, value);
    this.#len += 5;
  }

  /** Writes float64 (C# `double`). @param {number} value @returns {void} */
  writeFloat64(value) {
    if (typeof value !== 'number') throw new TypeError(`expected a number; got ${typeof value}`);
    this.#ensure(9);
    this.#buf[this.#len] = 0xcb;
    this.#view.setFloat64(this.#len + 1, value);
    this.#len += 9;
  }

  /**
   * Writes a UTF-8 string, or nil for null. A lone surrogate is written as U+FFFD, as C# and `TextEncoder` do.
   * @param {string | null} value
   * @returns {void}
   */
  writeString(value) {
    if (value === null) {
      this.writeNil();
      return;
    }
    if (typeof value !== 'string') throw new TypeError(`expected a string; got ${typeof value}`);
    const n = value.length;
    if (n < SHORT_STRING) {
      // At most 93 bytes: the header is fixstr or str8. Encode after a 2-byte header, then move left for fixstr.
      this.#ensure(2 + n * 3);
      const start = this.#len + 2;
      const written = encodeShort(value, this.#buf, start);
      if (written < 32) {
        this.#buf.copyWithin(this.#len + 1, start, start + written);
        this.#buf[this.#len] = 0xa0 | written;
        this.#len += 1 + written;
      } else {
        this.#buf[this.#len] = 0xd9;
        this.#buf[this.#len + 1] = written;
        this.#len += 2 + written;
      }
      return;
    }
    // Reserve the header for the worst case (3 bytes per unit), encode, then move left if the header is smaller.
    const reserved = strHeaderSize(n * 3);
    this.#ensure(reserved + n * 3);
    const start = this.#len + reserved;
    const written = /** @type {number} */ (encoder.encodeInto(value, this.#buf.subarray(start)).written);
    const header = strHeaderSize(written);
    if (header !== reserved) this.#buf.copyWithin(this.#len + header, start, start + written);
    this.#strHeader(written);
    this.#len += written;
  }

  /** @param {number} length */
  #strHeader(length) {
    if (length < 32) this.#byte(0xa0 | length);
    else if (length <= 0xff) this.#code8(0xd9, length);
    else if (length <= 0xffff) this.#code16(0xda, length);
    else this.#code32(0xdb, length);
  }

  /** Writes bin, or nil for null. @param {Uint8Array | null} value @returns {void} */
  writeBin(value) {
    if (value === null) {
      this.writeNil();
      return;
    }
    if (!(value instanceof Uint8Array)) throw new TypeError('expected a Uint8Array');
    this.writeBinHeader(value.length);
    this.writeRaw(value);
  }

  /**
   * Writes the header of a bin of `length` bytes; the caller writes the bytes with {@link writeRaw}.
   * @param {number} length
   * @returns {void}
   */
  writeBinHeader(length) {
    checkCount(length, 'bin byte');
    if (length <= 0xff) this.#code8(0xc4, length);
    else if (length <= 0xffff) this.#code16(0xc5, length);
    else this.#code32(0xc6, length);
  }

  /** @param {number} count @returns {void} */
  writeArrayHeader(count) {
    checkCount(count, 'array');
    if (count < 16) this.#byte(0x90 | count);
    else if (count <= 0xffff) this.#code16(0xdc, count);
    else this.#code32(0xdd, count);
  }

  /** @param {number} count Number of key/value pairs. @returns {void} */
  writeMapHeader(count) {
    checkCount(count, 'map');
    if (count < 16) this.#byte(0x80 | count);
    else if (count <= 0xffff) this.#code16(0xde, count);
    else this.#code32(0xdf, count);
  }

  /**
   * Writes an ext header; the caller writes the `length` data bytes with {@link writeRaw}.
   * @param {number} type Ext type, -128–127.
   * @param {number} length
   * @returns {void}
   */
  writeExtHeader(type, length) {
    if (typeof type !== 'number' || !Number.isInteger(type) || type < -128 || type > 127) {
      throw new RangeError(`ext type must be an integer from -128 to 127; got ${String(type)}`);
    }
    checkCount(length, 'ext byte');
    const t = type & 0xff;
    switch (length) {
      case 1: this.#code8(0xd4, t); return;
      case 2: this.#code8(0xd5, t); return;
      case 4: this.#code8(0xd6, t); return;
      case 8: this.#code8(0xd7, t); return;
      case 16: this.#code8(0xd8, t); return;
    }
    if (length <= 0xff) {
      this.#code8(0xc7, length);
    } else if (length <= 0xffff) {
      this.#code16(0xc8, length);
    } else {
      this.#code32(0xc9, length);
    }
    this.#byte(t);
  }

  /** Writes an ext value. @param {number} type Ext type, -128–127. @param {Uint8Array} data @returns {void} */
  writeExt(type, data) {
    if (!(data instanceof Uint8Array)) throw new TypeError('expected a Uint8Array');
    this.writeExtHeader(type, data.length);
    this.writeRaw(data);
  }

  /** Appends bytes as they are (already-encoded MessagePack, or the data after a header). @param {Uint8Array} bytes @returns {void} */
  writeRaw(bytes) {
    if (!(bytes instanceof Uint8Array)) throw new TypeError('expected a Uint8Array');
    this.#ensure(bytes.length);
    this.#buf.set(bytes, this.#len);
    this.#len += bytes.length;
  }

  /** Returns a view of the bytes written so far, valid until the next write or {@link reset}. @returns {Uint8Array} */
  toUint8Array() {
    return this.#buf.subarray(0, this.#len);
  }

  /** Clears the buffer for reuse; the capacity is kept. @returns {void} */
  reset() {
    this.#len = 0;
  }
}
