import { X509Certificate } from 'node:crypto';
import { afterEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { tcpTlsTransport, tcpTransport, udsTransport } from '../../src/transport/node/tcp.js';
import { PREAMBLE, certs, collect, listenSocket, listenUds, noSignal, received, serverTls } from './support/servers.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
});

describe('TCP transport', () => {
  it('carries raw bytes both ways with TCP_NODELAY, and reports the server closing', async () => {
    /** @type {ReturnType<typeof received> | undefined} */
    let fromClient;
    /** @type {import('node:net').Socket | undefined} */
    let serverSocket;
    const s = await listenSocket((socket) => {
      serverSocket = socket;
      fromClient = received(socket);
      socket.write(PREAMBLE);
    });
    cleanup.push(s.close);
    const t = await tcpTransport({ tcp: { host: '127.0.0.1', port: s.port } }, { signal: noSignal() });
    const c = collect(t);
    expect(await c.waitFor(8)).toEqual([...PREAMBLE]);
    expect(t.isSecure).toBe(false);
    expect(t.name).toBe('tcp');
    expect(t.remoteCertificate).toBeNull();

    await t.write([Uint8Array.of(1), Uint8Array.of(2, 3)]);
    expect(await /** @type {ReturnType<typeof received>} */ (fromClient).waitFor(3)).toEqual([1, 2, 3]);

    serverSocket?.end();
    await c.closed;
    await expect(t.write([Uint8Array.of(1)])).rejects.toMatchObject({ name: 'NexusTransportError' });
  });

  it('a large write resolves once the socket drains', async () => {
    let total = 0;
    const s = await listenSocket((socket) => socket.on('data', (d) => { total += d.length; }));
    cleanup.push(s.close);
    const t = await tcpTransport({ tcp: { host: '127.0.0.1', port: s.port } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    const big = new Uint8Array(8 * 1024 * 1024);
    await t.write([big]);
    await t.write([big]);
    await expect.poll(() => total, { timeout: 5000 }).toBe(2 * big.length);
  });

  it('close() ends the connection', async () => {
    /** @type {Promise<void>} */
    let ended = Promise.resolve();
    const s = await listenSocket((socket) => {
      ended = new Promise((resolve) => socket.on('end', () => resolve()));
      socket.resume();
    });
    cleanup.push(s.close);
    const t = await tcpTransport({ tcp: { host: '127.0.0.1', port: s.port } }, { signal: noSignal() });
    const c = collect(t);
    await t.close();
    await ended;
    await c.closed;
  });

  it('a refused connection is SocketError/ConnectionRefused', async () => {
    const s = await listenSocket(() => {});
    await s.close();
    await expect(tcpTransport({ tcp: { host: '127.0.0.1', port: s.port } }, { signal: noSignal() }))
      .rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.SocketError, transportError: 'ConnectionRefused' });
  });

  it('an aborted signal rejects with its reason', async () => {
    const reason = new Error('stop');
    await expect(tcpTransport({ tcp: { host: '127.0.0.1', port: 1 } }, { signal: AbortSignal.abort(reason) })).rejects.toBe(reason);
  });
});

describe('TLS transport', () => {
  it('connects to a trusted server by name, with SNI, and exposes its certificate', async () => {
    /** @type {string | false | null | undefined} */
    let sni;
    const s = await listenSocket((socket) => {
      sni = /** @type {import('node:tls').TLSSocket} */ (socket).servername;
      socket.write(PREAMBLE);
    }, { tls: serverTls() });
    cleanup.push(s.close);
    const t = await tcpTlsTransport({ tcpTls: { host: 'localhost', port: s.port, tls: { ca: certs.ca } } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
    expect(sni).toBe('localhost');
    expect(t.isSecure).toBe(true);
    expect(t.name).toBe('tls');
    expect(t.remoteCertificate).toBeInstanceOf(X509Certificate);
  });

  it('sends no SNI for an IP literal and still verifies the IP SAN', async () => {
    /** @type {string | false | null | undefined} */
    let sni;
    const s = await listenSocket((socket) => { sni = /** @type {import('node:tls').TLSSocket} */ (socket).servername; }, { tls: serverTls() });
    cleanup.push(s.close);
    const t = await tcpTlsTransport({ tcpTls: { host: '127.0.0.1', port: s.port, tls: { ca: certs.ca } } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    expect(sni || undefined).toBeUndefined();
  });

  it('presents a client certificate when the server asks for one', async () => {
    /** @type {boolean | undefined} */
    let authorized;
    const s = await listenSocket((socket) => {
      authorized = /** @type {import('node:tls').TLSSocket} */ (socket).authorized;
      socket.write(PREAMBLE);
    }, { tls: { ...serverTls(), requestCert: true, rejectUnauthorized: true, ca: certs.ca } });
    cleanup.push(s.close);
    const t = await tcpTlsTransport({ tcpTls: { host: 'localhost', port: s.port, tls: { ca: certs.ca, cert: certs.clientCert, key: certs.clientKey } } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    await collect(t).waitFor(8);
    expect(authorized).toBe(true);
  });

  it.each([
    ['an untrusted server', {}],
    ['a host-name mismatch', { ca: certs.ca, servername: 'example.com' }],
    ['a rejected pin', { ca: certs.ca, checkServerIdentity: () => new Error('pin mismatch') }],
  ])('%s is an authentication failure', async (_name, tls) => {
    const s = await listenSocket(() => {}, { tls: serverTls() });
    cleanup.push(s.close);
    await expect(tcpTlsTransport({ tcpTls: { host: 'localhost', port: s.port, tls } }, { signal: noSignal() }))
      .rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.Authentication, transportError: 'AuthenticationError' });
  });

  it('rejectUnauthorized: false accepts an untrusted server', async () => {
    const s = await listenSocket((socket) => socket.write(PREAMBLE), { tls: serverTls() });
    cleanup.push(s.close);
    const t = await tcpTlsTransport({ tcpTls: { host: 'localhost', port: s.port, tls: { rejectUnauthorized: false } } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    await collect(t).waitFor(8);
  });
});

describe('UDS transport', () => {
  it(`connects to a socket path (${process.platform === 'win32' ? 'a named pipe on Windows' : 'AF_UNIX'}) and is secure`, async () => {
    /** @type {ReturnType<typeof received> | undefined} */
    let fromClient;
    const s = await listenUds((socket) => {
      fromClient = received(socket);
      socket.write(PREAMBLE);
    });
    cleanup.push(s.close);
    const t = await udsTransport({ uds: { path: s.path } }, { signal: noSignal() });
    cleanup.push(() => t.close());
    expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);
    await t.write([Uint8Array.of(5)]);
    expect(await /** @type {ReturnType<typeof received>} */ (fromClient).waitFor(1)).toEqual([5]);
    expect(t.isSecure).toBe(true);
    expect(t.name).toBe('uds');
  });

  it('a missing socket is SocketError', async () => {
    const path = process.platform === 'win32' ? '\\\\.\\pipe\\nexnet-j06-missing' : '/tmp/nexnet-j06-missing.sock';
    await expect(udsTransport({ uds: { path } }, { signal: noSignal() }))
      .rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.SocketError });
  });
});
