/**
 * Frame encoding (protocol spec §2). Header fields after the type byte are little-endian.
 * @module
 */

import { MAX_FRAME_BODY, PREAMBLE } from './protocol.js';

/** @returns {Uint8Array} A fresh copy of the 8-byte preamble. */
export function encodePreamble() {
  return Uint8Array.from(PREAMBLE);
}

/**
 * A frame with no length and no body: `Ping` or a disconnect code.
 * @param {number} type
 * @returns {Uint8Array}
 */
export function encodeHeaderOnly(type) {
  return Uint8Array.of(type);
}

/** @param {number} length */
function checkBody(length) {
  if (length > MAX_FRAME_BODY) throw new RangeError(`frame body of ${length} bytes exceeds ${MAX_FRAME_BODY}`);
}

/**
 * A frame with a body and no post-header: `[type][length u16 LE][body]`.
 * @param {number} type
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export function encodeFrame(type, body) {
  checkBody(body.length);
  const frame = new Uint8Array(3 + body.length);
  frame[0] = type;
  frame[1] = body.length & 0xff;
  frame[2] = body.length >>> 8;
  frame.set(body, 3);
  return frame;
}

/**
 * A pipe frame (`DuplexPipeWrite` or `PipeWindowUpdate`): `[type][length u16 LE][clientId][serverId][body]`.
 * `pipeId` is the MessagePack form, `clientId | serverId << 8` (§2 Pipe IDs).
 * @param {number} type
 * @param {number} pipeId
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export function encodePipeFrame(type, pipeId, body) {
  checkBody(body.length);
  const frame = new Uint8Array(5 + body.length);
  frame[0] = type;
  frame[1] = body.length & 0xff;
  frame[2] = body.length >>> 8;
  frame[3] = pipeId & 0xff;
  frame[4] = (pipeId >>> 8) & 0xff;
  frame.set(body, 5);
  return frame;
}

/**
 * A `PipeWindowUpdate` frame granting `credit` bytes (§7.2): the body is a `uint32` little-endian.
 * @param {number} pipeId
 * @param {number} credit
 * @returns {Uint8Array}
 */
export function encodePipeWindowUpdate(pipeId, credit) {
  const body = new Uint8Array(4);
  new DataView(body.buffer).setUint32(0, credit, true);
  return encodePipeFrame(51, pipeId, body);
}

/**
 * A `MessageChunk` frame: `[114][length u16 LE][invocationId u16 LE][kind u8][body]` (§4.10).
 * @param {number} invocationId
 * @param {number} kind
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export function encodeChunkFrame(invocationId, kind, body) {
  checkBody(body.length);
  const frame = new Uint8Array(6 + body.length);
  frame[0] = 114;
  frame[1] = body.length & 0xff;
  frame[2] = body.length >>> 8;
  frame[3] = invocationId & 0xff;
  frame[4] = (invocationId >>> 8) & 0xff;
  frame[5] = kind;
  frame.set(body, 6);
  return frame;
}
