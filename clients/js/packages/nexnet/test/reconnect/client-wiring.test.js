import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { counters, isIdle } from '../../src/diagnostics/counters.js';
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

  it('getSnapshot has the C# field names', async () => {
    const h = await connected(harness());
    expect(Object.keys(/** @type {object} */ (h.client.getSnapshot()))).toContain('isEstablished');
  });
});
