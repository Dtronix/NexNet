/**
 * A minimal RFC 6455 server side for the launcher's loopback bridge: binary messages only, no extensions. The bridge
 * carries a byte stream, so message boundaries do not matter.
 * @module
 */

import { createHash } from 'node:crypto';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

/**
 * @typedef {object} BridgeSocket
 * @property {(bytes: Uint8Array) => void} send
 * @property {() => void} close
 */

/**
 * Completes a WebSocket upgrade and reads its messages.
 * @param {import('node:http').IncomingMessage} request
 * @param {import('node:stream').Duplex} socket
 * @param {Buffer} head Bytes after the upgrade request.
 * @param {(payload: Uint8Array) => void} onMessage
 * @param {() => void} onClose
 * @returns {BridgeSocket | null} Null when the request is not a WebSocket upgrade.
 */
export function acceptWebSocket(request, socket, head, onMessage, onClose) {
  const key = request.headers['sec-websocket-key'];
  if (typeof key !== 'string') {
    socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
    return null;
  }
  const accept = createHash('sha1').update(key + GUID).digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\n\r\n');

  /** @type {Buffer} */
  let buffer = Buffer.alloc(0);
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    try {
      socket.end(Buffer.from([0x88, 0x00]));
    } catch {
      // Gone already.
    }
    onClose();
  };

  /**
   * @param {number} opcode
   * @param {Uint8Array} payload
   */
  const write = (opcode, payload) => {
    if (closed) return;
    /** @type {Buffer} */
    let header;
    if (payload.length < 126) {
      header = Buffer.from([0x80 | opcode, payload.length]);
    } else if (payload.length < 0x10000) {
      header = Buffer.alloc(4);
      header[0] = 0x80 | opcode;
      header[1] = 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x80 | opcode;
      header[1] = 127;
      header.writeBigUInt64BE(BigInt(payload.length), 2);
    }
    socket.write(Buffer.concat([header, payload]));
  };

  /** @param {Buffer} chunk */
  const onData = (chunk) => {
    buffer = buffer.length === 0 ? chunk : Buffer.concat([buffer, chunk]);
    for (;;) {
      if (buffer.length < 2) return;
      const opcode = buffer[0] & 0x0f;
      const masked = (buffer[1] & 0x80) !== 0;
      let length = buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (buffer.length < 4) return;
        length = buffer.readUInt16BE(2);
        offset = 4;
      } else if (length === 127) {
        if (buffer.length < 10) return;
        length = Number(buffer.readBigUInt64BE(2));
        offset = 10;
      }
      const maskOffset = offset;
      if (masked) offset += 4;
      if (buffer.length < offset + length) return;
      const payload = Buffer.from(buffer.subarray(offset, offset + length));
      if (masked) {
        for (let i = 0; i < payload.length; i++) payload[i] ^= buffer[maskOffset + (i & 3)];
      }
      buffer = buffer.subarray(offset + length);
      switch (opcode) {
        case 0x0:
        case 0x1:
        case 0x2:
          onMessage(payload);
          break;
        case 0x8:
          close();
          return;
        case 0x9:
          write(0xa, payload);
          break;
        default:
          break;
      }
    }
  };

  socket.on('data', onData);
  socket.on('close', close);
  socket.on('error', close);
  if (head.length > 0) onData(head);
  return { send: (bytes) => write(0x2, bytes), close };
}
