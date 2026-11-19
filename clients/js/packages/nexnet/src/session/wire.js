/**
 * The MessagePack subset the session needs for protocol message bodies (§4), and the encoders and decoders of the
 * session-level messages. Payload values (arguments, results, channel items) use the J02 codec, not this.
 *
 * Integers are written canonically, as C# MessagePack does: non-negative values in the smallest unsigned format,
 * negative values in the smallest signed format. Readers accept any integer format whose value is in range.
 * @module
 */

import { MessageType } from './protocol.js';

const encoder = new TextEncoder();
const strictDecoder = new TextDecoder('utf-8', { fatal: true });

/** Thrown when a body does not decode; the session maps it to `ProtocolError`. */
export class WireFormatError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'WireFormatError';
  }
}

/** Appends MessagePack to a growable buffer. */
export class BodyWriter {
  /** @type {Uint8Array} */
  #buf;
  #len = 0;
  /** @type {DataView} */
  #view;

  /** @param {number} [capacity] */
  constructor(capacity = 64) {
    this.#buf = new Uint8Array(capacity);
    this.#view = new DataView(this.#buf.buffer);
  }

  /** @param {number} n */
  #ensure(n) {
    if (this.#len + n <= this.#buf.length) return;
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

  /** @param {number} marker @param {number} v */
  #u16(marker, v) {
    this.#ensure(3);
    this.#buf[this.#len] = marker;
    this.#view.setUint16(this.#len + 1, v);
    this.#len += 3;
  }

  /** @param {number} marker @param {number} v */
  #u32(marker, v) {
    this.#ensure(5);
    this.#buf[this.#len] = marker;
    this.#view.setUint32(this.#len + 1, v);
    this.#len += 5;
  }

  /** @param {number} count @returns {this} */
  writeArrayHeader(count) {
    if (count < 16) this.#byte(0x90 | count);
    else if (count <= 0xffff) this.#u16(0xdc, count);
    else this.#u32(0xdd, count);
    return this;
  }

  /** @returns {this} */
  writeNil() {
    this.#byte(0xc0);
    return this;
  }

  /**
   * Writes an integer: a safe-integer `number` or a 64-bit `bigint`.
   * @param {number | bigint} value
   * @returns {this}
   */
  writeInt(value) {
    if (typeof value === 'bigint') {
      if (value >= BigInt(Number.MIN_SAFE_INTEGER) && value <= BigInt(Number.MAX_SAFE_INTEGER)) return this.writeInt(Number(value));
      if (value > 0xffffffffffffffffn || value < -0x8000000000000000n) throw new RangeError(`integer out of 64-bit range: ${value}`);
      return this.#write64(value);
    }
    if (!Number.isSafeInteger(value)) throw new RangeError(`not a safe integer: ${value}`);
    if (value >= 0) {
      if (value < 0x80) this.#byte(value);
      else if (value <= 0xff) { this.#ensure(2); this.#buf[this.#len++] = 0xcc; this.#buf[this.#len++] = value; }
      else if (value <= 0xffff) this.#u16(0xcd, value);
      else if (value <= 0xffffffff) this.#u32(0xce, value);
      else this.#write64(BigInt(value));
      return this;
    }
    if (value >= -32) this.#byte(value & 0xff);
    else if (value >= -0x80) { this.#ensure(2); this.#buf[this.#len++] = 0xd0; this.#buf[this.#len++] = value & 0xff; }
    else if (value >= -0x8000) { this.#ensure(3); this.#buf[this.#len] = 0xd1; this.#view.setInt16(this.#len + 1, value); this.#len += 3; }
    else if (value >= -0x80000000) { this.#ensure(5); this.#buf[this.#len] = 0xd2; this.#view.setInt32(this.#len + 1, value); this.#len += 5; }
    else this.#write64(BigInt(value));
    return this;
  }

  /** @param {bigint} value @returns {this} */
  #write64(value) {
    this.#ensure(9);
    this.#buf[this.#len] = value >= 0n ? 0xcf : 0xd3;
    if (value >= 0n) this.#view.setBigUint64(this.#len + 1, value);
    else this.#view.setBigInt64(this.#len + 1, value);
    this.#len += 9;
    return this;
  }

  /** Writes a UTF-8 string, or nil for null. @param {string | null} value @returns {this} */
  writeString(value) {
    if (value === null) return this.writeNil();
    const bytes = encoder.encode(value);
    const n = bytes.length;
    if (n < 32) this.#byte(0xa0 | n);
    else if (n <= 0xff) { this.#ensure(2); this.#buf[this.#len++] = 0xd9; this.#buf[this.#len++] = n; }
    else if (n <= 0xffff) this.#u16(0xda, n);
    else this.#u32(0xdb, n);
    this.#raw(bytes);
    return this;
  }

  /** Writes bin, or nil for null. @param {Uint8Array | null} value @returns {this} */
  writeBin(value) {
    if (value === null) return this.writeNil();
    const n = value.length;
    if (n <= 0xff) { this.#ensure(2); this.#buf[this.#len++] = 0xc4; this.#buf[this.#len++] = n; }
    else if (n <= 0xffff) this.#u16(0xc5, n);
    else this.#u32(0xc6, n);
    this.#raw(value);
    return this;
  }

  /** @param {Uint8Array} bytes */
  #raw(bytes) {
    this.#ensure(bytes.length);
    this.#buf.set(bytes, this.#len);
    this.#len += bytes.length;
  }

  /** @returns {number} */
  get length() {
    return this.#len;
  }

  /** A copy of the bytes written. @returns {Uint8Array} */
  toUint8Array() {
    return this.#buf.slice(0, this.#len);
  }
}

/** Reads MessagePack from one body. Every read checks bounds and throws {@link WireFormatError}. */
export class BodyReader {
  #buf;
  #pos = 0;
  #view;

  /** @param {Uint8Array} body */
  constructor(body) {
    this.#buf = body;
    this.#view = new DataView(body.buffer, body.byteOffset, body.byteLength);
  }

  /** @returns {number} */
  get remaining() {
    return this.#buf.length - this.#pos;
  }

  /** @param {number} n */
  #need(n) {
    if (this.#pos + n > this.#buf.length) throw new WireFormatError('unexpected end of body');
  }

  /** @returns {number} */
  #u8() {
    this.#need(1);
    return this.#buf[this.#pos++];
  }

  /** @returns {number} */
  #u16() {
    this.#need(2);
    const v = this.#view.getUint16(this.#pos);
    this.#pos += 2;
    return v;
  }

  /** @returns {number} */
  #u32() {
    this.#need(4);
    const v = this.#view.getUint32(this.#pos);
    this.#pos += 4;
    return v;
  }

  /** @returns {number} The next format byte, not consumed. */
  peek() {
    this.#need(1);
    return this.#buf[this.#pos];
  }

  /** @returns {boolean} True (and consumes it) when the next value is nil. */
  tryReadNil() {
    if (this.peek() !== 0xc0) return false;
    this.#pos++;
    return true;
  }

  /** @returns {number} */
  readArrayHeader() {
    const b = this.#u8();
    if ((b & 0xf0) === 0x90) return b & 0x0f;
    if (b === 0xdc) return this.#u16();
    if (b === 0xdd) return this.#u32();
    throw new WireFormatError(`expected an array, found 0x${b.toString(16)}`);
  }

  /** Reads any MessagePack integer as a bigint. @returns {bigint} */
  readBigInt() {
    const b = this.#u8();
    if (b <= 0x7f) return BigInt(b);
    if (b >= 0xe0) return BigInt(b - 0x100);
    switch (b) {
      case 0xcc: return BigInt(this.#u8());
      case 0xcd: return BigInt(this.#u16());
      case 0xce: return BigInt(this.#u32());
      case 0xcf: { this.#need(8); const v = this.#view.getBigUint64(this.#pos); this.#pos += 8; return v; }
      case 0xd0: { this.#need(1); const v = this.#view.getInt8(this.#pos); this.#pos += 1; return BigInt(v); }
      case 0xd1: { this.#need(2); const v = this.#view.getInt16(this.#pos); this.#pos += 2; return BigInt(v); }
      case 0xd2: { this.#need(4); const v = this.#view.getInt32(this.#pos); this.#pos += 4; return BigInt(v); }
      case 0xd3: { this.#need(8); const v = this.#view.getBigInt64(this.#pos); this.#pos += 8; return v; }
      default: throw new WireFormatError(`expected an integer, found 0x${b.toString(16)}`);
    }
  }

  /**
   * Reads an integer and checks it is in `[min, max]`.
   * @param {number} min @param {number} max
   * @returns {number}
   */
  readIntInRange(min, max) {
    const v = this.readBigInt();
    if (v < BigInt(min) || v > BigInt(max)) throw new WireFormatError(`integer ${v} out of range [${min}, ${max}]`);
    return Number(v);
  }

  /** @returns {number} */
  readInt32() { return this.readIntInRange(-0x80000000, 0x7fffffff); }
  /** @returns {number} */
  readUInt8() { return this.readIntInRange(0, 0xff); }
  /** @returns {number} */
  readUInt16() { return this.readIntInRange(0, 0xffff); }
  /** @returns {number} */
  readUInt32() { return this.readIntInRange(0, 0xffffffff); }

  /** @returns {bigint} */
  readInt64() {
    const v = this.readBigInt();
    if (v < -0x8000000000000000n || v > 0x7fffffffffffffffn) throw new WireFormatError(`integer ${v} out of int64 range`);
    return v;
  }

  /** Reads a strict UTF-8 string, or null for nil. @returns {string | null} */
  readString() {
    const b = this.#u8();
    let n;
    if (b === 0xc0) return null;
    if ((b & 0xe0) === 0xa0) n = b & 0x1f;
    else if (b === 0xd9) n = this.#u8();
    else if (b === 0xda) n = this.#u16();
    else if (b === 0xdb) n = this.#u32();
    else throw new WireFormatError(`expected a string, found 0x${b.toString(16)}`);
    this.#need(n);
    const bytes = this.#buf.subarray(this.#pos, this.#pos + n);
    this.#pos += n;
    try {
      return strictDecoder.decode(bytes);
    } catch {
      throw new WireFormatError('invalid UTF-8');
    }
  }

  /**
   * Reads the length of a bin, or -1 for nil, leaving the reader at the bytes.
   * @returns {number}
   */
  readBinHeader() {
    const b = this.#u8();
    if (b === 0xc0) return -1;
    if (b === 0xc4) return this.#u8();
    if (b === 0xc5) return this.#u16();
    if (b === 0xc6) return this.#u32();
    throw new WireFormatError(`expected bin, found 0x${b.toString(16)}`);
  }

  /**
   * Reads bin as a copy; nil reads as null.
   * @param {number} [maxLength] Checked against the header before the bytes are read.
   * @returns {Uint8Array | null}
   */
  readBin(maxLength = Infinity) {
    const n = this.readBinHeader();
    if (n < 0) return null;
    if (n > maxLength) throw new WireFormatError(`bin of ${n} bytes exceeds ${maxLength}`);
    this.#need(n);
    const bytes = this.#buf.slice(this.#pos, this.#pos + n);
    this.#pos += n;
    return bytes;
  }

  /** Skips one value of any type (depth-bounded). @param {number} [depth] @returns {void} */
  skip(depth = 0) {
    if (depth > 32) throw new WireFormatError('nesting too deep');
    const b = this.#u8();
    if (b <= 0x7f || b >= 0xe0 || b === 0xc0 || b === 0xc2 || b === 0xc3) return;
    if ((b & 0xe0) === 0xa0) return this.#skipBytes(b & 0x1f);
    if ((b & 0xf0) === 0x90) return this.#skipValues(b & 0x0f, depth);
    if ((b & 0xf0) === 0x80) return this.#skipValues((b & 0x0f) * 2, depth);
    switch (b) {
      case 0xcc: case 0xd0: return this.#skipBytes(1);
      case 0xcd: case 0xd1: return this.#skipBytes(2);
      case 0xce: case 0xd2: case 0xca: return this.#skipBytes(4);
      case 0xcf: case 0xd3: case 0xcb: return this.#skipBytes(8);
      case 0xc4: case 0xd9: return this.#skipBytes(this.#u8());
      case 0xc5: case 0xda: return this.#skipBytes(this.#u16());
      case 0xc6: case 0xdb: return this.#skipBytes(this.#u32());
      case 0xdc: return this.#skipValues(this.#u16(), depth);
      case 0xdd: return this.#skipValues(this.#u32(), depth);
      case 0xde: return this.#skipValues(this.#u16() * 2, depth);
      case 0xdf: return this.#skipValues(this.#u32() * 2, depth);
      case 0xd4: return this.#skipBytes(2);
      case 0xd5: return this.#skipBytes(3);
      case 0xd6: return this.#skipBytes(5);
      case 0xd7: return this.#skipBytes(9);
      case 0xd8: return this.#skipBytes(17);
      case 0xc7: return this.#skipBytes(this.#u8() + 1);
      case 0xc8: return this.#skipBytes(this.#u16() + 1);
      case 0xc9: return this.#skipBytes(this.#u32() + 1);
      default: throw new WireFormatError(`invalid format byte 0x${b.toString(16)}`);
    }
  }

  /** @param {number} n */
  #skipBytes(n) {
    this.#need(n);
    this.#pos += n;
  }

  /** @param {number} count @param {number} depth */
  #skipValues(count, depth) {
    // Each value needs at least one byte; reject impossible counts before looping.
    this.#need(count);
    for (let i = 0; i < count; i++) this.skip(depth + 1);
  }

  /** Throws unless the whole body was read (§4: readers reject trailing bytes). @returns {void} */
  end() {
    if (this.#pos !== this.#buf.length) throw new WireFormatError(`${this.#buf.length - this.#pos} trailing bytes`);
  }
}

/**
 * @param {BodyReader} reader
 * @param {number} expected
 */
function exactArray(reader, expected) {
  const n = reader.readArrayHeader();
  if (n !== expected) throw new WireFormatError(`expected an array of ${expected}, found ${n}`);
}

/**
 * ClientGreeting / ClientGreetingReconnection body (§4.1).
 *
 * @typedef {object} ClientGreeting
 * @property {string | null} version
 * @property {number} serverNexusHash
 * @property {number} clientNexusHash
 * @property {Uint8Array | null} authenticationToken Null is written as an empty token.
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 */

/**
 * Encodes a client greeting body. A missing token is sent as an empty bin (`c4 00`), as C# does (§5.4).
 * @param {ClientGreeting} greeting
 * @returns {Uint8Array}
 */
export function encodeClientGreeting(greeting) {
  return new BodyWriter(32 + (greeting.authenticationToken?.length ?? 0))
    .writeArrayHeader(6)
    .writeString(greeting.version)
    .writeInt(greeting.serverNexusHash)
    .writeInt(greeting.clientNexusHash)
    .writeBin(greeting.authenticationToken ?? new Uint8Array(0))
    .writeInt(greeting.maxMessageSize)
    .writeInt(greeting.invocationCredits)
    .toUint8Array();
}

/**
 * Decodes a client greeting body (used by tests and test servers). Nil token reads as an empty token.
 * @param {Uint8Array} body
 * @returns {ClientGreeting}
 */
export function decodeClientGreeting(body) {
  const r = new BodyReader(body);
  exactArray(r, 6);
  const greeting = {
    version: r.readString(),
    serverNexusHash: r.readInt32(),
    clientNexusHash: r.readInt32(),
    authenticationToken: r.readBin(8192) ?? new Uint8Array(0),
    maxMessageSize: r.readInt32(),
    invocationCredits: r.readInt32(),
  };
  r.end();
  return greeting;
}

/**
 * ServerGreeting body (§4.2). `idleTimeoutMs` is the S04 trailing field: null when the server does not send it.
 *
 * @typedef {object} ServerGreeting
 * @property {number} version The client nexus hash the server expects.
 * @property {bigint} clientId The session ID.
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 * @property {number | null} idleTimeoutMs
 */

/**
 * Encodes a server greeting body (used by tests and test servers). Writes the trailing `idleTimeoutMs` only when set.
 * @param {ServerGreeting | Omit<ServerGreeting, 'idleTimeoutMs'> & { idleTimeoutMs?: number | null }} greeting
 * @returns {Uint8Array}
 */
export function encodeServerGreeting(greeting) {
  const trailing = greeting.idleTimeoutMs !== undefined && greeting.idleTimeoutMs !== null;
  const w = new BodyWriter(32)
    .writeArrayHeader(trailing ? 5 : 4)
    .writeInt(greeting.version)
    .writeInt(greeting.clientId)
    .writeInt(greeting.maxMessageSize)
    .writeInt(greeting.invocationCredits);
  if (trailing) w.writeInt(/** @type {number} */ (greeting.idleTimeoutMs));
  return w.toUint8Array();
}

/**
 * Decodes a server greeting body. Four elements are required; a fifth is `idleTimeoutMs` (uint32 or nil); any further
 * trailing elements are skipped (trailing-field rule, train §4.5).
 * @param {Uint8Array} body
 * @returns {ServerGreeting}
 */
export function decodeServerGreeting(body) {
  const r = new BodyReader(body);
  const n = r.readArrayHeader();
  if (n < 4) throw new WireFormatError(`expected an array of at least 4, found ${n}`);
  const version = r.readInt32();
  const clientId = r.readInt64();
  const maxMessageSize = r.readInt32();
  const invocationCredits = r.readInt32();
  let idleTimeoutMs = null;
  if (n >= 5) idleTimeoutMs = r.tryReadNil() ? null : r.readUInt32();
  for (let i = 5; i < n; i++) r.skip();
  r.end();
  return { version, clientId, maxMessageSize, invocationCredits, idleTimeoutMs };
}

/** @param {number} credits @returns {Uint8Array} InvocationCredit body `[credits: uint16]` (§4.7). */
export function encodeInvocationCredit(credits) {
  return new BodyWriter(4).writeArrayHeader(1).writeInt(credits).toUint8Array();
}

/** @param {Uint8Array} body @returns {{ credits: number }} */
export function decodeInvocationCredit(body) {
  const r = new BodyReader(body);
  exactArray(r, 1);
  const credits = r.readUInt16();
  r.end();
  return { credits };
}

/** @param {number} invocationId @returns {Uint8Array} InvocationCancellation body `[invocationId: uint16]` (§4.5). */
export function encodeInvocationCancellation(invocationId) {
  return new BodyWriter(4).writeArrayHeader(1).writeInt(invocationId).toUint8Array();
}

/** @param {Uint8Array} body @returns {{ invocationId: number }} */
export function decodeInvocationCancellation(body) {
  const r = new BodyReader(body);
  exactArray(r, 1);
  const invocationId = r.readUInt16();
  r.end();
  return { invocationId };
}

/**
 * MessageAbort body `[invocationId: uint16, kind: uint8, reason: uint8]` (§4.9).
 * @param {number} invocationId @param {number} kind @param {number} reason
 * @returns {Uint8Array}
 */
export function encodeMessageAbort(invocationId, kind, reason) {
  return new BodyWriter(8).writeArrayHeader(3).writeInt(invocationId).writeInt(kind).writeInt(reason).toUint8Array();
}

/** @param {Uint8Array} body @returns {{ invocationId: number, kind: number, reason: number }} */
export function decodeMessageAbort(body) {
  const r = new BodyReader(body);
  exactArray(r, 3);
  const result = { invocationId: r.readUInt16(), kind: r.readUInt8(), reason: r.readUInt8() };
  r.end();
  return result;
}

/**
 * DuplexPipeUpdateState body `[pipeId: uint16, state: uint8, window: uint32]` (§4.6).
 * @param {number} pipeId @param {number} state @param {number} window
 * @returns {Uint8Array}
 */
export function encodeDuplexPipeUpdateState(pipeId, state, window) {
  return new BodyWriter(12).writeArrayHeader(3).writeInt(pipeId).writeInt(state).writeInt(window).toUint8Array();
}

/** @param {Uint8Array} body @returns {{ pipeId: number, state: number, window: number }} */
export function decodeDuplexPipeUpdateState(body) {
  const r = new BodyReader(body);
  exactArray(r, 3);
  const result = { pipeId: r.readUInt16(), state: r.readUInt8(), window: r.readUInt32() };
  r.end();
  return result;
}

/** Message types whose bodies this module encodes, for callers that switch on them. */
export const SessionBodyTypes = Object.freeze([
  MessageType.ClientGreeting,
  MessageType.ClientGreetingReconnection,
  MessageType.ServerGreeting,
  MessageType.InvocationCancellation,
  MessageType.MessageAbort,
  MessageType.InvocationCredit,
  MessageType.DuplexPipeUpdateState,
]);
