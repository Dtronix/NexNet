import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionState } from '../../src/contract/enums.js';
import { NexusTransportError } from '../../src/contract/errors.js';
import { NexusClient } from '../../src/client/NexusClient.js';
import { counters, isIdle } from '../../src/diagnostics/counters.js';
import { NexusClientPool, NexusClientPoolConnectionError } from '../../src/pool/index.js';
import { LoopbackTransport } from '../session/support/LoopbackTransport.js';
import { ManualClock } from '../session/support/ManualClock.js';
import { CLIENT_HASH, SERVER_HASH, TestNexus, TestProxy, serverHello, tick } from '../session/support/harness.js';

/** A fake server: every connect gets a transport that answers the handshake, unless `failing` is set. */
function createServer() {
  const server = {
    /** @type {LoopbackTransport[]} */
    transports: [],
    failing: false,
    /** @type {import('../../src/transport/ITransport.js').TransportFactory} */
    factory: async () => {
      if (server.failing) throw new NexusTransportError('refused', { reason: 20 });
      const t = new LoopbackTransport();
      server.transports.push(t);
      t.serverSend(serverHello());
      return t;
    },
  };
  return server;
}

const binding = {
  handshake: { version: null, serverNexusHash: SERVER_HASH, clientNexusHash: CLIENT_HASH, acceptedIds: new Set([0]) },
  proxy: TestProxy,
};

/** @type {Array<NexusClientPool<any, any>>} */
let pools = [];
/** @type {ManualClock[]} */
let clocks = [];

/**
 * @param {Partial<import('../../src/contract/pool.js').NexusClientPoolConfig>} [options]
 */
function createPool(options = {}) {
  const server = createServer();
  const clock = new ManualClock();
  /** @extends {NexusClient<any, any>} */
  class PoolTestClient extends NexusClient {
    static transports = Object.freeze({ tcp: server.factory });
  }
  /** @type {any[]} */
  const nexuses = [];
  const pool = new NexusClientPool(
    /** @type {any} */ ({ clientConfig: { tcp: { host: 'h', port: 1 }, clock, disconnectDelay: 0 }, clientClass: PoolTestClient, ...options }),
    () => {
      const n = new TestNexus();
      nexuses.push(n);
      return n;
    },
    binding,
  );
  pools.push(pool);
  clocks.push(clock);
  return { pool, server, clock, nexuses };
}

afterEach(async () => {
  for (const pool of pools) await pool[Symbol.asyncDispose]();
  for (const clock of clocks) clock.advance(60_000);
  pools = [];
  clocks = [];
  await tick();
  expect(isIdle(), JSON.stringify(counters())).toBe(true);
});

describe('NexusClientPool', () => {
  it('validates its configuration like C#', () => {
    const clientConfig = { tcp: { host: 'h', port: 1 } };
    expect(() => new NexusClientPool({ clientConfig, maxConnections: 0 }, () => new TestNexus(), binding)).toThrow(RangeError);
    expect(() => new NexusClientPool({ clientConfig, minIdleConnections: -1 }, () => new TestNexus(), binding)).toThrow(RangeError);
    expect(() => new NexusClientPool(/** @type {any} */ ({}), () => new TestNexus(), binding)).toThrow(TypeError);
    const { pool } = createPool();
    expect(pool.maxConnections).toBe(10);
    expect(pool.availableConnections).toBe(0);
  });

  it('connects a client on first rent, and reuses it after release', async () => {
    const { pool, server } = createPool();
    const a = await pool.rent();
    expect(a.state).toBe(ConnectionState.Connected);
    expect(a.proxy).toBeInstanceOf(TestProxy);
    const proxy = a.proxy;
    a.release();
    a.release(); // idempotent
    expect(() => a.proxy).toThrow(/released/);
    expect(a.state).toBe(ConnectionState.Disconnected);
    expect(await a.ensureConnected()).toBe(false);
    expect(pool.availableConnections).toBe(1);

    const b = await pool.rent();
    expect(b.proxy).toBe(proxy);
    expect(server.transports.length).toBe(1);
    expect(pool.availableConnections).toBe(0);
    b[Symbol.dispose]();
    expect(pool.availableConnections).toBe(1);
  });

  it('waits in FIFO order while maxConnections clients are rented; a waiter can be aborted', async () => {
    const { pool } = createPool({ maxConnections: 2 });
    const a = await pool.rent();
    const b = await pool.rent();
    /** @type {string[]} */
    const order = [];
    const controller = new AbortController();
    const aborted = pool.rent({ signal: controller.signal });
    const c = pool.rent().then((r) => { order.push('c'); return r; });
    const d = pool.rent().then((r) => { order.push('d'); return r; });
    await tick();
    expect(order).toEqual([]);
    controller.abort(new Error('give up'));
    await expect(aborted).rejects.toThrow('give up');
    a.release();
    await tick();
    expect(order).toEqual(['c']);
    b.release();
    const [rc, rd] = await Promise.all([c, d]);
    expect(order).toEqual(['c', 'd']);
    rc.release();
    rd.release();
    await expect(pool.rent({ signal: AbortSignal.abort(new Error('early')) })).rejects.toThrow('early');
  });

  it('rejects with NexusClientPoolConnectionError when a new client fails to connect, and frees the slot', async () => {
    const { pool, server } = createPool({ maxConnections: 1 });
    server.failing = true;
    const error = await pool.rent().catch((e) => e);
    expect(error).toBeInstanceOf(NexusClientPoolConnectionError);
    expect(error.result.success).toBe(false);
    server.failing = false;
    const r = await pool.rent();
    r.release();
  });

  it('drops a client that is unhealthy when returned or rented, and ensureConnected reconnects', async () => {
    const { pool, server } = createPool();
    const a = await pool.rent();
    server.transports[0].serverClose();
    await tick();
    expect(a.state).toBe(ConnectionState.Disconnected);
    expect(await a.ensureConnected()).toBe(true);
    expect(server.transports.length).toBe(2);
    server.transports[1].serverClose();
    await tick();
    a.release();
    expect(pool.availableConnections).toBe(0);

    const b = await pool.rent();
    b.release();
    server.transports[2].serverClose();
    await tick();
    const c = await pool.rent();
    expect(server.transports.length).toBe(4);
    c.release();
  });

  it('health check evicts idle clients beyond minIdleConnections after maxIdleTime, and unhealthy ones', async () => {
    const { pool, server, clock } = createPool({ maxIdleTime: 1000, minIdleConnections: 1 });
    const rentals = await Promise.all([pool.rent(), pool.rent(), pool.rent()]);
    for (const r of rentals) r.release();
    expect(pool.availableConnections).toBe(3);
    clock.advance(500);
    expect(pool.availableConnections).toBe(3);
    clock.advance(600);
    await tick();
    expect(pool.availableConnections).toBe(1);
    server.transports.find((t) => !t.closed)?.serverClose();
    await tick();
    clock.advance(250);
    expect(pool.availableConnections).toBe(0);
  });

  it('collectionConnector rents a client per collection and returns them on release', async () => {
    const { pool } = createPool();
    const connector = pool.collectionConnector((proxy) => /** @type {any} */ ({ proxy }));
    const list = /** @type {any} */ (await connector.getCollection());
    expect(list.proxy).toBeInstanceOf(TestProxy);
    expect(pool.availableConnections).toBe(0);
    connector[Symbol.dispose]();
    expect(pool.availableConnections).toBe(1);
  });

  it('dispose disconnects idle clients, rejects waiters and later rents; released clients are disconnected', async () => {
    const { pool } = createPool({ maxConnections: 1 });
    const a = await pool.rent();
    const waiting = pool.rent();
    await pool[Symbol.asyncDispose]();
    await expect(waiting).rejects.toThrow(/disposed/);
    await expect(pool.rent()).rejects.toThrow(/disposed/);
    expect(a.state).toBe(ConnectionState.Connected);
    a.release();
    await tick();
    expect(counters().detail.ops.pooledClients).toBe(0);
    await pool[Symbol.asyncDispose]();
  });
});
