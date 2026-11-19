/**
 * Builds a client over a {@link LoopbackTransport} with a {@link ManualClock}.
 * @module
 */

import { ClientNexusBase } from '../../../src/contract/nexus.js';
import { ProxyBase } from '../../../src/contract/proxy.js';
import { NexusClient } from '../../../src/client/NexusClient.js';
import { encodeFrame, encodePreamble } from '../../../src/session/frames.js';
import { MessageType } from '../../../src/session/protocol.js';
import { encodeServerGreeting } from '../../../src/session/wire.js';
import { LoopbackTransport, concat } from './LoopbackTransport.js';
import { ManualClock } from './ManualClock.js';

export class TestProxy extends ProxyBase {}

export class TestNexus extends ClientNexusBase {
  /** @type {Array<[string, unknown]>} */
  calls = [];
  /** @param {boolean} isReconnected */
  onConnected(isReconnected) { this.calls.push(['connected', isReconnected]); }
  /** @param {number} reason */
  onDisconnected(reason) { this.calls.push(['disconnected', reason]); }
}

export const CLIENT_HASH = 401298355;
export const SERVER_HASH = -1288190245;

/**
 * Lets every pending microtask and promise continuation run.
 * @returns {Promise<void>}
 */
export function tick() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

/**
 * @typedef {object} HarnessOptions
 * @property {Partial<import('../../../src/contract/config.js').ClientConfig>} [config]
 * @property {{ version?: string | null, serverNexusHash?: number, clientNexusHash?: number }} [handshake]
 * @property {{ isSecure?: boolean, holdWrites?: boolean }} [transport]
 * @property {'tcp' | 'tcpTls' | 'uds' | 'webSocket' | 'httpSocket'} [kind]
 */

/**
 * @param {HarnessOptions} [options]
 */
export function createHarness({ config = {}, handshake = {}, transport: transportOptions = {}, kind = 'tcp' } = {}) {
  const clock = new ManualClock();
  /** @type {Array<[string, unknown, unknown]>} */
  const logs = [];
  /** @type {LoopbackTransport[]} */
  const transports = [];
  /** @type {Array<{ config: unknown, signal: AbortSignal }>} */
  const factoryCalls = [];
  /** @type {((config: unknown, options: { signal: AbortSignal }) => Promise<LoopbackTransport>) | null} */
  let factoryOverride = null;

  /** @type {import('../../../src/transport/ITransport.js').TransportFactory} */
  const factory = async (cfg, opts) => {
    factoryCalls.push({ config: cfg, signal: opts.signal });
    if (factoryOverride) return factoryOverride(cfg, opts);
    const t = new LoopbackTransport(transportOptions);
    transports.push(t);
    return t;
  };

  /** @extends {NexusClient<any, any>} */
  class HarnessClient extends NexusClient {
    static transports = Object.freeze({ webSocket: factory, httpSocket: factory, tcp: factory, tcpTls: factory, uds: factory });
  }

  /** @type {Record<string, unknown>} */
  const transportConfig = {
    tcp: { tcp: { host: 'localhost', port: 1 } },
    tcpTls: { tcpTls: { host: 'localhost', port: 1 } },
    uds: { uds: { path: '/tmp/x.sock' } },
    webSocket: { webSocket: { url: 'ws://localhost:1/nexus' } },
    httpSocket: { httpSocket: { url: 'http://localhost:1/nexus' } },
  }[kind];

  const nexus = new TestNexus();
  const client = HarnessClient.create(
    /** @type {import('../../../src/contract/config.js').ClientConfig} */ ({
      ...(/** @type {object} */ (transportConfig)),
      clock,
      logger: { log: (level, message, error) => logs.push([level, message, error]) },
      // The manual clock only moves when a test advances it; tests of the delay set it explicitly.
      disconnectDelay: 0,
      ...config,
    }),
    nexus,
    {
      handshake: {
        version: handshake.version ?? null,
        serverNexusHash: handshake.serverNexusHash ?? SERVER_HASH,
        clientNexusHash: handshake.clientNexusHash ?? CLIENT_HASH,
        acceptedIds: new Set([0]),
      },
      proxy: TestProxy,
    },
  );

  /** @type {Array<{ type: string, detail: any }>} */
  const events = [];
  client.addEventListener('statechange', (e) => events.push({ type: 'statechange', detail: /** @type {CustomEvent} */ (e).detail }));
  client.addEventListener('disconnected', (e) => events.push({ type: 'disconnected', detail: /** @type {CustomEvent} */ (e).detail }));

  return {
    client,
    nexus,
    clock,
    logs,
    events,
    transports,
    factoryCalls,
    /** @param {(config: unknown, options: { signal: AbortSignal }) => Promise<LoopbackTransport>} f */
    setFactory(f) { factoryOverride = f; },
    /** The transport of the latest connect attempt. @returns {LoopbackTransport} */
    get transport() {
      const t = transports[transports.length - 1];
      if (!t) throw new Error('no transport yet');
      return t;
    },
  };
}

/**
 * Preamble + ServerGreeting bytes.
 * @param {Partial<import('../../../src/session/wire.js').ServerGreeting>} [fields]
 * @returns {Uint8Array}
 */
export function serverHello(fields = {}) {
  const body = encodeServerGreeting({
    version: CLIENT_HASH,
    clientId: 7n,
    maxMessageSize: 4 * 1024 * 1024,
    invocationCredits: 32,
    idleTimeoutMs: null,
    ...fields,
  });
  return concat([encodePreamble(), encodeFrame(MessageType.ServerGreeting, body)]);
}

/**
 * Connects a harness client: starts the connect, answers with `serverHello(fields)`, awaits the result.
 * @param {ReturnType<typeof createHarness>} h
 * @param {Partial<import('../../../src/session/wire.js').ServerGreeting>} [fields]
 */
export async function connected(h, fields) {
  const result = h.client.tryConnect();
  await tick();
  h.transport.serverSend(serverHello(fields));
  const r = await result;
  if (!r.success) throw new Error(`connect failed: ${JSON.stringify(r)}`);
  await tick();
  return h;
}
