import { X509Certificate } from 'node:crypto';
import { afterEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { httpSocketTransport } from '../../src/transport/node/httpSocket.js';
import { PREAMBLE, certs, collect, httpSocket101, httpStatus, listenHttp, noSignal, received, serverTls } from './support/servers.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
});

/**
 * @param {(r: import('./support/servers.js').UpgradeRequest) => void} handler
 * @param {{ secure?: boolean, alpn?: string[] }} [options]
 */
async function server(handler, { secure = false, alpn } = {}) {
  const s = await listenHttp(handler, secure ? { tls: { ...serverTls(), ...(alpn ? { ALPNProtocols: alpn } : {}) } } : {});
  cleanup.push(s.close);
  return `${secure ? 'https' : 'http'}://localhost:${s.port}/nexus`;
}

/**
 * @param {string} url
 * @param {Partial<import('../../src/contract/config.js').HttpSocketTransportConfig>} [extra]
 */
function connect(url, extra = {}) {
  return httpSocketTransport({ httpSocket: { url, ...extra } }, { signal: noSignal() });
}

describe('HttpSocket transport', () => {
  it('upgrades to nexnet-httpsockets and feeds the bytes that came with the 101 to the session first', async () => {
    /** @type {import('node:http').IncomingMessage | undefined} */
    let seen;
    /** @type {ReturnType<typeof received> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      seen = req;
      fromClient = received(socket, head);
      // The 101 and the server's first session bytes in one packet.
      socket.write(Buffer.concat([httpSocket101(), PREAMBLE]));
    });
    const t = await connect(url, { headers: { Authorization: 'Bearer t' } });
    cleanup.push(() => t.close());
    const c = collect(t);
    expect(await c.waitFor(8)).toEqual([...PREAMBLE]);
    expect(seen?.method).toBe('GET');
    expect(seen?.httpVersion).toBe('1.1');
    expect(seen?.headers.upgrade).toBe('nexnet-httpsockets');
    expect(seen?.headers.connection).toMatch(/upgrade/i);
    expect(seen?.headers.authorization).toBe('Bearer t');
    expect(seen?.headers['content-length']).toBeUndefined();
    expect(seen?.headers['transfer-encoding']).toBeUndefined();
    expect(t.isSecure).toBe(false);
    expect(t.name).toBe('http://');

    // After the upgrade the stream is raw bytes, both ways.
    await t.write([Uint8Array.of(1, 2), Uint8Array.of(3)]);
    expect(await /** @type {ReturnType<typeof received>} */ (fromClient).waitFor(3)).toEqual([1, 2, 3]);
  });

  it.each([
    [401, 'AuthenticationError', DisconnectReason.Authentication],
    [403, 'AuthenticationError', DisconnectReason.Authentication],
    [404, 'InternalError', DisconnectReason.SocketError],
    [500, 'InternalError', DisconnectReason.SocketError],
    [200, 'InternalError', DisconnectReason.SocketError],
  ])('maps HTTP %i to %s', async (status, kind, reason) => {
    const url = await server(({ socket }) => socket.end(httpStatus(status)));
    await expect(connect(url)).rejects.toMatchObject({ name: 'NexusTransportError', transportError: kind, reason, status });
  });

  it('rejects a 101 that names another upgrade token', async () => {
    const url = await server(({ socket }) => socket.write('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n'));
    await expect(connect(url)).rejects.toMatchObject({ transportError: 'ProtocolError' });
  });

  it('reports the end of the stream', async () => {
    const url = await server(({ socket }) => socket.end(Buffer.concat([httpSocket101(), Uint8Array.of(9)])));
    const t = await connect(url);
    const c = collect(t);
    await c.closed;
    expect(c.bytes).toEqual([9]);
    await expect(t.write([Uint8Array.of(1)])).rejects.toMatchObject({ name: 'NexusTransportError' });
  });

  it('https: offers ALPN http/1.1 only, verifies the server and exposes its certificate', async () => {
    /** @type {string | false | null | undefined} */
    let alpn;
    const url = await server(({ socket }) => {
      alpn = /** @type {import('node:tls').TLSSocket} */ (socket).alpnProtocol;
      socket.write(Buffer.concat([httpSocket101(), PREAMBLE]));
    }, { secure: true, alpn: ['h2', 'http/1.1'] });
    const t = await connect(url, { tls: { ca: certs.ca } });
    cleanup.push(() => t.close());
    expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
    expect(alpn).toBe('http/1.1');
    expect(t.isSecure).toBe(true);
    expect(t.name).toBe('https://');
    expect(t.remoteCertificate).toBeInstanceOf(X509Certificate);
  });

  it('https: a matching pin connects; a mismatch fails before the request is sent', async () => {
    let requests = 0;
    const url = await server(({ socket }) => {
      requests++;
      socket.write(Buffer.concat([httpSocket101(), PREAMBLE]));
    }, { secure: true });
    const t = await connect(url, { tls: { ca: certs.ca, pins: [certs.serverPin] } });
    cleanup.push(() => t.close());
    expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
    expect(requests).toBe(1);
    await expect(connect(url, { tls: { rejectUnauthorized: false, pins: [Buffer.alloc(32, 1).toString('base64')] } }))
      .rejects.toMatchObject({ reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(requests).toBe(1);
  });

  it('https: an untrusted server is an authentication failure', async () => {
    const url = await server(() => {}, { secure: true });
    await expect(connect(url)).rejects.toMatchObject({ reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
  });
});
