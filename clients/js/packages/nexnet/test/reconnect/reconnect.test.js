import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { counters, isIdle } from '../../src/diagnostics/counters.js';
import {
  DEFAULT_RECONNECT_DELAYS,
  DefaultReconnectionPolicy,
  ReconnectController,
  attachBrowserLifecycle,
  defaultShouldReconnect,
  resolveReconnectionPolicy,
} from '../../src/reconnect/index.js';
import { createHarness, serverHello, tick } from '../session/support/harness.js';
import { connected } from '../session/support/harness.js';

/** @type {Array<{ h: ReturnType<typeof createHarness>, c: ReconnectController | null }>} */
let cleanup = [];

/**
 * @param {Parameters<typeof createHarness>[0]} [options]
 * @returns {ReturnType<typeof createHarness>}
 */
function harness(options) {
  const h = createHarness(options);
  cleanup.push({ h, c: null });
  return h;
}

/**
 * @param {ReturnType<typeof createHarness>} h
 * @param {ConstructorParameters<typeof ReconnectController>[1]} [options]
 */
function control(h, options) {
  // @ts-expect-error _tryConnectCore is protected; reconnection is its caller.
  const c = new ReconnectController(h.client, options);
  const entry = cleanup.find((e) => e.h === h);
  if (entry) entry.c = c;
  return c;
}

/** Collects the controller's events. @param {ReconnectController} c */
function eventsOf(c) {
  /** @type {Array<[string, any]>} */
  const events = [];
  for (const type of ['reconnecting', 'reconnectattempt', 'reconnected', 'reconnectfailed']) {
    c.addEventListener(type, (e) => events.push([type, /** @type {CustomEvent} */ (e).detail]));
  }
  return events;
}

afterEach(async () => {
  for (const { h, c } of cleanup) {
    await c?.stop();
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
  }
  cleanup = [];
  await tick();
  expect(counters()).toMatchObject({ sessions: 0, timers: 0, pendingInvocations: 0, subscriptions: 0 });
  expect(isIdle()).toBe(true);
});

describe('DefaultReconnectionPolicy', () => {
  it('waits 0, 2, 10 and 30 s, then 30 s forever (C# DefaultReconnectionPolicy)', () => {
    const p = new DefaultReconnectionPolicy();
    expect(DEFAULT_RECONNECT_DELAYS).toEqual([0, 2000, 10000, 30000]);
    expect([0, 1, 2, 3, 4, 100].map((n) => p.reconnectDelay(n))).toEqual([0, 2000, 10000, 30000, 30000, 30000]);
    const once = new DefaultReconnectionPolicy({ delays: [5], continuousRetry: false });
    expect([once.reconnectDelay(0), once.reconnectDelay(1)]).toEqual([5, null]);
    expect(() => new DefaultReconnectionPolicy({ delays: [] })).toThrow(TypeError);
    expect(() => new DefaultReconnectionPolicy({ delays: [-1] })).toThrow(RangeError);
  });

  it('reconnects for SocketError, Timeout and ServerRestarting only, unless told otherwise', () => {
    const p = new DefaultReconnectionPolicy();
    /** @type {number[]} */
    const yes = [DisconnectReason.SocketError, DisconnectReason.Timeout, DisconnectReason.ServerRestarting];
    for (const reason of Object.values(DisconnectReason)) {
      expect(p.shouldReconnect(reason), String(reason)).toBe(yes.includes(reason));
      expect(defaultShouldReconnect(reason)).toBe(yes.includes(reason));
    }
    const withShutdown = new DefaultReconnectionPolicy({ reconnectReasons: [...yes, DisconnectReason.ServerShutdown] });
    expect(withShutdown.shouldReconnect(DisconnectReason.ServerShutdown)).toBe(true);
  });

  it('is on by default in browsers and off in Node; an explicit null turns it off', () => {
    const ws = { webSocket: { url: 'ws://x' } };
    expect(resolveReconnectionPolicy(ws, false)).toBeNull();
    expect(resolveReconnectionPolicy(ws, true)).toBeInstanceOf(DefaultReconnectionPolicy);
    expect(resolveReconnectionPolicy({ ...ws, reconnectionPolicy: null }, true)).toBeNull();
    const custom = { reconnectDelay: () => null };
    expect(resolveReconnectionPolicy({ ...ws, reconnectionPolicy: custom }, false)).toBe(custom);
    expect(resolveReconnectionPolicy(ws)).toBeNull(); // Node
  });
});

describe('ReconnectController', () => {
  it('reconnects a lost session with the 101 greeting, calling onReconnecting then onConnected(true)', async () => {
    const h = await connected(harness());
    /** @type {string[]} */
    const hooks = [];
    h.nexus.onReconnecting = () => { hooks.push('reconnecting'); };
    const c = control(h);
    const events = eventsOf(c);

    h.transport.serverClose();
    await tick();
    expect(c.isReconnecting).toBe(true);
    expect(hooks).toEqual(['reconnecting']);
    // First delay is 0.
    h.clock.advance(0);
    await tick();
    expect(h.transports.length).toBe(2);
    expect(h.transport.sent[8]).toBe(101);
    expect(h.client.state).toBe(ConnectionState.Reconnecting);
    h.transport.serverSend(serverHello());
    await c.idle;
    await tick();
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.nexus.calls).toEqual([['connected', false], ['disconnected', DisconnectReason.SocketError], ['connected', true]]);
    expect(events.map(([t]) => t)).toEqual(['reconnecting', 'reconnectattempt', 'reconnected']);
    expect(events[0][1]).toEqual({ reason: DisconnectReason.SocketError });
    expect(c.isReconnecting).toBe(false);
    expect(h.logs.some(([level, m]) => level === 'information' && m === 'Reconnected')).toBe(true);
  });

  it('waits the policy delays between failed attempts, and starts over after a later loss', async () => {
    const h = await connected(harness());
    const c = control(h, { policy: new DefaultReconnectionPolicy({ delays: [0, 2000, 10000] }) });
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    expect(h.transports.length).toBe(2);
    h.transport.serverClose(); // attempt 1 fails before the handshake
    await tick();
    expect(c.isWaiting).toBe(true);
    h.clock.advance(1999);
    await tick();
    expect(h.transports.length).toBe(2);
    h.clock.advance(1);
    await tick();
    expect(h.transports.length).toBe(3);
    h.transport.serverSend(serverHello());
    await c.idle;
    expect(c.attempts).toBe(2);

    await tick();
    h.transport.serverClose();
    await tick();
    expect(c.isReconnecting).toBe(true);
    h.clock.advance(0);
    await tick();
    expect(h.transports.length).toBe(4);
    h.transport.serverSend(serverHello());
    await c.idle;
    expect(c.attempts).toBe(1);
  });

  it('does not reconnect after a graceful disconnect, or for a reason the policy declines', async () => {
    const h = await connected(harness());
    const c = control(h);
    const events = eventsOf(c);
    const done = h.client.disconnect();
    h.clock.advance(1000);
    await done;
    await tick();
    expect(events).toEqual([]);
    expect(h.logs.some(([, m]) => String(m).includes("Can't reconnect with Graceful"))).toBe(true);
    expect(c.wouldReconnect(DisconnectReason.Graceful)).toBe(false);
    expect(c.wouldReconnect(DisconnectReason.Timeout)).toBe(true);
    await c.stop();
    expect(c.wouldReconnect(DisconnectReason.Timeout)).toBe(false);
  });

  it('gives up when the policy returns null', async () => {
    const h = await connected(harness());
    const c = control(h, { policy: { reconnectDelay: (n) => (n < 1 ? 0 : null) } });
    const events = eventsOf(c);
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    h.transport.serverClose();
    await c.idle;
    expect(events.map(([t]) => t)).toEqual(['reconnecting', 'reconnectattempt', 'reconnectfailed']);
    expect(events[2][1]).toEqual({ reason: DisconnectReason.SocketError, attempts: 1 });
    expect(h.client.state).toBe(ConnectionState.Disconnected);
  });

  it('stop() cancels a pending delay and stops listening', async () => {
    const h = await connected(harness());
    const c = control(h, { policy: new DefaultReconnectionPolicy({ delays: [5000] }) });
    h.transport.serverClose();
    await tick();
    expect(c.isWaiting).toBe(true);
    expect(counters().timers).toBeGreaterThan(0);
    await c.stop();
    expect(c.stopped).toBe(true);
    expect(c.isReconnecting).toBe(false);
    h.clock.advance(10_000);
    await tick();
    expect(h.transports.length).toBe(1);
  });

  it('logs and survives an onReconnecting that throws', async () => {
    const h = await connected(harness());
    h.nexus.onReconnecting = () => { throw new Error('hook'); };
    const c = control(h);
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    h.transport.serverSend(serverHello());
    await c.idle;
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.logs.some(([level, m]) => level === 'error' && m === 'onReconnecting threw.')).toBe(true);
  });
});

describe('browser lifecycle triggers', () => {
  it('visibilitychange (visible), online and resume end a pending delay at once', async () => {
    const h = await connected(harness());
    const doc = Object.assign(new EventTarget(), { visibilityState: 'hidden' });
    const win = new EventTarget();
    const c = control(h, { policy: new DefaultReconnectionPolicy({ delays: [60_000] }), lifecycle: { document: doc, window: win } });
    expect(counters().subscriptions).toBe(1 + 4);

    h.transport.serverClose();
    await tick();
    doc.dispatchEvent(new Event('visibilitychange'));
    await tick();
    expect(h.transports.length).toBe(1);
    doc.visibilityState = 'visible';
    doc.dispatchEvent(new Event('visibilitychange'));
    await tick();
    expect(h.transports.length).toBe(2);
    h.transport.serverClose();
    await tick();
    win.dispatchEvent(new Event('online'));
    await tick();
    expect(h.transports.length).toBe(3);
    h.transport.serverClose();
    await tick();
    doc.dispatchEvent(new Event('freeze'));
    doc.dispatchEvent(new Event('resume'));
    await tick();
    expect(h.transports.length).toBe(4);
    h.transport.serverSend(serverHello());
    await c.idle;
    expect(c.retryNow()).toBe(false);
    await c.stop();
    expect(counters().subscriptions).toBe(0);
  });

  it('is a no-op without a document or window', () => {
    const detach = attachBrowserLifecycle({ retryNow: () => false }, { document: null, window: null });
    expect(counters().subscriptions).toBe(0);
    detach();
    detach();
  });
});
