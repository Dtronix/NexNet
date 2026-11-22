import { X509Certificate, createHash } from 'node:crypto';
import { afterEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { NexusTransportError } from '../../src/contract/errors.js';
import { CloseCode, FrameDecoder, Opcode, WebSocketProtocolError, acceptKey, encodeClientFrame, nodeWebSocketTransport } from '../../src/transport/node/ws.js';
import {
  PREAMBLE, certs, clientFrames, collect, httpStatus, listenHttp, listenSocket, noSignal, serverFrame, serverTls,
  webSocket101,
} from './support/servers.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
});

/**
 * Starts a server whose upgrade handler is `handler` and returns the client config for it.
 * @param {(r: import('./support/servers.js').UpgradeRequest) => void} handler
 * @param {{ secure?: boolean, path?: string }} [options]
 */
async function server(handler, { secure = false, path = '/nexus' } = {}) {
  const s = await listenHttp(handler, secure ? { tls: serverTls() } : {});
  cleanup.push(s.close);
  return `${secure ? 'wss' : 'ws'}://localhost:${s.port}${path}`;
}

/**
 * @param {string} url
 * @param {Partial<import('../../src/contract/config.js').WebSocketTransportConfig>} [extra]
 * @param {AbortSignal} [signal]
 */
function connect(url, extra = {}, signal = noSignal()) {
  return nodeWebSocketTransport({ webSocket: { url, ...extra } }, { signal });
}

describe('RFC 6455 frame codec', () => {
  it('computes Sec-WebSocket-Accept (RFC 6455 §1.3 example)', () => {
    expect(acceptKey('dGhlIHNhbXBsZSBub25jZQ==')).toBe('s3pPLMBiTxaQ9kYGzzhZRbK+xOo=');
  });

  it('client frames are masked, FIN set, with 7-, 16- and 64-bit lengths', () => {
    for (const length of [0, 125, 126, 65535, 65536]) {
      const payload = new Uint8Array(length).map((_, i) => i & 0xff);
      const frame = encodeClientFrame(Opcode.Binary, [payload.subarray(0, 3), payload.subarray(3)]);
      expect(frame[0]).toBe(0x82);
      expect(frame[1] & 0x80).toBe(0x80);
      const len7 = frame[1] & 0x7f;
      const at = len7 < 126 ? 2 : len7 === 126 ? 4 : 10;
      const decoded = len7 < 126 ? len7 : len7 === 126 ? frame.readUInt16BE(2) : Number(frame.readBigUInt64BE(2));
      expect(decoded).toBe(length);
      const key = frame.subarray(at, at + 4);
      const body = frame.subarray(at + 4).map((b, i) => b ^ key[i & 3]);
      expect(Buffer.from(body).equals(Buffer.from(payload))).toBe(true);
    }
  });

  it('decodes fragmented messages with interleaved control frames, split at every byte', () => {
    const wire = Buffer.concat([
      serverFrame(Opcode.Binary, Uint8Array.of(1, 2, 3), { fin: false }),
      serverFrame(Opcode.Ping, Uint8Array.of(9)),
      serverFrame(Opcode.Continuation, new Uint8Array(300).fill(7), { fin: false }),
      serverFrame(Opcode.Continuation, Uint8Array.of(4), { fin: true }),
      serverFrame(Opcode.Binary, new Uint8Array(70000).fill(5)),
      serverFrame(Opcode.Pong, new Uint8Array(0)),
    ]);
    for (const step of [1, 2, 7, 1000, wire.length]) {
      /** @type {number[]} */
      const bytes = [];
      /** @type {Array<[number, number[]]>} */
      const controls = [];
      const decoder = new FrameDecoder({ onBinary: (d) => bytes.push(...d), onControl: (op, p) => controls.push([op, [...p]]) });
      for (let i = 0; i < wire.length; i += step) decoder.push(wire.subarray(i, i + step));
      expect(bytes.length).toBe(3 + 300 + 1 + 70000);
      expect(bytes.slice(0, 4)).toEqual([1, 2, 3, 7]);
      expect(bytes[303]).toBe(4);
      expect(controls).toEqual([[Opcode.Ping, [9]], [Opcode.Pong, []]]);
    }
  });

  it.each([
    ['a masked server frame', serverFrame(Opcode.Binary, Uint8Array.of(1), { mask: true }), CloseCode.ProtocolError],
    ['reserved bits', serverFrame(Opcode.Binary, Uint8Array.of(1), { rsv: 4 }), CloseCode.ProtocolError],
    ['a text message', serverFrame(Opcode.Text, Uint8Array.of(0x41)), CloseCode.UnsupportedData],
    ['a continuation without a message', serverFrame(Opcode.Continuation, Uint8Array.of(1)), CloseCode.ProtocolError],
    ['a new message inside a fragmented one', Buffer.concat([serverFrame(Opcode.Binary, Uint8Array.of(1), { fin: false }), serverFrame(Opcode.Binary, Uint8Array.of(2))]), CloseCode.ProtocolError],
    ['a fragmented control frame', serverFrame(Opcode.Ping, Uint8Array.of(1), { fin: false }), CloseCode.ProtocolError],
    ['a control frame over 125 bytes', serverFrame(Opcode.Ping, new Uint8Array(126)), CloseCode.ProtocolError],
    ['an unknown opcode', serverFrame(0x3, Uint8Array.of(1)), CloseCode.ProtocolError],
  ])('rejects %s', (_name, wire, code) => {
    const decoder = new FrameDecoder({ onBinary: () => {}, onControl: () => {} });
    let error;
    try {
      decoder.push(wire);
    } catch (e) {
      error = e;
    }
    expect(error).toBeInstanceOf(WebSocketProtocolError);
    expect(/** @type {WebSocketProtocolError} */ (error).closeCode).toBe(code);
  });
});

describe('Node WebSocket transport', () => {
  it('sends an RFC 6455 upgrade with no subprotocol, the user headers and the query string', async () => {
    /** @type {import('node:http').IncomingMessage | undefined} */
    let seen;
    const url = await server(({ req, socket }) => {
      seen = req;
      socket.write(webSocket101(req));
    }, { path: '/nexus?access_token=abc' });
    const t = await connect(url, { headers: { Authorization: 'Bearer xyz' } });
    cleanup.push(() => t.close());
    const h = /** @type {import('node:http').IncomingMessage} */ (seen).headers;
    expect(seen?.url).toBe('/nexus?access_token=abc');
    expect(h.upgrade).toBe('websocket');
    expect(h.connection).toMatch(/upgrade/i);
    expect(h['sec-websocket-version']).toBe('13');
    expect(Buffer.from(String(h['sec-websocket-key']), 'base64').length).toBe(16);
    expect(h['sec-websocket-protocol']).toBeUndefined();
    expect(h['sec-websocket-extensions']).toBeUndefined();
    expect(h.origin).toBeUndefined();
    expect(h.authorization).toBe('Bearer xyz');
    expect(t.isSecure).toBe(false);
    expect(t.remoteCertificate).toBeNull();
    expect(t.name).toBe('ws://');
    expect(t.remoteAddress).toMatch(/^(127\.0\.0\.1|::1|::ffff:127\.0\.0\.1)$/);
    expect(t.remotePort).toBe(Number(new URL(url).port));
  });

  it('appends queryToken URL-encoded to the request target, after the URL query; errors do not show it', async () => {
    /** @type {string[]} */
    const seen = [];
    const url = await server(({ req, socket }) => {
      seen.push(String(req.url));
      socket.write(webSocket101(req));
    }, { path: '/nexus?x=1' });
    const t = await connect(url, { queryToken: { name: 'access_token', value: 'a b&c=/é' } });
    cleanup.push(() => t.close());
    expect(seen[0]).toBe('/nexus?x=1&access_token=a%20b%26c%3D%2F%C3%A9');

    const refused = await server(({ req, socket }) => {
      seen.push(String(req.url));
      socket.end(httpStatus(401));
    });
    const error = await connect(refused, { queryToken: { name: 'token', value: 'secret' } }).catch((e) => e);
    expect(seen[1]).toBe('/nexus?token=secret');
    expect(error).toMatchObject({ transportError: 'AuthenticationError', status: 401 });
    expect(String(error.message)).not.toContain('secret');
  });

  it('delivers bytes that arrive in the same packet as the 101 first', async () => {
    const url = await server(({ req, socket }) => {
      socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Binary, PREAMBLE.subarray(0, 5)), serverFrame(Opcode.Binary, PREAMBLE.subarray(5))]));
    });
    const t = await connect(url);
    cleanup.push(() => t.close());
    const c = collect(t);
    expect(await c.waitFor(8)).toEqual([...PREAMBLE]);
  });

  it('streams fragmented messages split across TCP writes and answers Ping with Pong', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    /** @type {import('node:net').Socket | undefined} */
    let serverSocket;
    const url = await server(({ req, socket, head }) => {
      serverSocket = socket;
      fromClient = clientFrames(socket, head);
      socket.setNoDelay(true);
      socket.write(webSocket101(req));
    });
    const t = await connect(url);
    cleanup.push(() => t.close());
    const c = collect(t);
    const wire = Buffer.concat([
      serverFrame(Opcode.Binary, Uint8Array.of(1, 2), { fin: false }),
      serverFrame(Opcode.Ping, Uint8Array.of(42, 43)),
      serverFrame(Opcode.Continuation, Uint8Array.of(3), { fin: false }),
      serverFrame(Opcode.Continuation, Uint8Array.of(4, 5), { fin: true }),
    ]);
    for (let i = 0; i < wire.length; i += 3) {
      serverSocket?.write(wire.subarray(i, i + 3));
      await new Promise((resolve) => setTimeout(resolve, 1));
    }
    expect(await c.waitFor(5)).toEqual([1, 2, 3, 4, 5]);
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0]).toMatchObject({ opcode: Opcode.Pong, masked: true, fin: true });
    expect([...frames[0].payload]).toEqual([42, 43]);
  });

  it('writes each batch as one masked binary message', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      fromClient = clientFrames(socket, head);
      socket.write(webSocket101(req));
    });
    const t = await connect(url);
    cleanup.push(() => t.close());
    await t.write([PREAMBLE, Uint8Array.of(9, 9)]);
    await t.write([new Uint8Array(100_000).fill(1)]);
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(2);
    expect(frames[0]).toMatchObject({ opcode: Opcode.Binary, masked: true, fin: true });
    expect([...frames[0].payload]).toEqual([...PREAMBLE, 9, 9]);
    expect(frames[1].payload.length).toBe(100_000);
  });

  it('answers a server Close with the same code and reports the close', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      fromClient = clientFrames(socket, head);
      socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Binary, Uint8Array.of(7)), serverFrame(Opcode.Close, Uint8Array.of(0x03, 0xe9))]));
    });
    const t = await connect(url);
    const c = collect(t);
    const error = await c.closed;
    expect(c.bytes).toEqual([7]);
    expect(error).toBeInstanceOf(NexusTransportError);
    expect(String(/** @type {Error} */ (error).message)).toContain('1001');
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0].opcode).toBe(Opcode.Close);
    expect(frames[0].payload.readUInt16BE(0)).toBe(1001);
    await expect(t.write([Uint8Array.of(1)])).rejects.toBeInstanceOf(NexusTransportError);
  });

  it('a server Close 1000 is a clean close; a dropped connection is reported as 1006', async () => {
    const clean = await server(({ req, socket }) => socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Close, Uint8Array.of(0x03, 0xe8))])));
    const t1 = await connect(clean);
    expect(await collect(t1).closed).toBeUndefined();

    const dropped = await server(({ req, socket }) => socket.end(webSocket101(req)));
    const t2 = await connect(dropped);
    const error = await collect(t2).closed;
    expect(String(/** @type {Error} */ (error).message)).toContain('1006');
  });

  it('close() sends Close 1000 and finishes when the server answers', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      fromClient = clientFrames(socket, head);
      socket.write(webSocket101(req));
      void fromClient.waitFor(1).then((f) => {
        if (f[0].opcode === Opcode.Close) socket.end(serverFrame(Opcode.Close, f[0].payload));
      });
    });
    const t = await connect(url);
    const c = collect(t);
    const started = performance.now();
    await t.close();
    expect(performance.now() - started).toBeLessThan(240);
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0]).toMatchObject({ opcode: Opcode.Close, masked: true });
    expect(frames[0].payload.readUInt16BE(0)).toBe(CloseCode.Normal);
    await c.closed;
  });

  it('fails the connection with 1003 on a text message', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      fromClient = clientFrames(socket, head);
      socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Text, Buffer.from('hi'))]));
    });
    const t = await connect(url);
    const error = await collect(t).closed;
    expect(error).toMatchObject({ name: 'NexusTransportError', transportError: 'ProtocolError' });
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0].payload.readUInt16BE(0)).toBe(CloseCode.UnsupportedData);
  });

  it.each([
    ['a wrong Sec-WebSocket-Accept', { 'Sec-WebSocket-Accept': 'AAAA' }],
    ['a subprotocol', { 'Sec-WebSocket-Protocol': 'chat' }],
    ['an extension', { 'Sec-WebSocket-Extensions': 'permessage-deflate' }],
  ])('rejects a 101 with %s', async (_name, extra) => {
    const url = await server(({ req, socket }) => socket.write(webSocket101(req, extra)));
    await expect(connect(url)).rejects.toMatchObject({ name: 'NexusTransportError', transportError: 'ProtocolError', reason: DisconnectReason.SocketError });
  });

  it.each([
    [401, 'AuthenticationError', DisconnectReason.Authentication],
    [403, 'AuthenticationError', DisconnectReason.Authentication],
    [404, 'ProtocolError', DisconnectReason.SocketError],
    [500, 'InternalError', DisconnectReason.SocketError],
    [200, 'ProtocolError', DisconnectReason.SocketError],
  ])('maps a refused upgrade (HTTP %i)', async (status, kind, reason) => {
    const url = await server(({ socket }) => socket.end(httpStatus(status)));
    await expect(connect(url)).rejects.toMatchObject({ name: 'NexusTransportError', transportError: kind, reason, status });
  });

  it.each([301, 302, 307])('does not follow a redirect (HTTP %i): ProtocolError, no second request', async (status) => {
    let targetConnections = 0;
    const target = await listenSocket((socket) => {
      targetConnections++;
      socket.destroy();
    });
    cleanup.push(target.close);
    let requests = 0;
    const url = await server(({ socket }) => {
      requests++;
      socket.end(Buffer.from(`HTTP/1.1 ${status} Redirect\r\nLocation: http://127.0.0.1:${target.port}/elsewhere\r\n`
        + 'Content-Length: 0\r\nConnection: close\r\n\r\n', 'latin1'));
    });
    await expect(connect(url)).rejects.toMatchObject({
      name: 'NexusTransportError', transportError: 'ProtocolError', reason: DisconnectReason.SocketError, status,
    });
    expect(requests).toBe(1);
    expect(targetConnections).toBe(0);
  });

  it('a refused connection is SocketError/ConnectionRefused', async () => {
    const url = await server(() => {});
    const port = new URL(url).port;
    await cleanup.pop()?.();
    await expect(connect(`ws://127.0.0.1:${port}/nexus`)).rejects.toMatchObject({ reason: DisconnectReason.SocketError, transportError: 'ConnectionRefused' });
  });

  it('aborting the signal rejects with its reason', async () => {
    const url = await server(() => {}); // never answers
    const controller = new AbortController();
    const pending = connect(url, {}, controller.signal);
    const reason = new DOMException('stop', 'TimeoutError');
    setTimeout(() => controller.abort(reason), 20);
    await expect(pending).rejects.toBe(reason);
  });

  describe('wss', () => {
    it('connects with a trusted CA, verifies the host name and exposes the server certificate', async () => {
      const url = await server(({ req, socket }) => socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Binary, PREAMBLE)])), { secure: true });
      const t = await connect(url, { tls: { ca: certs.ca } });
      cleanup.push(() => t.close());
      expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
      expect(t.isSecure).toBe(true);
      expect(t.name).toBe('wss://');
      expect(t.remoteCertificate).toBeInstanceOf(X509Certificate);
      expect(/** @type {X509Certificate} */ (t.remoteCertificate).subject).toContain('CN=localhost');
    });

    it('a matching pin connects with rejectUnauthorized: false', async () => {
      const url = await server(({ req, socket }) => socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Binary, PREAMBLE)])), { secure: true });
      const t = await connect(url, { tls: { rejectUnauthorized: false, pins: [certs.serverPin] } });
      cleanup.push(() => t.close());
      expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
    });

    it('a pin mismatch fails before the upgrade request is sent, even with rejectUnauthorized: false', async () => {
      let requests = 0;
      const url = await server(() => { requests++; }, { secure: true });
      await expect(connect(url, { headers: { Authorization: 'Bearer secret' }, tls: { rejectUnauthorized: false, pins: [Buffer.alloc(32).toString('base64')] } }))
        .rejects.toMatchObject({ reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(requests).toBe(0);
    });

    it('an untrusted server certificate is an authentication failure', async () => {
      const url = await server(() => {}, { secure: true });
      await expect(connect(url)).rejects.toMatchObject({ reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
    });

    it('a host-name mismatch is an authentication failure', async () => {
      const url = await server(() => {}, { secure: true });
      await expect(connect(url, { tls: { ca: certs.ca, servername: 'example.com' } }))
        .rejects.toMatchObject({ reason: DisconnectReason.Authentication });
    });

    it('checkServerIdentity runs after the host-name check (pinning)', async () => {
      const url = await server(({ req, socket }) => socket.write(webSocket101(req)), { secure: true });
      /** @type {string[]} */
      const pins = [];
      const ok = await connect(url, {
        tls: {
          ca: certs.ca,
          checkServerIdentity: (_host, cert) => {
            pins.push(String(/** @type {any} */ (cert).pubkey ? hashSpki(/** @type {any} */ (cert).pubkey) : ''));
            return undefined;
          },
        },
      });
      await ok.close();
      expect(pins).toEqual([certs.serverPin]);
      await expect(connect(url, { tls: { ca: certs.ca, checkServerIdentity: () => new Error('pin mismatch') } }))
        .rejects.toMatchObject({ reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
    });
  });
});

/** @param {Buffer} spki @returns {string} */
function hashSpki(spki) {
  return createHash('sha256').update(spki).digest('base64');
}
