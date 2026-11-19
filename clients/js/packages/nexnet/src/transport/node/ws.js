/// <reference types="node" />
/**
 * RFC 6455 WebSocket client for Node (protocol spec §13.7): the opening handshake over `http`/`https`, masked binary
 * frames out, fragmented binary messages in (their payload streamed to the session as it arrives), Ping/Pong and the
 * close handshake. No subprotocol and no extensions. Node only.
 * @module
 */

import { createHash, randomBytes, randomFillSync } from 'node:crypto';
import { TransportBase, toUrl, transportError } from '../TransportBase.js';
import { headerHasToken, httpUpgrade } from './upgrade.js';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

/** WebSocket opcodes. */
export const Opcode = Object.freeze({
  Continuation: 0x0,
  Text: 0x1,
  Binary: 0x2,
  Close: 0x8,
  Ping: 0x9,
  Pong: 0xa,
});

/** Close codes this client sends. */
export const CloseCode = Object.freeze({
  Normal: 1000,
  ProtocolError: 1002,
  UnsupportedData: 1003,
  MessageTooBig: 1009,
});

/** How long `close` waits for the server's Close (C# waits 250 ms too). */
const CLOSE_WAIT_MS = 250;
/** Largest frame payload accepted (2^53 - 1 is the JS limit; NexNet's own limits are far below). */
const MAX_FRAME_PAYLOAD = Number.MAX_SAFE_INTEGER;

/**
 * `Sec-WebSocket-Accept` for a key (RFC 6455 §4.2.2).
 * @param {string} key
 * @returns {string}
 */
export function acceptKey(key) {
  return createHash('sha1').update(key + GUID).digest('base64');
}

/**
 * Encodes one client frame: FIN set, masked with a fresh key (RFC 6455 §5.3).
 *
 * @param {number} opcode
 * @param {Uint8Array[]} chunks Payload, concatenated.
 * @returns {Buffer}
 */
export function encodeClientFrame(opcode, chunks) {
  let length = 0;
  for (const chunk of chunks) length += chunk.length;
  const headerLength = 2 + (length < 126 ? 0 : length <= 0xffff ? 2 : 8) + 4;
  const frame = Buffer.allocUnsafe(headerLength + length);
  frame[0] = 0x80 | opcode;
  let offset = 2;
  if (length < 126) {
    frame[1] = 0x80 | length;
  } else if (length <= 0xffff) {
    frame[1] = 0x80 | 126;
    frame.writeUInt16BE(length, 2);
    offset = 4;
  } else {
    frame[1] = 0x80 | 127;
    frame.writeUInt32BE(Math.floor(length / 0x1_0000_0000), 2);
    frame.writeUInt32BE(length >>> 0, 6);
    offset = 10;
  }
  const mask = frame.subarray(offset, offset + 4);
  randomFillSync(mask);
  offset += 4;
  let at = offset;
  for (const chunk of chunks) {
    frame.set(chunk, at);
    at += chunk.length;
  }
  for (let i = 0; i < length; i++) frame[offset + i] ^= mask[i & 3];
  return frame;
}

/**
 * Error raised by {@link FrameDecoder} on a frame that breaks RFC 6455 or §13.7. `closeCode` is the code to fail the
 * connection with.
 */
export class WebSocketProtocolError extends Error {
  /**
   * @param {string} message
   * @param {number} [closeCode]
   */
  constructor(message, closeCode = CloseCode.ProtocolError) {
    super(message);
    this.name = 'WebSocketProtocolError';
    this.closeCode = closeCode;
  }
}

/**
 * Incremental decoder of server frames. Binary payload is passed to `onBinary` as soon as it arrives, so a message
 * (or a frame) split across reads, and a message split into continuation frames, become one byte stream.
 * Control frames may arrive between the fragments of a message.
 */
export class FrameDecoder {
  /** @type {Uint8Array} Bytes of an incomplete frame header. */
  #header = new Uint8Array(14);
  #headerLength = 0;
  /** Payload bytes of the current frame still to come; -1 while reading a header. */
  #remaining = -1;
  #opcode = 0;
  /** True between a non-final data frame and its final continuation. */
  #inMessage = false;
  /** @type {Uint8Array[]} Payload of the current control frame. */
  #control = [];

  /**
   * @param {object} handlers
   * @param {(data: Uint8Array) => void} handlers.onBinary
   * @param {(opcode: number, payload: Uint8Array) => void} handlers.onControl Close, Ping or Pong, complete.
   */
  constructor({ onBinary, onControl }) {
    this.onBinary = onBinary;
    this.onControl = onControl;
  }

  /**
   * Feeds bytes. Throws {@link WebSocketProtocolError} on a malformed frame; the decoder must not be used after that.
   * @param {Uint8Array} data
   */
  push(data) {
    let offset = 0;
    while (offset < data.length) {
      if (this.#remaining < 0) {
        offset = this.#readHeader(data, offset);
        if (this.#remaining < 0) return;
        if (this.#remaining === 0) this.#endFrame();
        continue;
      }
      const take = Math.min(this.#remaining, data.length - offset);
      const part = data.subarray(offset, offset + take);
      offset += take;
      this.#remaining -= take;
      if (this.#opcode >= 0x8) this.#control.push(part.slice());
      else this.onBinary(part);
      if (this.#remaining === 0) this.#endFrame();
    }
  }

  /**
   * @param {Uint8Array} data
   * @param {number} offset
   * @returns {number} The new offset.
   */
  #readHeader(data, offset) {
    const header = this.#header;
    while (offset < data.length) {
      header[this.#headerLength++] = data[offset++];
      const needed = this.#headerSize();
      if (needed !== 0 && this.#headerLength === needed) {
        this.#startFrame();
        return offset;
      }
    }
    return offset;
  }

  /** @returns {number} The full header size, or 0 while it is not known yet. */
  #headerSize() {
    if (this.#headerLength < 2) return 0;
    const len = this.#header[1] & 0x7f;
    const masked = (this.#header[1] & 0x80) !== 0;
    return 2 + (len === 126 ? 2 : len === 127 ? 8 : 0) + (masked ? 4 : 0);
  }

  #startFrame() {
    const h = this.#header;
    this.#headerLength = 0;
    const fin = (h[0] & 0x80) !== 0;
    const opcode = h[0] & 0x0f;
    if ((h[0] & 0x70) !== 0) throw new WebSocketProtocolError('reserved bits set (no extension was negotiated)');
    if ((h[1] & 0x80) !== 0) throw new WebSocketProtocolError('masked server frame');
    let length = h[1] & 0x7f;
    if (length === 126) length = (h[2] << 8) | h[3];
    else if (length === 127) {
      const high = ((h[2] << 24) >>> 0) + (h[3] << 16) + (h[4] << 8) + h[5];
      const low = ((h[6] << 24) >>> 0) + (h[7] << 16) + (h[8] << 8) + h[9];
      length = high * 0x1_0000_0000 + low;
      if (h[2] & 0x80 || length > MAX_FRAME_PAYLOAD) throw new WebSocketProtocolError('frame too large', CloseCode.MessageTooBig);
    }

    if (opcode >= 0x8) {
      if (opcode !== Opcode.Close && opcode !== Opcode.Ping && opcode !== Opcode.Pong) throw new WebSocketProtocolError(`unknown control opcode ${opcode}`);
      if (!fin) throw new WebSocketProtocolError('fragmented control frame');
      if (length > 125) throw new WebSocketProtocolError('control frame payload over 125 bytes');
    } else if (opcode === Opcode.Continuation) {
      if (!this.#inMessage) throw new WebSocketProtocolError('continuation frame without a message');
    } else if (opcode === Opcode.Binary) {
      if (this.#inMessage) throw new WebSocketProtocolError('new message before the previous one ended');
    } else if (opcode === Opcode.Text) {
      throw new WebSocketProtocolError('text message; NexNet uses binary messages only', CloseCode.UnsupportedData);
    } else {
      throw new WebSocketProtocolError(`unknown data opcode ${opcode}`);
    }

    this.#opcode = opcode;
    this.#remaining = length;
    if (opcode < 0x8) this.#inMessage = !fin;
  }

  #endFrame() {
    this.#remaining = -1;
    if (this.#opcode >= 0x8) {
      const parts = this.#control;
      this.#control = [];
      this.onControl(this.#opcode, parts.length === 1 ? parts[0] : Buffer.concat(parts));
    }
  }
}

/**
 * A NexNet stream over an RFC 6455 connection. Created by {@link nodeWebSocketTransport}.
 */
export class NodeWebSocketTransport extends TransportBase {
  /** @type {import('node:net').Socket} */
  #socket;
  #decoder;
  #closeSent = false;
  /** @type {Promise<void>} */
  #socketClosed;
  /** Close code received from the server, or 0. */
  closeCode = 0;

  /**
   * @param {import('node:net').Socket} socket The upgraded connection.
   * @param {{ name: string, isSecure: boolean, head?: Uint8Array }} options
   */
  constructor(socket, { name, isSecure, head }) {
    super(name, isSecure);
    this.#socket = socket;
    if (typeof (/** @type {any} */ (socket).getPeerX509Certificate) === 'function') {
      this.remoteCertificate = /** @type {import('node:tls').TLSSocket} */ (socket).getPeerX509Certificate() ?? null;
    }
    this.#decoder = new FrameDecoder({
      onBinary: (data) => this._deliver(data),
      onControl: (opcode, payload) => this.#onControl(opcode, payload),
    });
    this.#socketClosed = new Promise((resolve) => socket.once('close', () => resolve(undefined)));
    socket.on('error', (error) => this._ended(error));
    socket.on('end', () => this._ended(this.#closeError()));
    socket.on('close', () => this._ended(this.#closeError()));
    if (head !== undefined && head.length > 0) this.#feed(head);
    socket.on('data', (/** @type {Buffer} */ data) => this.#feed(data));
    socket.resume();
  }

  /**
   * Sends the chunks as one binary message.
   * @param {Uint8Array[]} chunks
   * @returns {Promise<void>}
   */
  write(chunks) {
    const socket = this.#socket;
    if (this.ended || this.#closeSent || socket.destroyed || !socket.writable) {
      return Promise.reject(transportError(`${this.name} connection is closed.`, { kind: 'ConnectionAborted' }));
    }
    let length = 0;
    for (const chunk of chunks) length += chunk.length;
    if (length === 0) return Promise.resolve();
    if (socket.write(encodeClientFrame(Opcode.Binary, chunks))) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const onDrain = () => {
        socket.off('close', onClose);
        resolve();
      };
      const onClose = () => {
        socket.off('drain', onDrain);
        reject(transportError(`${this.name} connection closed while writing.`, { kind: 'ConnectionAborted' }));
      };
      socket.once('drain', onDrain);
      socket.once('close', onClose);
    });
  }

  /**
   * Starts the close handshake with code 1000 and waits up to 250 ms for the server to finish it, then closes the
   * connection.
   * @param {unknown} [reason]
   * @returns {Promise<void>}
   */
  async close(reason) {
    const socket = this.#socket;
    if (!socket.destroyed) {
      this.#sendClose(CloseCode.Normal);
      /** @type {ReturnType<typeof setTimeout> | undefined} */
      let timer;
      await Promise.race([this.#socketClosed, new Promise((resolve) => { timer = setTimeout(resolve, CLOSE_WAIT_MS); })]);
      clearTimeout(timer);
      socket.destroy();
    }
    this._ended();
  }

  /** Sends a Ping control frame (keepalive at the WebSocket layer; not a NexNet ping). @param {Uint8Array} [payload] */
  ping(payload = new Uint8Array(0)) {
    if (!this.#closeSent && !this.#socket.destroyed) this.#socket.write(encodeClientFrame(Opcode.Ping, [payload]));
  }

  /** @protected */
  _release() {
    if (!this.#socket.destroyed) this.#socket.destroy();
  }

  /** @param {Uint8Array} data */
  #feed(data) {
    if (this.ended) return;
    try {
      this.#decoder.push(data);
    } catch (error) {
      if (error instanceof WebSocketProtocolError) {
        this.#sendClose(error.closeCode);
        this.#socket.end();
        this._ended(transportError(`WebSocket protocol error: ${error.message}.`, { kind: 'ProtocolError', cause: error }));
        return;
      }
      throw error;
    }
  }

  /**
   * @param {number} opcode
   * @param {Uint8Array} payload
   */
  #onControl(opcode, payload) {
    if (opcode === Opcode.Ping) {
      if (!this.#closeSent && !this.#socket.destroyed) this.#socket.write(encodeClientFrame(Opcode.Pong, [payload]));
      return;
    }
    if (opcode === Opcode.Pong) return;
    // Close: answer with the same code (§13.7), then the server closes the TCP connection.
    if (payload.length === 1) throw new WebSocketProtocolError('close payload of one byte');
    this.closeCode = payload.length >= 2 ? (payload[0] << 8) | payload[1] : 1005;
    if (!this.#closeSent) this.#sendClose(payload.length >= 2 ? this.closeCode : null);
    this.#socket.end();
    this._ended(this.#closeError());
  }

  /** @param {number | null} code Null: an empty Close. */
  #sendClose(code) {
    if (this.#closeSent || this.#socket.destroyed || !this.#socket.writable) return;
    this.#closeSent = true;
    const payload = code === null ? new Uint8Array(0) : Uint8Array.of(code >> 8, code & 0xff);
    this.#socket.write(encodeClientFrame(Opcode.Close, [payload]));
  }

  /** @returns {unknown} undefined after a Normal Closure, else an error naming the close code. */
  #closeError() {
    const code = this.closeCode;
    if (code === CloseCode.Normal) return undefined;
    if (code === 0) return transportError('WebSocket connection ended without a Close frame (1006).', { kind: 'ConnectionAborted' });
    return transportError(`WebSocket closed by the server with code ${code}.`, { kind: 'ConnectionAborted' });
  }
}

/**
 * Opens a WebSocket transport (`ClientConfig.webSocket`) with the RFC 6455 client: `ws:` over `http`, `wss:` over
 * `https` with the TLS options. Sends `config.headers` (an `Authorization` header, for example) with the upgrade
 * request; a query-string token goes in the URL. A refused upgrade rejects with `status` set: `401`/`403` are
 * `AuthenticationError`, `5xx` `InternalError`, others `ProtocolError`.
 *
 * @type {import('../ITransport.js').TransportFactory}
 */
export async function nodeWebSocketTransport(config, { signal }) {
  const options = /** @type {import('../../contract/config.js').WebSocketTransportConfig} */ (config.webSocket);
  const url = toUrl(options.url);
  const secure = url.protocol === 'wss:';
  const httpUrl = new URL(url.href);
  httpUrl.protocol = secure ? 'https:' : 'http:';
  const key = randomBytes(16).toString('base64');
  const { response, socket, head } = await httpUpgrade({
    transport: 'WebSocket',
    url: httpUrl,
    secure,
    headers: {
      ...options.headers,
      Connection: 'Upgrade',
      Upgrade: 'websocket',
      'Sec-WebSocket-Version': '13',
      'Sec-WebSocket-Key': key,
    },
    tls: options.tls,
    signal,
  });
  const h = response.headers;
  /** @type {string | null} */
  let problem = null;
  if (!headerHasToken(h.upgrade, 'websocket')) problem = `Upgrade is '${h.upgrade ?? ''}'`;
  else if (!headerHasToken(h.connection, 'upgrade')) problem = `Connection is '${h.connection ?? ''}'`;
  else if (h['sec-websocket-accept'] !== acceptKey(key)) problem = 'Sec-WebSocket-Accept does not match the key';
  else if (h['sec-websocket-protocol'] !== undefined) problem = 'the server selected a subprotocol, none was requested';
  else if (h['sec-websocket-extensions'] !== undefined) problem = 'the server selected an extension, none was offered';
  if (problem !== null) {
    socket.destroy();
    throw transportError(`WebSocket handshake with ${url.href} failed: ${problem}.`, { kind: 'ProtocolError' });
  }
  return new NodeWebSocketTransport(socket, { name: `${url.protocol}//`, isSecure: secure, head });
}
