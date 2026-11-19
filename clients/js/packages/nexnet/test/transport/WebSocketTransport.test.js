// The platform-WebSocket transport (the browser entry's), run in Node against its global WebSocket.
import { afterEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { NexusClient } from '../../src/client/NexusClient.js';
import { webSocketTransport } from '../../src/transport/WebSocketTransport.js';
import { Opcode } from '../../src/transport/node/ws.js';
import { PREAMBLE, clientFrames, collect, httpStatus, listenHttp, noSignal, serverFrame, webSocket101 } from './support/servers.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
});

/** @param {(r: import('./support/servers.js').UpgradeRequest) => void} handler */
async function server(handler) {
  const s = await listenHttp(handler);
  cleanup.push(s.close);
  return `ws://127.0.0.1:${s.port}/nexus`;
}

/** @param {string} url */
function connect(url) {
  return webSocketTransport({ webSocket: { url } }, { signal: noSignal() });
}

describe.skipIf(typeof globalThis.WebSocket !== 'function')('platform WebSocket transport', () => {
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
