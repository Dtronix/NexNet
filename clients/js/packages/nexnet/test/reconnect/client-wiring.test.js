import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { counters, isIdle } from '../../src/diagnostics/counters.js';
import { NexusTransportError } from '../../src/contract/errors.js';
import { DefaultReconnectionPolicy } from '../../src/reconnect/index.js';
import { connected, createHarness, serverHello, tick } from '../session/support/harness.js';

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
  expect(isIdle(), JSON.stringify(counters())).toBe(true);
});

describe('NexusClient reconnection', () => {
  it('is off in Node unless configured', async () => {
    const h = await connected(harness());
    expect(h.client.reconnection).toBeNull();
    h.transport.serverClose();
    await h.client.disconnected;
    await tick();
    h.clock.advance(60_000);
    await tick();
    expect(h.transports.length).toBe(1);
  });

  it('reconnects a lost session; disconnected stays pending until disconnect()', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy() } }));
    const reconnection = /** @type {NonNullable<typeof h.client.reconnection>} */ (h.client.reconnection);
    expect(reconnection).not.toBeNull();
    let ended = false;
    void h.client.disconnected.then(() => { ended = true; });

    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    expect(h.transport.sent[8]).toBe(101);
    // A user connect during the attempt joins it.
    const joined = h.client.tryConnect();
    h.transport.serverSend(serverHello());
    expect((await joined).success).toBe(true);
    await reconnection.idle;
    await tick();
    expect(h.transports.length).toBe(2);
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.nexus.calls.at(-1)).toEqual(['connected', true]);
    expect(ended).toBe(false);

    const done = h.client.disconnect();
    h.clock.advance(1000);
    await done;
    expect(ended).toBe(true);
    expect(reconnection.stopped).toBe(true);
    expect(h.client.reconnection).toBeNull();
  });

  it('disconnected resolves when the policy declines or gives up', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [0], continuousRetry: false }) } }));
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    h.transport.serverClose(); // the single attempt fails
    await h.client.disconnected;
    expect(h.client.state).toBe(ConnectionState.Disconnected);

    const g = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy() } }));
    g.transport.serverSend(Uint8Array.of(DisconnectReason.Graceful));
    await g.client.disconnected;
    expect(g.transports.length).toBe(1);
  });

  it('stays Reconnecting between failed attempts, then Connected (C# parity)', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [0, 100], continuousRetry: true }) } }));
    const refused = new NexusTransportError('refused', { reason: DisconnectReason.SocketError });
    h.setFactory(async () => { throw refused; });
    h.events.length = 0;

    h.transport.serverClose();
    await tick();
    expect(h.client.state).toBe(ConnectionState.Reconnecting);
    // Three refused attempts; the client reads Reconnecting during each delay.
    for (let i = 0; i < 3; i++) {
      h.clock.advance(100);
      await tick();
      expect(h.client.state).toBe(ConnectionState.Reconnecting);
    }
    expect(h.factoryCalls.length).toBeGreaterThanOrEqual(4);

    h.setFactory(/** @type {any} */ (null));
    h.clock.advance(100);
    await tick();
    h.transport.serverSend(serverHello());
    await tick();
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.events.filter((e) => e.type === 'statechange').map((e) => e.detail.state)).toEqual([
      ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Reconnecting, ConnectionState.Connected,
    ]);
  });

  it('ends Disconnected when the policy gives up, and when disconnect() stops the loop', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [0, 50], continuousRetry: false }) } }));
    h.setFactory(async () => { throw new NexusTransportError('refused', { reason: DisconnectReason.SocketError }); });
    h.events.length = 0;
    h.transport.serverClose();
    await tick();
    for (let i = 0; i < 3; i++) {
      h.clock.advance(50);
      await tick();
    }
    await h.client.disconnected;
    // Disconnected before `disconnected` resolves.
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    expect(h.factoryCalls.length).toBe(3);
    await tick();
    expect(h.events.filter((e) => e.type === 'statechange').map((e) => e.detail.state)).toEqual([
      ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Reconnecting, ConnectionState.Disconnected,
    ]);

    const d = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [1000], continuousRetry: true }) } }));
    d.transport.serverClose();
    await tick();
    expect(d.client.state).toBe(ConnectionState.Reconnecting);
    await d.client.disconnect();
    expect(d.client.state).toBe(ConnectionState.Disconnected);
  });

  it('disconnect() during an attempt\'s handshake aborts the attempt: no session, no onConnected (J42)', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [0], continuousRetry: true }) } }));
    const reconnection = /** @type {NonNullable<typeof h.client.reconnection>} */ (h.client.reconnection);
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    // The attempt sent its reconnection greeting; the server has not answered.
    expect(h.transports.length).toBe(2);
    expect(h.transport.sent[8]).toBe(101);
    h.nexus.calls.length = 0;
    h.events.length = 0;

    // disconnect() returns without the server answering the attempt.
    await h.client.disconnect();
    expect(reconnection.stopped).toBe(true);
    expect(reconnection.isReconnecting).toBe(false);
    expect(h.client._session).toBeNull();
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    // A late greeting opens nothing.
    h.transport.serverSend(serverHello());
    h.clock.advance(60_000);
    await tick();
    expect(h.transports.length).toBe(2);
    expect(h.nexus.calls).toEqual([]);
    expect(h.events.filter((e) => e.type === 'disconnected')).toEqual([]);
    expect(h.logs.filter(([level]) => level === 'error')).toEqual([]);
  });

  it('disconnect() right after an attempt\'s greeting arrives still aborts the attempt (J42)', async () => {
    const h = await connected(harness({ config: { reconnectionPolicy: new DefaultReconnectionPolicy({ delays: [0], continuousRetry: true }) } }));
    h.transport.serverClose();
    await tick();
    h.clock.advance(0);
    await tick();
    h.nexus.calls.length = 0;
    h.events.length = 0;

    // The greeting completes the handshake; disconnect() runs before the attempt's continuation.
    h.transport.serverSend(serverHello());
    const done = h.client.disconnect();
    h.clock.advance(1000);
    await done;
    h.clock.advance(60_000);
    await tick();
    expect(h.client._session).toBeNull();
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    expect(h.transports.length).toBe(2);
    expect(h.nexus.calls).toEqual([]);
    expect(h.events.filter((e) => e.type === 'disconnected')).toEqual([]);
  });

  it('getSnapshot has the C# field names', async () => {
    const h = await connected(harness());
    expect(Object.keys(/** @type {object} */ (h.client.getSnapshot()))).toContain('isEstablished');
  });
});
