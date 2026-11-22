/**
 * Minimal local servers for the transport tests: raw TCP/TLS/UDS listeners and an HTTP(S) server that hands each
 * upgrade request to the test, which then writes raw bytes (WebSocket frames, preambles) itself.
 * @module
 */

import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import tls from 'node:tls';
import { fileURLToPath } from 'node:url';

const certDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../../../../src/NexNet.IntegrationTests/certs');

/** The F08 test certificates (TEST ONLY). */
export const certs = {
  ca: readFileSync(path.join(certDir, 'test-ca.pem'), 'utf8'),
  serverCert: readFileSync(path.join(certDir, 'server.pem'), 'utf8'),
  serverKey: readFileSync(path.join(certDir, 'server.key'), 'utf8'),
  clientCert: readFileSync(path.join(certDir, 'client.pem'), 'utf8'),
  clientKey: readFileSync(path.join(certDir, 'client.key'), 'utf8'),
  /** Server leaf SPKI sha256 pin (certs/README.md). */
  serverPin: 'MZEa+vKvxw+5EI8S4C6W811NN39NrGjkKDakkDBwHdE=',
};

/** @typedef {{ port: number, close: () => Promise<void> }} Listening */

/**
 * @param {net.Server} server
 * @param {Set<net.Socket>} sockets
 * @returns {() => Promise<void>}
 */
function closer(server, sockets) {
  return () => new Promise((resolve) => {
    for (const s of sockets) s.destroy();
    server.close(() => resolve(undefined));
  });
}

/**
 * @param {net.Server} server
 * @returns {Set<net.Socket>}
 */
function track(server) {
  /** @type {Set<net.Socket>} */
  const sockets = new Set();
  server.on('connection', (s) => {
    sockets.add(s);
    s.on('close', () => sockets.delete(s));
  });
  server.on('secureConnection', (s) => {
    sockets.add(s);
    s.on('close', () => sockets.delete(s));
  });
  return sockets;
}

/**
 * A TCP (or TLS) server; `onSocket` gets each accepted socket.
 * @param {(socket: net.Socket) => void} onSocket
 * @param {{ tls?: tls.TlsOptions }} [options]
 * @returns {Promise<Listening & { server: net.Server }>}
 */
export async function listenSocket(onSocket, options = {}) {
  const server = options.tls ? tls.createServer(options.tls, onSocket) : net.createServer(onSocket);
  server.on('tlsClientError', () => {});
  const sockets = track(server);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  const port = /** @type {net.AddressInfo} */ (server.address()).port;
  return { server, port, close: closer(server, sockets) };
}

/**
 * A server on a Unix domain socket path (a named pipe on Windows).
 * @param {(socket: net.Socket) => void} onSocket
 * @returns {Promise<{ path: string, close: () => Promise<void> }>}
 */
export async function listenUds(onSocket) {
  const name = `nexnet-j06-${process.pid}-${Math.random().toString(36).slice(2)}`;
  const socketPath = process.platform === 'win32' ? `\\\\.\\pipe\\${name}` : path.join(os.tmpdir(), `${name}.sock`);
  const server = net.createServer(onSocket);
  const sockets = track(server);
  await new Promise((resolve) => server.listen(socketPath, () => resolve(undefined)));
  return { path: socketPath, close: closer(server, sockets) };
}

/**
 * @typedef {object} UpgradeRequest
 * @property {http.IncomingMessage} req
 * @property {net.Socket} socket
 * @property {Buffer} head
 */

/**
 * An HTTP(S) server. Upgrade requests go to `onUpgrade`; plain requests get `404`.
 * @param {(request: UpgradeRequest) => void} onUpgrade
 * @param {{ tls?: https.ServerOptions }} [options]
 * @returns {Promise<Listening>}
 */
export async function listenHttp(onUpgrade, options = {}) {
  const server = options.tls ? https.createServer(options.tls) : http.createServer();
  server.on('request', (_req, res) => {
    res.statusCode = 404;
    res.end();
  });
  server.on('upgrade', (req, socket, head) => onUpgrade({ req, socket: /** @type {net.Socket} */ (socket), head }));
  server.on('tlsClientError', () => {});
  const sockets = track(server);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  const port = /** @type {net.AddressInfo} */ (server.address()).port;
  return { port, close: closer(server, sockets) };
}

/** TLS options of a test server with the F08 leaf. @returns {tls.TlsOptions} */
export function serverTls() {
  return { cert: certs.serverCert, key: certs.serverKey };
}

/**
 * The `101` response to a WebSocket upgrade request, as bytes.
 * @param {http.IncomingMessage} req
 * @param {Record<string, string>} [extra] Extra or overriding headers.
 * @returns {Buffer}
 */
export function webSocket101(req, extra = {}) {
  const key = String(req.headers['sec-websocket-key']);
  const accept = createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
  /** @type {Record<string, string>} */
  const headers = { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Accept': accept, ...extra };
  const lines = Object.entries(headers).map(([k, v]) => `${k}: ${v}`);
  return Buffer.from(`HTTP/1.1 101 Switching Protocols\r\n${lines.join('\r\n')}\r\n\r\n`, 'latin1');
}

/** The `101` response to an HttpSocket upgrade, as bytes. @returns {Buffer} */
export function httpSocket101() {
  return Buffer.from('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: nexnet-httpsockets\r\n\r\n', 'latin1');
}

/**
 * A plain HTTP response, as bytes.
 * @param {number} status
 * @returns {Buffer}
 */
export function httpStatus(status) {
  return Buffer.from(`HTTP/1.1 ${status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`, 'latin1');
}

/**
 * Encodes a server frame (unmasked unless `mask`).
 * @param {number} opcode
 * @param {Uint8Array} payload
 * @param {{ fin?: boolean, mask?: boolean, rsv?: number }} [options]
 * @returns {Buffer}
 */
export function serverFrame(opcode, payload, { fin = true, mask = false, rsv = 0 } = {}) {
  const len = payload.length;
  const ext = len < 126 ? 0 : len <= 0xffff ? 2 : 8;
  const out = Buffer.alloc(2 + ext + (mask ? 4 : 0) + len);
  out[0] = (fin ? 0x80 : 0) | (rsv << 4) | opcode;
  out[1] = (mask ? 0x80 : 0) | (len < 126 ? len : ext === 2 ? 126 : 127);
  if (ext === 2) out.writeUInt16BE(len, 2);
  if (ext === 8) out.writeBigUInt64BE(BigInt(len), 2);
  const at = 2 + ext + (mask ? 4 : 0);
  out.set(payload, at);
  if (mask) {
    const key = [1, 2, 3, 4];
    out.set(key, 2 + ext);
    for (let i = 0; i < len; i++) out[at + i] ^= key[i & 3];
  }
  return out;
}

/**
 * @typedef {object} ClientFrame
 * @property {boolean} fin
 * @property {number} opcode
 * @property {boolean} masked
 * @property {Buffer} payload Unmasked.
 */

/**
 * Collects the client's frames from a socket (after the upgrade request).
 * @param {net.Socket} socket
 * @param {Buffer} [head]
 */
export function clientFrames(socket, head) {
  /** @type {ClientFrame[]} */
  const frames = [];
  /** @type {Array<() => void>} */
  let waiters = [];
  let buffer = head ?? Buffer.alloc(0);
  const parse = () => {
    for (;;) {
      if (buffer.length < 2) return;
      let len = buffer[1] & 0x7f;
      const masked = (buffer[1] & 0x80) !== 0;
      let at = 2;
      if (len === 126) {
        if (buffer.length < 4) return;
        len = buffer.readUInt16BE(2);
        at = 4;
      } else if (len === 127) {
        if (buffer.length < 10) return;
        len = Number(buffer.readBigUInt64BE(2));
        at = 10;
      }
      const keyAt = at;
      if (masked) at += 4;
      if (buffer.length < at + len) return;
      const payload = Buffer.from(buffer.subarray(at, at + len));
      if (masked) for (let i = 0; i < len; i++) payload[i] ^= buffer[keyAt + (i & 3)];
      frames.push({ fin: (buffer[0] & 0x80) !== 0, opcode: buffer[0] & 0x0f, masked, payload });
      buffer = buffer.subarray(at + len);
      const w = waiters;
      waiters = [];
      for (const f of w) f();
    }
  };
  socket.on('data', (d) => {
    buffer = Buffer.concat([buffer, d]);
    parse();
  });
  parse();
  return {
    frames,
    /**
     * Resolves once at least `n` frames arrived.
     * @param {number} n
     * @returns {Promise<ClientFrame[]>}
     */
    async waitFor(n) {
      while (frames.length < n) await new Promise((resolve) => waiters.push(() => resolve(undefined)));
      return frames;
    },
  };
}

/**
 * A transport's received bytes, collected.
 * @param {import('../../../src/transport/ITransport.js').Transport} transport
 */
export function collect(transport) {
  /** @type {number[]} */
  const bytes = [];
  /** @type {Array<() => void>} */
  let waiters = [];
  /** @type {{ closed: boolean, error: unknown }} */
  const state = { closed: false, error: undefined };
  /** @type {Promise<unknown>} */
  const closed = new Promise((resolve) => {
    transport.onClose((error) => {
      state.closed = true;
      state.error = error;
      resolve(error);
    });
  });
  transport.onData((data) => {
    bytes.push(...data);
    const w = waiters;
    waiters = [];
    for (const f of w) f();
  });
  return {
    bytes,
    state,
    closed,
    /**
     * Resolves once at least `n` bytes arrived.
     * @param {number} n
     * @returns {Promise<number[]>}
     */
    async waitFor(n) {
      while (bytes.length < n) await new Promise((resolve) => waiters.push(() => resolve(undefined)));
      return bytes;
    },
  };
}

/**
 * Collects what a server socket receives.
 * @param {net.Socket} socket
 * @param {Buffer} [head]
 */
export function received(socket, head) {
  /** @type {number[]} */
  const bytes = head ? [...head] : [];
  /** @type {Array<() => void>} */
  let waiters = [];
  socket.on('data', (d) => {
    bytes.push(...d);
    const w = waiters;
    waiters = [];
    for (const f of w) f();
  });
  return {
    bytes,
    /** @param {number} n @returns {Promise<number[]>} */
    async waitFor(n) {
      while (bytes.length < n) await new Promise((resolve) => waiters.push(() => resolve(undefined)));
      return bytes;
    },
  };
}

/** NexNet preamble bytes (protocol spec §1), used as recognizable session bytes. */
export const PREAMBLE = Uint8Array.of(0x4e, 0x6e, 0x50, 0x14, 0, 0, 0, 1);

/** A signal that never aborts. */
export const noSignal = () => new AbortController().signal;
