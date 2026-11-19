import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { counters, isIdle } from '../../src/diagnostics/counters.js';
import { Instruments, METER_NAME, NexusMetrics, disconnectReasonTag, toSnakeCase, transportTagOf } from '../../src/metrics/index.js';
import { connected, createHarness, tick } from '../session/support/harness.js';

const metricsDoc = readFileSync(fileURLToPath(new URL('../../../../../../docs/articles/metrics.md', import.meta.url)), 'utf8');

/** @type {Array<{ h: ReturnType<typeof createHarness>, m: NexusMetrics }>} */
let cleanup = [];

afterEach(async () => {
  for (const { h, m } of cleanup) {
    m.dispose();
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
  }
  cleanup = [];
  await tick();
  expect(isIdle(), JSON.stringify(counters())).toBe(true);
});

/** @param {NexusMetrics} m */
function collect(m) {
  /** @type {Record<string, number>} */
  const out = {};
  m.collect((instrument, value, tags) => {
    const extra = tags['nexnet.direction'] ? `/${tags['nexnet.direction']}` : '';
    expect(tags['nexnet.role']).toBe('client');
    out[instrument.name + extra] = value;
  });
  return out;
}

describe('instrument catalog', () => {
  it('has exactly the instruments, kinds, units and extra tags of docs/articles/metrics.md', () => {
    /** @type {Map<string, { type: string, unit: string, tags: string }>} */
    const documented = new Map();
    for (const match of metricsDoc.matchAll(/^\| `(nexnet\.[a-z_.]+)` \| (\w+) \| `([^`]+)` \|([^|]*)\|/gm)) {
      documented.set(match[1], { type: match[2], unit: match[3], tags: match[4] });
    }
    expect(documented.size).toBeGreaterThan(15);
    const names = Object.values(Instruments).map((i) => i.name).sort();
    expect(names).toEqual([...documented.keys()].sort());
    const kinds = { Counter: ['counter', 'observableCounter'], UpDownCounter: ['observableUpDownCounter'], Histogram: ['histogram'] };
    for (const instrument of Object.values(Instruments)) {
      const doc = /** @type {{ type: string, unit: string, tags: string }} */ (documented.get(instrument.name));
      expect(kinds[/** @type {keyof typeof kinds} */ (doc.type)], instrument.name).toContain(instrument.kind);
      expect(instrument.unit, instrument.name).toBe(doc.unit);
      for (const tag of instrument.tags) expect(doc.tags, instrument.name).toContain(tag);
    }
    expect(METER_NAME).toBe('NexNet');
  });

  it('names tags like C#: snake-case disconnect reasons and transport names', () => {
    expect(toSnakeCase('SocketClosedWhenWriting')).toBe('socket_closed_when_writing');
    expect(disconnectReasonTag(DisconnectReason.FlowControl)).toBe('flow_control');
    expect(disconnectReasonTag(36)).toBe('unknown');
    expect(transportTagOf({ webSocket: { url: 'wss://x' } })).toBe('websocket');
    expect(transportTagOf({ httpSocket: { url: 'https://x' } })).toBe('httpsocket');
    expect(transportTagOf({ tcpTls: { host: 'h', port: 1 } })).toBe('tls');
    expect(transportTagOf({ uds: { path: 'p' } })).toBe('uds');
    expect(transportTagOf({})).toBe('custom');
  });
});

describe('NexusMetrics', () => {
  it('observes sessions, frames, bytes and budget, and records disconnects and durations', async () => {
    const h = createHarness();
    const m = new NexusMetrics();
    cleanup.push({ h, m });
    /** @type {Array<[string, string, number, Record<string, string>]>} */
    const pushed = [];
    const remove = m.addListener({
      add: (i, v, t) => pushed.push(['add', i.name, v, t]),
      record: (i, v, t) => pushed.push(['record', i.name, v, t]),
    });
    m.track(h.client);
    m.track(h.client);
    expect(m.trackedClients).toBe(1);
    expect(collect(m)['nexnet.session.active']).toBe(0);

    await connected(h);
    const live = collect(m);
    expect(live['nexnet.session.active']).toBe(1);
    expect(live['nexnet.session.handshaking']).toBe(0);
    expect(live['nexnet.io.frames/received']).toBe(1);
    expect(live['nexnet.io.bytes/received']).toBeGreaterThan(10);
    expect(live['nexnet.io.frames/sent']).toBeGreaterThanOrEqual(1);
    expect(live['nexnet.budget.limit']).toBe(h.client._session?.options.sessionBudget);
    expect(live['nexnet.budget.reserved']).toBeGreaterThan(0);

    h.clock.advance(2500);
    h.transport.serverClose();
    await h.client.disconnected;
    await tick();
    const after = collect(m);
    expect(after['nexnet.session.active']).toBe(0);
    expect(after['nexnet.io.bytes/received']).toBe(live['nexnet.io.bytes/received']);
    expect(pushed).toEqual([
      ['add', 'nexnet.session.disconnects', 1, { 'nexnet.role': 'client', 'nexnet.transport': 'tcp', 'nexnet.disconnect.reason': 'socket_error', 'nexnet.session.phase': 'established' }],
      ['record', 'nexnet.session.duration', 2.5, { 'nexnet.role': 'client', 'nexnet.transport': 'tcp', 'nexnet.disconnect.reason': 'socket_error' }],
    ]);

    // A second session adds to the cumulative counters; untracking keeps them.
    await connected(h);
    const second = collect(m);
    expect(second['nexnet.io.frames/received']).toBe(2);
    m.untrack(h.client);
    m.untrack(h.client);
    const untracked = collect(m);
    expect(untracked['nexnet.io.frames/received']).toBe(2);
    expect(untracked['nexnet.session.active']).toBe(0);
    remove();
    expect(NexusMetrics.shared).toBe(NexusMetrics.shared);
  });

  it('a listener that throws does not break the client', async () => {
    const h = createHarness();
    const m = new NexusMetrics();
    cleanup.push({ h, m });
    m.addListener({ add: () => { throw new Error('listener'); } });
    m.track(h.client);
    await connected(h);
    h.transport.serverClose();
    await h.client.disconnected;
    await tick();
    expect(collect(m)['nexnet.session.active']).toBe(0);
  });
});
