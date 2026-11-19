import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectionResultState, ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { NexusTransportError } from '../../src/contract/errors.js';
import { NexusClient } from '../../src/client/NexusClient.js';
import { NodeNexusClient } from '../../src/contract/node.js';
import { getSessionCounters } from '../../src/session/diagnostics.js';
import { decodeClientGreeting } from '../../src/session/wire.js';
import { LoopbackTransport } from './support/LoopbackTransport.js';
import { TestNexus, TestProxy, connected, createHarness, serverHello, tick } from './support/harness.js';

/** @type {Array<ReturnType<typeof createHarness>>} */
let harnesses = [];

/** @param {Parameters<typeof createHarness>[0]} [options] */
function harness(options) {
  const h = createHarness(options);
  harnesses.push(h);
  return h;
}

afterEach(async () => {
  for (const h of harnesses) {
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
  }
  harnesses = [];
  await tick();
  expect(getSessionCounters()).toEqual({ openSessions: 0, timers: 0, pendingWrites: 0 });
});

const binding = {
  handshake: { version: null, serverNexusHash: 1, clientNexusHash: 2, acceptedIds: new Set([0]) },
  proxy: TestProxy,
};

const warning = (/** @type {string} */ t) => `NexNet: unencrypted transport (${t}); credentials and data are visible on the network.`;

describe('config validation', () => {
  it('requires exactly one transport the entry point supports', () => {
    expect(() => NexusClient.create(/** @type {any} */ ({}), new TestNexus(), binding)).toThrow(/none is set/);
    expect(() => NexusClient.create(/** @type {any} */ ({ tcp: { host: 'h', port: 1 }, uds: { path: 'p' } }), new TestNexus(), binding))
      .toThrow(/several are set: tcp, uds/);
    expect(() => NexusClient.create({ tcp: { host: 'h', port: 1 } }, new TestNexus(), binding)).toThrow(/'tcp' is not supported/);
    expect(() => NodeNexusClient.create({ tcp: { host: 'h', port: 1 } }, new TestNexus(), binding)).not.toThrow();
    expect(() => NexusClient.create({ webSocket: { url: 'http://x' } }, new TestNexus(), binding)).toThrow(/ws: or wss:/);
    expect(() => NexusClient.create({ webSocket: { url: 'not a url' } }, new TestNexus(), binding)).toThrow(TypeError);
  });

  it('checks ranges and the session budget like C# ConfigBase', () => {
    const ws = { webSocket: { url: 'ws://x' } };
    expect(() => NexusClient.create({ ...ws, maxPipes: 0 }, new TestNexus(), binding)).toThrow(RangeError);
    expect(() => NexusClient.create({ ...ws, timeout: 10 }, new TestNexus(), binding)).toThrow(/timeout must be between 50 and 300000/);
    expect(() => NexusClient.create({ ...ws, handshakeTimeout: 1.5 }, new TestNexus(), binding)).toThrow(TypeError);
    expect(() => NexusClient.create({ ...ws, sessionBudget: 64 * 1024 }, new TestNexus(), binding)).toThrow(/sessionBudget/);
    expect(() => NexusClient.create({ ...ws, serializerOptions: { preset: 'trusted' }, sessionBudget: 16 * 1024 * 1024 }, new TestNexus(), binding))
      .toThrow(/sessionBudget/);
    expect(() => NexusClient.create(/** @type {any} */ ({ ...ws, authenticate: 'token' }), new TestNexus(), binding)).toThrow(/authenticate/);
  });

  it('validates the generated binding', () => {
    const ws = { webSocket: { url: 'ws://x' } };
    const bad = (/** @type {any} */ handshake) => () => NexusClient.create(ws, new TestNexus(), { handshake, proxy: TestProxy });
    expect(bad({ ...binding.handshake, version: 'x'.repeat(17) })).toThrow(/at most 16/);
    expect(bad({ ...binding.handshake, serverNexusHash: 2 ** 31 })).toThrow(/int32/);
    expect(bad(null)).toThrow(/handshake/);
  });
});

describe('isSecure and the plaintext warning (D7, F05)', () => {
  const cases = /** @type {const} */ ([
    ['tcp', false, 'tcp'], ['tcpTls', true, null], ['uds', true, null], ['webSocket', false, 'ws://'], ['httpSocket', false, 'http://'],
  ]);
  for (const [kind, secure, name] of cases) {
    it(`${kind}: isSecure ${secure}`, async () => {
      const h = harness({ kind });
      expect(h.client.isSecure).toBe(secure);
      await connected(h);
      await h.client.disconnect();
      await connected(h);
      const warnings = h.logs.filter(([level, m]) => level === 'warning' && String(m).startsWith('NexNet: unencrypted'));
      expect(warnings.map(([, m]) => m)).toEqual(name ? [warning(name)] : []);
    });
  }

  it('wss: and https: are secure', () => {
    expect(NexusClient.create({ webSocket: { url: 'wss://x' } }, new TestNexus(), binding).isSecure).toBe(true);
    expect(NodeNexusClient.create({ httpSocket: { url: 'https://x' } }, new TestNexus(), binding).isSecure).toBe(true);
  });

  it('warns once per client instance, on the first connect, before the transport opens', async () => {
    const h = harness();
    const pending = h.client.tryConnect();
    expect(h.logs.map(([, m]) => m)).toContain(warning('tcp'));
    expect(h.factoryCalls.length).toBe(0);
    await tick();
    h.transport.serverSend(serverHello());
    await pending;
    const other = harness();
    void other.client.tryConnect();
    expect(other.logs.map(([, m]) => m)).toContain(warning('tcp'));
  });
});

describe('authenticate (F05)', () => {
  it('runs before the transport connects, on every connect, and its token is sent in the greeting', async () => {
    /** @type {string[]} */
    const order = [];
    let n = 0;
    const h = harness({
      config: {
        authenticate: async ({ signal }) => {
          expect(signal).toBeInstanceOf(AbortSignal);
          order.push('authenticate');
          return Uint8Array.of(++n);
        },
      },
    });
    h.setFactory(async () => {
      order.push('transport');
      const t = new LoopbackTransport();
      h.transports.push(t);
      return t;
    });
    await connected(h);
    expect(decodeClientGreeting(h.transport.sent.subarray(11)).authenticationToken).toEqual(Uint8Array.of(1));
    await h.client.disconnect();
    await connected(h);
    expect(decodeClientGreeting(h.transport.sent.subarray(11)).authenticationToken).toEqual(Uint8Array.of(2));
    expect(order).toEqual(['authenticate', 'transport', 'authenticate', 'transport']);
  });

  it('a throwing provider fails with Authentication and opens no connection', async () => {
    const error = new Error('no token');
    for (const authenticate of [() => { throw error; }, async () => { throw error; }]) {
      const h = harness({ config: { authenticate } });
      expect(await h.client.tryConnect()).toEqual({
        success: false, state: ConnectionResultState.Exception, disconnectReason: DisconnectReason.Authentication, error,
      });
      expect(h.factoryCalls.length).toBe(0);
      expect(h.client.state).toBe(ConnectionState.Disconnected);
      await expect(h.client.connect()).rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.Authentication, cause: error });
    }
  });

  it('is bounded by the handshake timeout', async () => {
    /** @type {AbortSignal | null} */
    let seen = null;
    const h = harness({ config: { handshakeTimeout: 500, authenticate: ({ signal }) => { seen = signal; return new Promise(() => {}); } } });
    const result = h.client.tryConnect();
    await tick();
    h.clock.advance(500);
    const r = await result;
    expect(r.state).toBe(ConnectionResultState.Exception);
    expect(r.disconnectReason).toBe(DisconnectReason.Authentication);
    expect(r.error).toBeInstanceOf(NexusTransportError);
    expect(/** @type {AbortSignal} */ (/** @type {unknown} */ (seen)).aborted).toBe(true);
    expect(h.factoryCalls.length).toBe(0);
  });

  it('a non-Uint8Array token is an error', async () => {
    const h = harness({ config: { authenticate: () => /** @type {any} */ ('token') } });
    const r = await h.client.tryConnect();
    expect(r.disconnectReason).toBe(DisconnectReason.Authentication);
    expect(r.error).toBeInstanceOf(TypeError);
  });

  it('null or undefined sends an empty token', async () => {
    const h = harness({ config: { authenticate: () => undefined } });
    void h.client.tryConnect();
    await tick();
    expect(decodeClientGreeting(h.transport.sent.subarray(11)).authenticationToken).toEqual(new Uint8Array(0));
  });
});

describe('connect', () => {
  it('state machine and events: Connecting -> Connected -> Disconnecting -> Disconnected, never dispatched re-entrantly', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    const pending = h.client.tryConnect();
    expect(h.client.state).toBe(ConnectionState.Connecting);
    expect(h.events).toEqual([]);
    await tick();
    h.transport.serverSend(serverHello());
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.events.length).toBe(1); // only Connecting so far: Connected is dispatched in a microtask
    expect(await pending).toEqual({ success: true, state: ConnectionResultState.Success, disconnectReason: DisconnectReason.None });
    await h.client.disconnect();
    await tick();
    expect(h.events.map((e) => e.type === 'statechange' ? e.detail.state : `disconnected:${e.detail.reason}`)).toEqual([
      ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected,
      `disconnected:${DisconnectReason.Graceful}`,
    ]);
    expect(h.events[1].detail).toEqual({ state: ConnectionState.Connected, previous: ConnectionState.Connecting });
    expect(h.nexus.calls).toEqual([['connected', false], ['disconnected', DisconnectReason.Graceful]]);
  });

  it('creates the proxy on the first successful connect with a handle that follows the session', async () => {
    const h = harness();
    expect(() => h.client.proxy).toThrow(/not connected/);
    await connected(h);
    const proxy = h.client.proxy;
    expect(proxy).toBeInstanceOf(TestProxy);
    const handle = /** @type {any} */ (proxy)._session;
    expect(handle.client).toBe(h.client);
    expect(handle.session).toBe(h.client._session);
    await h.client.disconnect();
    expect(handle.session).toBeNull();
    await connected(h);
    expect(h.client.proxy).toBe(proxy);
    expect(handle.session).toBe(h.client._session);
  });

  it('concurrent tryConnect calls share one attempt; connecting while connected succeeds at once', async () => {
    const h = harness();
    const a = h.client.tryConnect();
    const b = h.client.tryConnect();
    expect(b).toBe(a);
    await tick();
    h.transport.serverSend(serverHello());
    await a;
    expect(h.factoryCalls.length).toBe(1);
    expect((await h.client.tryConnect()).success).toBe(true);
  });

  it('a reconnect sends ClientGreetingReconnection (101) and reports isReconnected', async () => {
    const h = harness();
    // @ts-expect-error _tryConnectCore is protected; J10's reconnection calls it.
    const pending = h.client._tryConnectCore(true);
    expect(h.client.state).toBe(ConnectionState.Reconnecting);
    await tick();
    expect(h.transport.sent[8]).toBe(101);
    h.transport.serverSend(serverHello());
    expect((await pending).success).toBe(true);
    await tick();
    expect(h.nexus.calls).toEqual([['connected', true]]);
  });

  it('an aborted signal before connect resolves Canceled; connect() rejects with the abort reason', async () => {
    const h = harness();
    const controller = new AbortController();
    const reason = new Error('stop');
    controller.abort(reason);
    expect((await h.client.tryConnect({ signal: controller.signal })).state).toBe(ConnectionResultState.Canceled);
    await expect(h.client.connect({ signal: controller.signal })).rejects.toBe(reason);
    expect(h.factoryCalls.length).toBe(0);
  });

  it('aborting during the handshake closes the transport without a byte and resolves Canceled', async () => {
    const h = harness();
    const controller = new AbortController();
    const pending = h.client.tryConnect({ signal: controller.signal });
    await tick();
    controller.abort();
    expect((await pending).state).toBe(ConnectionResultState.Canceled);
    expect(h.transport.closed).toBe(true);
    expect(h.transport.sent[h.transport.sent.length - 1]).not.toBe(DisconnectReason.Graceful);
    expect(h.client.state).toBe(ConnectionState.Disconnected);
  });

  it('the transport factory gets a signal that aborts at connectionTimeout', async () => {
    const h = harness({ config: { connectionTimeout: 300 } });
    h.setFactory((_c, { signal }) => new Promise((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(signal.reason));
    }));
    const pending = h.client.tryConnect();
    await tick();
    h.clock.advance(300);
    const r = await pending;
    expect(r).toMatchObject({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.Timeout });
    expect(r.error).toBeInstanceOf(NexusTransportError);
  });

  it('maps factory errors: NexusTransportError -> Disconnected with its reason; others -> Exception/SocketError', async () => {
    const h = harness();
    const refused = new NexusTransportError('refused', { reason: DisconnectReason.SocketError });
    h.setFactory(async () => { throw refused; });
    expect(await h.client.tryConnect()).toEqual({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.SocketError, error: refused });
    const boom = new Error('boom');
    h.setFactory(async () => { throw boom; });
    expect(await h.client.tryConnect()).toEqual({ success: false, state: ConnectionResultState.Exception, disconnectReason: DisconnectReason.SocketError, error: boom });
    await expect(h.client.connect()).rejects.toMatchObject({ name: 'NexusTransportError', cause: boom });
  });

  it('disconnected resolves when the session ends; nexus hook errors are logged, not thrown', async () => {
    const h = harness();
    h.nexus.onDisconnected = () => { throw new Error('hook failed'); };
    await connected(h);
    const ended = h.client.disconnected;
    h.transport.serverClose();
    await ended;
    await tick();
    expect(h.logs.some(([level, m]) => level === 'error' && String(m).includes('onDisconnected'))).toBe(true);
  });

  it('Symbol.asyncDispose disconnects', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    await connected(h);
    await h.client[Symbol.asyncDispose]();
    expect(h.client.state).toBe(ConnectionState.Disconnected);
  });

  it('getSnapshot reports the session while connected and null otherwise', async () => {
    const h = harness();
    expect(h.client.getSnapshot()).toBeNull();
    await connected(h, { clientId: 99n, idleTimeoutMs: 9000 });
    expect(h.client.getSnapshot()).toMatchObject({ id: 99n, state: ConnectionState.Connected, isSecure: false, idleTimeoutMs: 9000, pingInterval: 3000 });
  });

  it('remoteCertificate is the transport certificate while connected and null otherwise', async () => {
    const cert = { subject: 'CN=localhost' };
    const h = harness({ kind: 'tcpTls', transport: { isSecure: true } });
    h.setFactory(async () => {
      const t = new LoopbackTransport({ isSecure: true, name: 'tls' });
      t.remoteCertificate = /** @type {any} */ (cert);
      h.transports.push(t);
      return t;
    });
    expect(h.client.remoteCertificate).toBeNull();
    await connected(h);
    expect(h.client.remoteCertificate).toBe(cert);
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
    expect(h.client.remoteCertificate).toBeNull();
  });

  it('remoteCertificate is null on a transport without one', async () => {
    const h = harness();
    await connected(h);
    expect(h.client.remoteCertificate).toBeNull();
  });

  it('session counters return to zero after the client disconnects', async () => {
    const h = harness({ config: { disconnectDelay: 100 } });
    await connected(h);
    expect(getSessionCounters().openSessions).toBe(1);
    expect(getSessionCounters().timers).toBe(1); // keepalive
    const done = h.client.disconnect();
    await tick();
    expect(getSessionCounters().timers).toBe(1); // disconnect delay
    h.clock.advance(100);
    await done;
    expect(getSessionCounters()).toEqual({ openSessions: 0, timers: 0, pendingWrites: 0 });
  });
});

describe('system clock interval timer', () => {
  it('uses a dedicated Worker when the platform has one, and setInterval otherwise', async () => {
    const { startIntervalTimer, systemClock } = await import('../../src/session/clock.js');
    let ticks = 0;
    const plain = startIntervalTimer(systemClock, 5, () => ticks++);
    await new Promise((r) => setTimeout(r, 30));
    plain.stop();
    expect(ticks).toBeGreaterThan(0);

    /** @type {any[]} */
    const workers = [];
    class FakeWorker {
      /** @type {((e: unknown) => void) | null} */
      onmessage = null;
      terminated = false;
      /** @param {string} url */
      constructor(url) { this.url = url; workers.push(this); }
      /** @param {number} ms */
      postMessage(ms) { this.ms = ms; }
      terminate() { this.terminated = true; }
    }
    vi.stubGlobal('Worker', FakeWorker);
    const revoke = vi.spyOn(URL, 'revokeObjectURL');
    try {
      let workerTicks = 0;
      const timer = startIntervalTimer(systemClock, 1000, () => workerTicks++);
      expect(workers.length).toBe(1);
      expect(workers[0].ms).toBe(1000);
      workers[0].onmessage?.({});
      expect(workerTicks).toBe(1);
      timer.stop();
      expect(workers[0].terminated).toBe(true);
      expect(revoke).toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
      revoke.mockRestore();
    }
  });
});
