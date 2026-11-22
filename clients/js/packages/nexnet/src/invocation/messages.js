/**
 * Bodies of the invocation messages (protocol spec §4.3, §4.4, §4.8) and the remote error detail. Session-level
 * bodies (credit, cancellation, abort) are in `../session/wire.js`.
 * @module
 */

import { BodyReader, BodyWriter, WireFormatError } from '../session/wire.js';

/** Invocation flags (§4.3). */
export const InvocationFlags = Object.freeze({ None: 0, IgnoreReturn: 1, DuplexPipe: 2 });

/** InvocationResult states (§4.4). */
export const ResultState = Object.freeze({ Unset: 0, CompletedResult: 1, Exception: 2, Unauthorized: 3, Canceled: 4 });

/** Large message kinds (§4.8). */
export const LargeMessageKind = Object.freeze({ Argument: 0, Result: 1 });

/** MessageAbort reasons (§4.9). */
export const AbortReason = Object.freeze({ Busy: 0, TooLarge: 1, Cancelled: 2, Ack: 3 });

/** Longest remote exception type name sent or accepted, in UTF-16 code units (§4.4). */
export const MAX_REMOTE_TYPE_LENGTH = 256;

/** Longest remote message sent or accepted, in UTF-16 code units (§4.4). */
export const MAX_REMOTE_MESSAGE_LENGTH = 2048;

const EMPTY_ARRAY = Uint8Array.of(0x90);

/**
 * @param {Uint8Array} a @param {Uint8Array} b
 * @returns {Uint8Array}
 */
function concat(a, b) {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

/**
 * Returns the offset just past the MessagePack value at `pos`. Iterative, so nesting depth costs no stack; every
 * length is bounds-checked. Throws {@link WireFormatError}.
 * @param {Uint8Array} buf
 * @param {number} pos
 * @returns {number}
 */
export function skipValue(buf, pos) {
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  let pending = 1;
  /** @param {number} n */
  const need = (n) => {
    if (pos + n > buf.length) throw new WireFormatError('unexpected end of body');
  };
  /** @param {number} n */
  const bytes = (n) => {
    need(n);
    pos += n;
  };
  /** @param {number} n */
  const values = (n) => {
    // Each value needs at least one byte; reject impossible counts at once.
    if (n > buf.length - pos) throw new WireFormatError('unexpected end of body');
    pending += n;
  };
  while (pending > 0) {
    pending--;
    need(1);
    const b = buf[pos++];
    if (b <= 0x7f || b >= 0xe0 || b === 0xc0 || b === 0xc2 || b === 0xc3) continue;
    if ((b & 0xe0) === 0xa0) { bytes(b & 0x1f); continue; }
    if ((b & 0xf0) === 0x90) { values(b & 0x0f); continue; }
    if ((b & 0xf0) === 0x80) { values((b & 0x0f) * 2); continue; }
    switch (b) {
      case 0xcc: case 0xd0: bytes(1); break;
      case 0xcd: case 0xd1: bytes(2); break;
      case 0xce: case 0xd2: case 0xca: bytes(4); break;
      case 0xcf: case 0xd3: case 0xcb: bytes(8); break;
      case 0xc4: case 0xd9: need(1); bytes(1 + buf[pos]); break;
      case 0xc5: case 0xda: need(2); bytes(2 + view.getUint16(pos)); break;
      case 0xc6: case 0xdb: need(4); bytes(4 + view.getUint32(pos)); break;
      case 0xdc: need(2); { const n = view.getUint16(pos); pos += 2; values(n); } break;
      case 0xdd: need(4); { const n = view.getUint32(pos); pos += 4; values(n); } break;
      case 0xde: need(2); { const n = view.getUint16(pos) * 2; pos += 2; values(n); } break;
      case 0xdf: need(4); { const n = view.getUint32(pos) * 2; pos += 4; values(n); } break;
      case 0xd4: bytes(2); break;
      case 0xd5: bytes(3); break;
      case 0xd6: bytes(5); break;
      case 0xd7: bytes(9); break;
      case 0xd8: bytes(17); break;
      case 0xc7: need(1); bytes(2 + buf[pos]); break;
      case 0xc8: need(2); bytes(3 + view.getUint16(pos)); break;
      case 0xc9: need(4); bytes(5 + view.getUint32(pos)); break;
      default: throw new WireFormatError(`invalid format byte 0x${b.toString(16)}`);
    }
  }
  return pos;
}

/**
 * Reads the embedded value that ends the body (from the reader's position) as a copy, and checks nothing follows.
 * @param {Uint8Array} body
 * @param {BodyReader} reader
 * @returns {Uint8Array}
 */
function readTrailingValue(body, reader) {
  const start = body.length - reader.remaining;
  const end = skipValue(body, start);
  if (end !== body.length) throw new WireFormatError(`${body.length - end} trailing bytes`);
  return body.slice(start, end);
}

/**
 * @typedef {object} InvocationMessage
 * @property {number} invocationId
 * @property {number} methodId
 * @property {number} flags
 * @property {Uint8Array} args The arguments array, as MessagePack (`0x90` when there are none).
 */

/**
 * Invocation body `[invocationId, methodId, flags, arguments]` (§4.3). `args` is the serialized arguments array; null
 * or empty sends an empty array.
 * @param {number} invocationId
 * @param {number} methodId
 * @param {number} flags
 * @param {Uint8Array | null} args
 * @returns {Uint8Array}
 */
export function encodeInvocation(invocationId, methodId, flags, args) {
  const head = new BodyWriter(16).writeArrayHeader(4).writeInt(invocationId).writeInt(methodId).writeInt(flags).toUint8Array();
  return concat(head, args && args.length > 0 ? args : EMPTY_ARRAY);
}

/**
 * Decodes an Invocation body; the arguments are copied.
 * @param {Uint8Array} body
 * @returns {InvocationMessage}
 */
export function decodeInvocation(body) {
  const r = new BodyReader(body);
  const n = r.readArrayHeader();
  if (n !== 4) throw new WireFormatError(`expected an array of 4, found ${n}`);
  const invocationId = r.readUInt16();
  const methodId = r.readUInt16();
  const flags = r.readUInt8();
  const args = readTrailingValue(body, r);
  return { invocationId, methodId, flags, args };
}

/**
 * @typedef {object} InvocationResultMessage
 * @property {number} invocationId
 * @property {number} state A {@link ResultState}.
 * @property {Uint8Array | null} value The embedded result (or error detail), or null when the body has none.
 */

/**
 * InvocationResult body `[invocationId, state]` or `[invocationId, state, value]` (§4.4).
 * @param {number} invocationId
 * @param {number} state
 * @param {Uint8Array | null} value An embedded MessagePack value, or null for none.
 * @returns {Uint8Array}
 */
export function encodeInvocationResult(invocationId, state, value) {
  const head = new BodyWriter(8).writeArrayHeader(value ? 3 : 2).writeInt(invocationId).writeInt(state).toUint8Array();
  return value ? concat(head, value) : head;
}

/**
 * Decodes an InvocationResult body; the value is copied.
 * @param {Uint8Array} body
 * @returns {InvocationResultMessage}
 */
export function decodeInvocationResult(body) {
  const r = new BodyReader(body);
  const n = r.readArrayHeader();
  if (n !== 2 && n !== 3) throw new WireFormatError(`expected an array of 2 or 3, found ${n}`);
  const invocationId = r.readUInt16();
  const state = r.readUInt8();
  if (n === 2) {
    r.end();
    return { invocationId, state, value: null };
  }
  return { invocationId, state, value: readTrailingValue(body, r) };
}

/**
 * @typedef {object} MessageStart
 * @property {number} invocationId
 * @property {number} kind A {@link LargeMessageKind}.
 * @property {number} innerType 110 (Invocation) or 112 (InvocationResult).
 * @property {number} totalLength
 */

/**
 * MessageStart body `[invocationId, kind, innerType, totalLength]` (§4.8).
 * @param {MessageStart} start
 * @returns {Uint8Array}
 */
export function encodeMessageStart({ invocationId, kind, innerType, totalLength }) {
  return new BodyWriter(16).writeArrayHeader(4).writeInt(invocationId).writeInt(kind).writeInt(innerType).writeInt(totalLength).toUint8Array();
}

/**
 * @param {Uint8Array} body
 * @returns {MessageStart}
 */
export function decodeMessageStart(body) {
  const r = new BodyReader(body);
  const n = r.readArrayHeader();
  if (n !== 4) throw new WireFormatError(`expected an array of 4, found ${n}`);
  const start = { invocationId: r.readUInt16(), kind: r.readUInt8(), innerType: r.readUInt8(), totalLength: r.readUInt32() };
  r.end();
  return start;
}

/**
 * Cuts `value` to at most `max` UTF-16 code units without splitting a surrogate pair.
 * @param {string | null} value
 * @param {number} max
 * @returns {string | null}
 */
export function truncate(value, max) {
  if (value === null || value.length <= max) return value;
  let length = max;
  const high = value.charCodeAt(length - 1);
  const low = value.charCodeAt(length);
  if (high >= 0xd800 && high <= 0xdbff && low >= 0xdc00 && low <= 0xdfff) length--;
  return value.slice(0, length);
}

/**
 * @typedef {object} RemoteErrorDetail
 * @property {number | null} code
 * @property {string | null} type
 * @property {string | null} message
 */

/**
 * The detail of an Exception result, `[code: int32|nil, type: str|nil, message: str|nil]` (§4.4), truncating the
 * type to 256 and the message to 2048 code units.
 * @param {RemoteErrorDetail} detail
 * @returns {Uint8Array}
 */
export function encodeRemoteErrorDetail({ code, type, message }) {
  const w = new BodyWriter(32).writeArrayHeader(3);
  if (code === null) w.writeNil();
  else w.writeInt(code);
  return w.writeString(truncate(type, MAX_REMOTE_TYPE_LENGTH)).writeString(truncate(message, MAX_REMOTE_MESSAGE_LENGTH)).toUint8Array();
}

/**
 * Reads a remote error detail. Returns null for anything that is not exactly a valid one (§4.4); never throws.
 * @param {Uint8Array | null} value
 * @returns {RemoteErrorDetail | null}
 */
export function tryReadRemoteErrorDetail(value) {
  if (value === null) return null;
  try {
    const r = new BodyReader(value);
    if (r.readArrayHeader() !== 3) return null;
    const code = r.tryReadNil() ? null : r.readInt32();
    const type = r.readString();
    const message = r.readString();
    r.end();
    if ((type !== null && type.length > MAX_REMOTE_TYPE_LENGTH) || (message !== null && message.length > MAX_REMOTE_MESSAGE_LENGTH)) return null;
    return { code, type, message };
  } catch {
    return null;
  }
}
