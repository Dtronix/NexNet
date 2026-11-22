// The platform-WebSocket transport (the browser entry's), run in Node against its global WebSocket.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectionResultState, DisconnectReason } from '../../src/contract/enums.js';
import { ClientNexusBase } from '../../src/contract/nexus.js';
import { ProxyBase } from '../../src/contract/proxy.js';
import { NexusClient } from '../../src/client/NexusClient.js';
import { webSocketTransport } from '../../src/transport/WebSocketTransport.js';
import { Opcode } from '../../src/transport/node/ws.js';
import { PREAMBLE, clientFrames, collect, httpStatus, listenHttp, noSignal, serverFrame, webSocket101 } from './support/servers.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
  vi.unstubAllGlobals();
});

/** @param {(r: import('./support/servers.js').UpgradeRequest) => void} handler */
async function server(handler) {
  const s = await listenHttp(handler);
  cleanup.push(s.close);
  return `ws://127.0.0.1:${s.port}/nexus`;
}

/**
 * @param {string} url
 * @param {Partial<import('../../src/contract/config.js').WebSocketTransportConfig>} [extra]
 */
function connect(url, extra = {}) {
  return webSocketTransport({ webSocket: { url, ...extra } }, { signal: noSignal() });
}

describe.skipIf(typeof globalThis.WebSocket !== 'function')('platform WebSocket transport', () => {
  it('appends queryToken URL-encoded to the URL query; a failure message does not show it', async () => {
    /** @type {string[]} */
    const seen = [];
    const url = await server(({ req, socket }) => {
      seen.push(String(req.url));
      socket.write(webSocket101(req));
    });
    const t = await connect(url + '?x=1', { queryToken: { name: 'access_token', value: 'a b&c' } });
    cleanup.push(() => t.close());
    expect(seen[0]).toBe('/nexus?x=1&access_token=a%20b%26c');

    const refused = await server(({ req, socket }) => {
      seen.push(String(req.url));
      socket.end(httpStatus(401));
    });
    const error = await connect(refused, { queryToken: { name: 'token', value: 'secret' } }).catch((e) => e);
    expect(seen[1]).toBe('/nexus?token=secret');
    expect(error).toMatchObject({ name: 'NexusTransportError' });
    expect(String(error.message)).not.toContain('secret');
  });

  it('is the core entry\'s webSocket factory', () => {
    expect(NexusClient.transports.webSocket).toBe(webSocketTransport);
  });

  it('requests no subprotocol, receives binary messages as one stream and sends binary messages', async () => {
    /** @type {import('node:http').IncomingMessage | undefined} */
    let seen;
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      seen = req;
      fromClient = clientFrames(socket, head);
      socket.write(Buffer.concat([
        webSocket101(req),
        serverFrame(Opcode.Binary, PREAMBLE.subarray(0, 3)),
        serverFrame(Opcode.Binary, PREAMBLE.subarray(3), { fin: false }),
        serverFrame(Opcode.Continuation, Uint8Array.of(), { fin: true }),
      ]));
    });
    const t = await connect(url);
    cleanup.push(() => t.close());
    expect(seen?.headers['sec-websocket-protocol']).toBeUndefined();
    expect(t.isSecure).toBe(false);
    expect(t.name).toBe('ws://');
    expect(t.remoteCertificate).toBeNull();
    expect(await collect(t).waitFor(8)).toEqual([...PREAMBLE]);

    await t.write([Uint8Array.of(1), Uint8Array.of(2, 3)]);
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0]).toMatchObject({ opcode: Opcode.Binary, fin: true });
    expect([...frames[0].payload]).toEqual([1, 2, 3]);
  });

  it('a text message fails the transport', async () => {
    const url = await server(({ req, socket }) => socket.write(Buffer.concat([webSocket101(req), serverFrame(Opcode.Text, Buffer.from('x'))])));
    const t = await connect(url);
    expect(await collect(t).closed).toMatchObject({ name: 'NexusTransportError', transportError: 'ProtocolError' });
  });

  it('a refused upgrade is a SocketError connect failure', async () => {
    const url = await server(({ socket }) => socket.end(httpStatus(403)));
    await expect(connect(url)).rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.SocketError });
  });

  it('an \'error\' with no \'close\' still fails the connect (Node 22 undici on a refused upgrade)', async () => {
    // Node 22 fires only 'error' for a non-101 response and leaves the socket CONNECTING; browsers and Node 24 follow
    // it with 'close'. A stub reproduces the Node 22 order on every runtime.
    class ErrorOnlyWebSocket extends EventTarget {
      readyState = 0;
      binaryType = 'blob';
      constructor() {
        super();
        setTimeout(() => this.dispatchEvent(new Event('error')), 0);
      }
      close() {}
    }
    vi.stubGlobal('WebSocket', ErrorOnlyWebSocket);
    await expect(connect('ws://127.0.0.1:1/nexus')).rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.SocketError });
  });

  it('a connect the socket does not settle within connectionTimeout fails with ConnectionTimeout (WebKit no-server)', async () => {
    // WebKit can take longer than a short connectionTimeout to report a refused connect; the connect then ends by the
    // timeout, which must still be a transport error with a kind (C# TransportException(ConnectionTimeout)).
    class PendingWebSocket extends EventTarget {
      readyState = 0;
      binaryType = 'blob';
      close() {}
    }
    vi.stubGlobal('WebSocket', PendingWebSocket);
    class Proxy extends ProxyBase {}
    class Nexus extends ClientNexusBase {}
    const create = () => NexusClient.create({ webSocket: { url: 'ws://127.0.0.1:1/nexus' }, connectionTimeout: 20 }, new Nexus(), {
      handshake: { version: null, serverNexusHash: 1, clientNexusHash: 2, acceptedIds: new Set([0]) },
      proxy: Proxy,
    });
    const expected = { name: 'NexusTransportError', transportError: 'ConnectionTimeout', reason: DisconnectReason.Timeout };
    await expect(create().connect()).rejects.toMatchObject(expected);
    expect(await create().tryConnect()).toMatchObject({
      success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.Timeout, error: expected,
    });
  });

  it('close() sends Close 1000', async () => {
    /** @type {ReturnType<typeof clientFrames> | undefined} */
    let fromClient;
    const url = await server(({ req, socket, head }) => {
      fromClient = clientFrames(socket, head);
      socket.write(webSocket101(req));
      void fromClient.waitFor(1).then((f) => socket.end(serverFrame(Opcode.Close, f[0].payload)));
    });
    const t = await connect(url);
    const c = collect(t);
    await t.close();
    const frames = await /** @type {ReturnType<typeof clientFrames>} */ (fromClient).waitFor(1);
    expect(frames[0].opcode).toBe(Opcode.Close);
    expect(frames[0].payload.readUInt16BE(0)).toBe(1000);
    expect(await c.closed).toBeUndefined();
  });

  it('an aborted signal rejects with its reason', async () => {
    const reason = new Error('stop');
    await expect(webSocketTransport({ webSocket: { url: 'ws://127.0.0.1:1/' } }, { signal: AbortSignal.abort(reason) })).rejects.toBe(reason);
  });
});
