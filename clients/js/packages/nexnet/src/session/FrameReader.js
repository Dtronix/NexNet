/**
 * Incremental frame parser (protocol spec §1, §2). Bytes are appended to a growable buffer and every complete frame
 * is handed to the sink synchronously, in the data callback that completed it.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';
import { MAX_PRE_AUTH_BODY, PREAMBLE, PROTOCOL_VERSION, frameLayout, isDisconnectCode } from './protocol.js';

/**
 * One received frame. `body` is a view into the reader's buffer: it is valid only until the sink returns, so copy
 * what must outlive the call.
 *
 * @typedef {object} Frame
 * @property {number} type Message type byte.
 * @property {Uint8Array} body Empty for `Ping`.
 * @property {number} pipeId `DuplexPipeWrite`/`PipeWindowUpdate`: `clientId | serverId << 8`; otherwise 0.
 * @property {number} invocationId `MessageChunk`: the invocation ID; otherwise 0.
 * @property {number} kind `MessageChunk`: the large-message kind; otherwise 0.
 */

/**
 * Result of {@link FrameReader#push}.
 *  - `{ kind: 'more' }`: every complete frame was consumed; waiting for more bytes.
 *  - `{ kind: 'stopped' }`: the sink returned false; the reader must not be used again.
 *  - `{ kind: 'disconnect', reason }`: the peer sent a disconnect byte (20–38). Bytes after it are ignored.
 *  - `{ kind: 'error', reason }`: a protocol violation (bad preamble, unknown type, oversized pre-auth body).
 *
 * @typedef {{ kind: 'more' } | { kind: 'stopped' } | { kind: 'disconnect', reason: number } | { kind: 'error', reason: number, message: string }} PushResult
 */

const MORE = Object.freeze({ kind: /** @type {'more'} */ ('more') });
const STOPPED = Object.freeze({ kind: /** @type {'stopped'} */ ('stopped') });

export class FrameReader {
  /** @type {Uint8Array} */
  #buf = new Uint8Array(0);
  #start = 0;
  #end = 0;
  #preambleConfirmed = false;

  /** True once the connection completed: lifts the 16 KiB pre-auth body cap (§2). */
  handshakeComplete = false;

  /** Bytes buffered and not yet consumed (an incomplete frame). @returns {number} */
  get buffered() {
    return this.#end - this.#start;
  }

  /**
   * Appends `chunk` and parses every complete frame.
   * @param {Uint8Array} chunk
   * @param {(frame: Frame) => boolean} sink Return false to stop parsing (the session is ending).
   * @returns {PushResult}
   */
  push(chunk, sink) {
    this.#append(chunk);
    const result = this.#parse(sink);
    // Compact: drop consumed bytes so the buffer never grows past one partial frame plus a chunk.
    if (this.#start === this.#end) {
      this.#start = this.#end = 0;
      if (this.#buf.length > 1 << 17) this.#buf = new Uint8Array(0);
    }
    return result;
  }

  /** @param {Uint8Array} chunk */
  #append(chunk) {
    const live = this.#end - this.#start;
    if (this.#end + chunk.length > this.#buf.length) {
      if (live + chunk.length <= this.#buf.length && this.#start > 0) {
        this.#buf.copyWithin(0, this.#start, this.#end);
      } else {
        let size = Math.max(1024, this.#buf.length);
        while (size < live + chunk.length) size *= 2;
        const next = new Uint8Array(size);
        next.set(this.#buf.subarray(this.#start, this.#end));
        this.#buf = next;
      }
      this.#start = 0;
      this.#end = live;
    }
    this.#buf.set(chunk, this.#end);
    this.#end += chunk.length;
  }

  /**
   * @param {(frame: Frame) => boolean} sink
   * @returns {PushResult}
   */
  #parse(sink) {
    const buf = this.#buf;
    if (!this.#preambleConfirmed) {
      if (this.#end - this.#start < PREAMBLE.length) return MORE;
      for (let i = 0; i < 4; i++) {
        if (buf[this.#start + i] !== PREAMBLE[i]) return protocolError('not a NexNet stream (preamble magic)');
      }
      if (buf[this.#start + 4] !== 0 || buf[this.#start + 5] !== 0 || buf[this.#start + 6] !== 0) {
        return protocolError('preamble reserved bytes are not zero');
      }
      if (buf[this.#start + 7] !== PROTOCOL_VERSION) return protocolError(`unsupported protocol version ${buf[this.#start + 7]}`);
      this.#start += PREAMBLE.length;
      this.#preambleConfirmed = true;
    }

    while (this.#start < this.#end) {
      const p = this.#start;
      const type = buf[p];
      if (isDisconnectCode(type)) {
        this.#start = this.#end;
        return { kind: 'disconnect', reason: type };
      }
      const layout = frameLayout(type);
      if (layout === null) return protocolError(`unknown message type ${type}`);

      if (!layout.hasBody) {
        this.#start = p + 1;
        if (!sink({ type, body: EMPTY, pipeId: 0, invocationId: 0, kind: 0 })) return STOPPED;
        continue;
      }

      const available = this.#end - p;
      if (available < 3) return MORE;
      const length = buf[p + 1] | (buf[p + 2] << 8);
      if (!this.handshakeComplete && length > MAX_PRE_AUTH_BODY) {
        return protocolError(`${length}-byte body before the connection completed`);
      }
      const headerLength = 3 + layout.postHeader;
      if (available < headerLength + length) return MORE;

      let pipeId = 0;
      let invocationId = 0;
      let kind = 0;
      if (layout.postHeader === 2) {
        pipeId = buf[p + 3] | (buf[p + 4] << 8);
      } else if (layout.postHeader === 3) {
        invocationId = buf[p + 3] | (buf[p + 4] << 8);
        kind = buf[p + 5];
      }
      const bodyStart = p + headerLength;
      this.#start = bodyStart + length;
      const body = buf.subarray(bodyStart, bodyStart + length);
      if (!sink({ type, body, pipeId, invocationId, kind })) return STOPPED;
    }
    return MORE;
  }
}

const EMPTY = new Uint8Array(0);

/**
 * @param {string} message
 * @returns {PushResult}
 */
function protocolError(message) {
  return { kind: 'error', reason: DisconnectReason.ProtocolError, message };
}
