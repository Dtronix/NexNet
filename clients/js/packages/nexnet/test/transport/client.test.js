// The Node client over real sockets: the session starts on a transport the factory opened.
import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionResultState, DisconnectReason } from '../../src/contract/enums.js';
import { ClientNexusBase } from '../../src/contract/nexus.js';
import { ProxyBase } from '../../src/contract/proxy.js';
import { NexusClient } from '../../src/node.js';
import { listenHttp, listenSocket, received, webSocket101, httpSocket101, clientFrames } from './support/servers.js';
import { Opcode } from '../../src/transport/node/ws.js';

/** @type {Array<() => Promise<void>>} */
let cleanup = [];
afterEach(async () => {
  for (const c of cleanup.reverse()) await c();
  cleanup = [];
});

class Proxy extends ProxyBase {}
class Nexus extends ClientNexusBase {}

/** @param {import('../../src/contract/config.js').ClientConfig} config */
function client(config) {
  return NexusClient.create({ handshakeTimeout: 200, ...config }, new Nexus(), {
    handshake: { version: null, serverNexusHash: 1, clientNexusHash: 2, acceptedIds: new Set([0]) },
    proxy: Proxy,
  });
}

/** 'NnP' + 0x14: the start of the preamble (protocol spec §1). */
const PREAMBLE_START = [0x4e, 0x6e, 0x50, 0x14];

describe('NexusClient (node entry) over real transports', () => {
  it('tcp: writes the preamble first; a server that closes fails the connect', async () => {
    /** @type {Promise<number[]>} */
    let first = Promise.resolve([]);
    const s = await listenSocket((socket) => {
      const r = received(socket);
      first = r.waitFor(8).then((b) => { socket.end(); return b; });
    });
    cleanup.push(s.close);
    const result = await client({ tcp: { host: '127.0.0.1', port: s.port } }).tryConnect();
    expect((await first).slice(0, 4)).toEqual(PREAMBLE_START);
    expect(result).toMatchObject({ success: false, state: ConnectionResultState.Disconnected });
  });

  it('httpSocket and webSocket carry the session after the upgrade', async () => {
    /** @type {Promise<number[]>} */
    let viaHttpSocket = Promise.resolve([]);
    /** @type {Promise<number[]>} */
    let viaWebSocket = Promise.resolve([]);
    const s = await listenHttp(({ req, socket, head }) => {
      if (req.headers.upgrade === 'websocket') {
        const frames = clientFrames(socket, head);
        socket.write(webSocket101(req));
        viaWebSocket = frames.waitFor(1).then((f) => { socket.destroy(); return f[0].opcode === Opcode.Binary ? [...f[0].payload] : []; });
      } else {
        const r = received(socket, head);
        socket.write(httpSocket101());
        viaHttpSocket = r.waitFor(8).then((b) => { socket.destroy(); return b; });
      }
    });
    cleanup.push(s.close);
    await client({ httpSocket: { url: `http://127.0.0.1:${s.port}/nexus` } }).tryConnect();
    expect((await viaHttpSocket).slice(0, 4)).toEqual(PREAMBLE_START);
    await client({ webSocket: { url: `ws://127.0.0.1:${s.port}/nexus` } }).tryConnect();
    expect((await viaWebSocket).slice(0, 4)).toEqual(PREAMBLE_START);
  });

  it('a 401 upgrade fails the connect with DisconnectReason.Authentication', async () => {
    const s = await listenHttp(({ socket }) => socket.end('HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n'));
    cleanup.push(s.close);
    const result = await client({ httpSocket: { url: `http://127.0.0.1:${s.port}/nexus` } }).tryConnect();
    expect(result).toMatchObject({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.Authentication });
  });
});
