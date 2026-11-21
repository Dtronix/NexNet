import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { ConnectionState } from '../../src/contract/enums.js';
import { SnapshotFields, counters, diagnostics, getClientSnapshot, getInvocationCounters, isIdle, registerCounterSource } from '../../src/diagnostics/index.js';
import { connected, createHarness, tick } from '../session/support/harness.js';

const golden = JSON.parse(readFileSync(fileURLToPath(new URL('../../../../../../tests/golden/snapshot-fields.json', import.meta.url)), 'utf8'));

/** @type {Array<ReturnType<typeof createHarness>>} */
let harnesses = [];

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

describe('snapshot fields', () => {
  it('match the C# records exported to tests/golden/snapshot-fields.json', () => {
    expect(Object.keys(SnapshotFields).sort()).toEqual(Object.keys(golden.records).sort());
    for (const [record, fields] of Object.entries(golden.records)) {
      expect(SnapshotFields[/** @type {keyof typeof SnapshotFields} */ (record)], record).toEqual(fields);
    }
  });

  it('getClientSnapshot returns exactly those fields; null when not connected', async () => {
    const h = createHarness();
    harnesses.push(h);
    expect(getClientSnapshot(h.client)).toBeNull();
    await connected(h);
    h.clock.advance(1500);
    const snapshot = /** @type {import('../../src/diagnostics/snapshot.js').NexusSessionSnapshot} */ (getClientSnapshot(h.client));
    expect(Object.keys(snapshot)).toEqual(golden.records.NexusSessionSnapshot);
    expect(Object.keys(snapshot.budget)).toEqual(golden.records.NexusBudgetSnapshot);
    expect(Object.keys(snapshot.invocations)).toEqual(golden.records.NexusInvocationSnapshot);
    expect(snapshot).toMatchObject({
      id: 7n,
      isServer: false,
      transport: 'tcp',
      endpoint: null,
      isSecure: false,
      pingInterval: 10_000,
      state: ConnectionState.Connected,
      isEstablished: true,
      sinceLastReceived: 1500,
      framesReceived: 1,
      pipes: [],
      largeMessages: [],
    });
    const options = /** @type {NonNullable<typeof h.client._session>} */ (h.client._session).options;
    expect(snapshot.budget.limit).toBe(options.sessionBudget);
    expect(snapshot.invocations.maxExecuting).toBe(options.maxConcurrentConnectionInvocations);
    expect(snapshot.invocations.separatePoolMaxExecuting).toBe(options.maxSeparatePoolInvocations);
    expect(snapshot.invocations.outboundCredits).toBe(32);
  });
});

describe('leak-audit counters', () => {
  it('report sessions, pipes, timers, pending invocations and subscriptions', async () => {
    expect(counters()).toMatchObject({ sessions: 0, pipes: 0, timers: 0, pendingInvocations: 0, subscriptions: 0 });
    expect(diagnostics.counters).toBe(counters);
    expect(counters().detail.invocation).toEqual(getInvocationCounters());

    const h = createHarness();
    harnesses.push(h);
    await connected(h);
    const c = counters();
    expect(c.sessions).toBe(1);
    expect(c.timers).toBeGreaterThan(0);
    expect(isIdle(c)).toBe(false);

    const unregister = registerCounterSource(() => ({ pipes: 2, timers: 1, subscriptions: 3 }));
    expect(counters()).toMatchObject({ pipes: 2, timers: c.timers + 1, subscriptions: 3 });
    unregister();
    expect(counters().pipes).toBe(0);
  });
});
