/**
 * Control protocol framing (docs/internals/interop-control-protocol.md §3): a 4-byte big-endian length, a kind byte
 * (0x01: one UTF-8 JSON object) and the payload. Platform-neutral.
 * @module
 */

/** Largest frame length (kind + payload): 64 MiB. */
export const MAX_FRAME_LENGTH = 64 * 1024 * 1024;

/** The kind of a JSON payload. */
export const KIND_JSON = 0x01;

/** A violation of the control protocol: the connection fails (bye, close). */
export class ControlProtocolError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'ControlProtocolError';
  }
}

const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });

/**
 * Encodes one message as a frame.
 * @param {object} message A JSON object (no bigint values).
 * @returns {Uint8Array}
 */
export function encodeFrame(message) {
  const payload = encoder.encode(JSON.stringify(message));
  const length = payload.length + 1;
  if (length > MAX_FRAME_LENGTH) throw new ControlProtocolError('A control frame of ' + length + ' bytes exceeds 64 MiB.');
  const frame = new Uint8Array(4 + length);
  new DataView(frame.buffer).setUint32(0, length, false);
  frame[4] = KIND_JSON;
  frame.set(payload, 5);
  return frame;
}

/** Splits a byte stream into messages. Frames may span chunks and a chunk may hold several frames. */
export class FrameDecoder {
  /** @type {Uint8Array} */
  #buffer = new Uint8Array(0);

  /** True when bytes of an unfinished frame are buffered (the end of the stream now is an error). @returns {boolean} */
  get midFrame() {
    return this.#buffer.length > 0;
  }

  /**
   * Adds bytes; returns the messages they complete, in order.
   * @param {Uint8Array} chunk
   * @returns {Record<string, any>[]}
   * @throws {ControlProtocolError} A malformed frame.
   */
  push(chunk) {
    if (chunk.length === 0) return [];
    if (this.#buffer.length === 0) {
      this.#buffer = chunk;
    } else {
      const joined = new Uint8Array(this.#buffer.length + chunk.length);
      joined.set(this.#buffer, 0);
      joined.set(chunk, this.#buffer.length);
      this.#buffer = joined;
    }

    /** @type {Record<string, any>[]} */
    const messages = [];
    let offset = 0;
    const view = new DataView(this.#buffer.buffer, this.#buffer.byteOffset, this.#buffer.byteLength);
    while (this.#buffer.length - offset >= 4) {
      const length = view.getUint32(offset, false);
      if (length < 1 || length > MAX_FRAME_LENGTH) throw new ControlProtocolError('Invalid control frame length ' + length + '.');
      if (this.#buffer.length - offset - 4 < length) break;
      const kind = this.#buffer[offset + 4];
      if (kind !== KIND_JSON) throw new ControlProtocolError('Unsupported control frame kind 0x' + kind.toString(16) + '.');
      const payload = this.#buffer.subarray(offset + 5, offset + 4 + length);
      offset += 4 + length;
      /** @type {unknown} */
      let message;
      try {
        message = JSON.parse(decoder.decode(payload));
      } catch (error) {
        throw new ControlProtocolError('A control frame is not UTF-8 JSON: ' + String(error));
      }
      if (typeof message !== 'object' || message === null || Array.isArray(message)) {
        throw new ControlProtocolError('A control frame payload is not a JSON object.');
      }
      messages.push(/** @type {Record<string, any>} */ (message));
    }
    // Keep the rest (copied, so a caller may reuse its chunk).
    this.#buffer = offset === this.#buffer.length ? new Uint8Array(0) : this.#buffer.slice(offset);
    return messages;
  }
}
