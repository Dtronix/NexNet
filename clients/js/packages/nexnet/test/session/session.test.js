import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectionResultState, ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { getSessionCounters } from '../../src/session/diagnostics.js';
import { encodeFrame, encodeHeaderOnly, encodePreamble } from '../../src/session/frames.js';
import { MessageType } from '../../src/session/protocol.js';
import { DEFAULT_PING_INTERVAL, MIN_DERIVED_PING_INTERVAL, effectivePingInterval, resolveClientOptions } from '../../src/session/options.js';
import { decodeInvocationCancellation, decodeInvocationCredit, decodeMessageAbort } from '../../src/session/wire.js';
import { concat } from './support/LoopbackTransport.js';
import { CLIENT_HASH, connected, createHarness, serverHello, tick } from './support/harness.js';

/** @type {Array<ReturnType<typeof createHarness>>} */
let harnesses = [];

/** @param {Parameters<typeof createHarness>[0]} [options] */
function harness(options) {
  const h = createHarness(options);
  harnesses.push(h);
  return h;
}

/**
 * Fakes the global timers, which the disconnect delay and the disconnect byte wait run on whatever the client clock
 * (J26). The harness `tick` needs real timers: use {@link flush} after this.
 */
function fakeRealTime() {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
}

/**
 * Moves the faked global timers by `ms`, letting promise continuations run before and after.
 * @param {number} [ms]
 */
async function flush(ms = 0) {
  for (let i = 0; i < 5; i++) await vi.advanceTimersByTimeAsync(0);
  if (ms > 0) await vi.advanceTimersByTimeAsync(ms);
  for (let i = 0; i < 5; i++) await vi.advanceTimersByTimeAsync(0);
}

afterEach(async () => {
  vi.useRealTimers();
  for (const h of harnesses) {
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
  }
  harnesses = [];
  await tick();
  expect(getSessionCounters()).toEqual({ openSessions: 0, timers: 0, pendingWrites: 0 });
});

/** Bytes the client wrote after the preamble and greeting. @param {ReturnType<typeof createHarness>} h */
function afterHello(h) {
  const sent = h.transport.sent;
  return [...sent.subarray(8 + 3 + (sent[9] | (sent[10] << 8)))];
}

/**
 * Starts a connect and answers with a ServerGreeting body; returns the connect result.
 * @param {ReturnType<typeof createHarness>} h
 * @param {Uint8Array} bytes
 */
async function answer(h, bytes) {
  const result = h.client.tryConnect();
  await tick();
  h.transport.serverSend(bytes);
  return result;
}

describe('ServerGreeting validation (§5.2)', () => {
  it('a different client nexus hash disconnects with ClientMismatch and sends the byte', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    const r = await answer(h, serverHello({ version: CLIENT_HASH + 1 }));
    expect(r.disconnectReason).toBe(DisconnectReason.ClientMismatch);
    expect(afterHello(h)).toEqual([DisconnectReason.ClientMismatch]);
  });

  it('limits below one frame or zero credits are a ProtocolError', async () => {
    for (const fields of [{ maxMessageSize: 65525 }, { invocationCredits: 0 }, { invocationCredits: -1 }]) {
      const h = harness({ config: { disconnectDelay: 0 } });
      const r = await answer(h, serverHello(fields));
      expect(r.disconnectReason, JSON.stringify(fields)).toBe(DisconnectReason.ProtocolError);
      expect(afterHello(h)).toEqual([DisconnectReason.ProtocolError]);
    }
  });

  it('accepts the smallest valid limits and applies the smaller maxMessageSize for sending', async () => {
    const h = harness();
    await connected(h, { maxMessageSize: 65526, invocationCredits: 1 });
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    expect(s.peerMaxMessageSize).toBe(65526);
    expect(s.peerInvocationCredits).toBe(1);
    expect(s.sendMaxMessageSize).toBe(65526);
  });

  it('a body that does not decode is a ProtocolError', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    const r = await answer(h, concat([encodePreamble(), encodeFrame(MessageType.ServerGreeting, Uint8Array.of(0x93, 1, 2, 3))]));
    expect(r.disconnectReason).toBe(DisconnectReason.ProtocolError);
  });

  it('a second ServerGreeting is a ProtocolError', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    await connected(h);
    h.transport.serverSend(serverHello().subarray(8));
    await h.client.disconnected;
    expect(h.client._session).toBeNull();
    expect(h.events.at(-1)).toEqual({ type: 'disconnected', detail: { reason: DisconnectReason.ProtocolError } });
    expect(afterHello(h)).toEqual([DisconnectReason.ProtocolError]);
  });

  it('anything but a greeting before the connection completes is a ProtocolError, Ping included', async () => {
    for (const bytes of [
      Uint8Array.of(...encodePreamble(), MessageType.Ping),
      concat([encodePreamble(), encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 1))]),
      concat([encodePreamble(), encodeFrame(MessageType.ClientGreeting, Uint8Array.of(0x90))]),
    ]) {
      const h = harness({ config: { disconnectDelay: 0 } });
      expect((await answer(h, bytes)).disconnectReason).toBe(DisconnectReason.ProtocolError);
    }
  });

  it('a bad preamble is a ProtocolError', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    expect((await answer(h, Uint8Array.of(0x4e, 0x6e, 0x50, 0x14, 0, 0, 0, 9))).disconnectReason).toBe(DisconnectReason.ProtocolError);
  });

  it('a disconnect byte before the greeting ends the connect with its reason, unassigned codes included', async () => {
    for (const code of [DisconnectReason.RateLimited, 26, 38]) {
      const h = harness();
      const r = await answer(h, Uint8Array.of(...encodePreamble(), code));
      expect(r).toEqual({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: code });
      expect(afterHello(h)).toEqual([]);
    }
  });

  it('sends ClientGreeting (100) with the configured limits, version and hashes', async () => {
    const h = harness({ handshake: { version: 'v2', serverNexusHash: 5, clientNexusHash: -6 }, config: { maxMessageSize: 65536, invocationCredits: 3 } });
    void h.client.tryConnect();
    await tick();
    const sent = h.transport.sent;
    expect([...sent.subarray(0, 8)]).toEqual([0x4e, 0x6e, 0x50, 0x14, 0, 0, 0, 1]);
    expect(sent[8]).toBe(100);
    const { decodeClientGreeting } = await import('../../src/session/wire.js');
    expect(decodeClientGreeting(sent.subarray(11))).toEqual({
      version: 'v2', serverNexusHash: 5, clientNexusHash: -6, authenticationToken: new Uint8Array(0), maxMessageSize: 65536, invocationCredits: 3,
    });
  });
});

describe('timers', () => {
  it('handshake timeout closes with Timeout and sends no disconnect byte (§5.1)', async () => {
    const h = harness({ config: { handshakeTimeout: 1000 } });
    const result = h.client.tryConnect();
    await tick();
    h.clock.advance(999);
    await tick();
    expect(h.transport.closed).toBe(false);
    h.clock.advance(1);
    expect(await result).toEqual({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.Timeout });
    expect(afterHello(h)).toEqual([]);
    expect(h.transport.closed).toBe(true);
  });

  it('pings every pingInterval once connected, never before', async () => {
    const h = harness({ config: { pingInterval: 100, timeout: 10_000 } });
    void h.client.tryConnect();
    await tick();
    h.clock.advance(500);
    await tick();
    h.transport.serverSend(serverHello());
    await tick();
    expect(afterHello(h)).toEqual([]);
    for (let i = 0; i < 3; i++) {
      h.clock.advance(100);
      await tick();
    }
    expect(afterHello(h)).toEqual([1, 1, 1]);
  });

  it('derives the ping interval from the advertised idleTimeoutMs (S04), a third of it', async () => {
    const h = harness();
    await connected(h, { idleTimeoutMs: 3000 });
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    expect(s.idleTimeoutMs).toBe(3000);
    expect(s.pingInterval).toBe(1000);
    h.clock.advance(999);
    await tick();
    expect(afterHello(h)).toEqual([]);
    h.clock.advance(1);
    await tick();
    expect(afterHello(h)).toEqual([1]);
  });

  it('an explicit pingInterval wins over idleTimeoutMs; 10 s without either', async () => {
    const a = harness({ config: { pingInterval: 700 } });
    await connected(a, { idleTimeoutMs: 3000 });
    expect(a.client._session?.pingInterval).toBe(700);
    const b = harness();
    await connected(b);
    expect(b.client._session?.pingInterval).toBe(10_000);
  });

  it('derived intervals are at least 1000 ms; 0 advertised means none (S04 parity, J15)', async () => {
    const a = harness();
    await connected(a, { idleTimeoutMs: 1500 });
    expect(a.client._session?.pingInterval).toBe(1000);
    expect(a.client._session?.idleTimeoutMs).toBe(1500);
    const b = harness();
    await connected(b, { idleTimeoutMs: 0 });
    expect(b.client._session?.idleTimeoutMs).toBeNull();
    expect(b.client._session?.pingInterval).toBe(10_000);
    const c = harness();
    await connected(c, { idleTimeoutMs: 0xffffffff });
    expect(c.client._session?.idleTimeoutMs).toBe(0xffffffff);
    expect(c.client._session?.pingInterval).toBe(Math.floor(0xffffffff / 3));
  });

  it('an explicit pingInterval is not capped; one not below the advertised idle timeout logs a warning (J15)', async () => {
    const warned = (/** @type {ReturnType<typeof createHarness>} */ h) =>
      h.logs.some(([level, m]) => level === 'warning' && String(m).includes('is not below the server\'s idle timeout'));
    const above = harness({ config: { pingInterval: 5000 } });
    await connected(above, { idleTimeoutMs: 3000 });
    expect(above.client._session?.pingInterval).toBe(5000);
    expect(warned(above)).toBe(true);
    const equal = harness({ config: { pingInterval: 3000 } });
    await connected(equal, { idleTimeoutMs: 3000 });
    expect(warned(equal)).toBe(true);
    const below = harness({ config: { pingInterval: 2999 } });
    await connected(below, { idleTimeoutMs: 3000 });
    expect(warned(below)).toBe(false);
    const none = harness({ config: { pingInterval: 60_000 } });
    await connected(none);
    expect(none.client._session?.pingInterval).toBe(60_000);
    expect(warned(none)).toBe(false);
    const derived = harness();
    await connected(derived, { idleTimeoutMs: 2000 }); // a derived interval never warns
    expect(warned(derived)).toBe(false);
  });

  it('disconnects with Timeout when no complete frame arrived within timeout, checked on the ping tick (§5.5)', async () => {
    const h = harness({ config: { pingInterval: 100, timeout: 250, disconnectDelay: 0 } });
    await connected(h);
    h.clock.advance(200);
    await tick();
    h.transport.serverSend(Uint8Array.of(MessageType.Invocation, 5)); // partial frame: does not count
    h.clock.advance(100);
    await tick();
    await h.client.disconnected;
    expect(afterHello(h)).toEqual([1, 1, DisconnectReason.Timeout]);
    expect(h.nexus.calls.at(-1)).toEqual(['disconnected', DisconnectReason.Timeout]);
  });

  it('any complete frame refreshes the idle timeout', async () => {
    const h = harness({ config: { pingInterval: 100, timeout: 250 } });
    await connected(h);
    for (let i = 0; i < 10; i++) {
      h.clock.advance(100);
      await tick();
      h.transport.serverSend(Uint8Array.of(MessageType.Ping));
    }
    expect(h.client.state).toBe(ConnectionState.Connected);
  });

  it('a transport write that does not finish within progressTimeout disconnects with Stalled (§9)', async () => {
    const h = harness({ config: { pingInterval: 100, timeout: 60_000, progressTimeout: 300, disconnectDelay: 0 }, transport: { holdWrites: true } });
    void h.client.tryConnect();
    await tick();
    h.transport.releaseWrites();
    h.transport.serverSend(serverHello());
    await tick();
    h.clock.advance(100); // first ping: its write is held
    await tick();
    for (let i = 0; i < 4; i++) {
      h.transport.serverSend(Uint8Array.of(MessageType.Ping));
      h.clock.advance(100);
      await tick();
    }
    h.clock.advance(10_000);
    await h.client.disconnected;
    expect(h.nexus.calls.at(-1)).toEqual(['disconnected', DisconnectReason.Stalled]);
  });

  it('progress probes from other layers are checked on the ping tick', async () => {
    const h = harness({ config: { pingInterval: 100, disconnectDelay: 0 } });
    await connected(h);
    h.client._session?.addProgressProbe((now, limit) => (now > 250 ? `pipe 3 closing for ${now} ms (limit ${limit})` : null));
    h.clock.advance(300);
    await tick();
    await h.client.disconnected;
    expect(h.nexus.calls.at(-1)).toEqual(['disconnected', DisconnectReason.Stalled]);
    expect(h.logs.some(([, m]) => String(m).includes('pipe 3 closing'))).toBe(true);
  });
});

describe('disconnect (§5.6)', () => {
  it('Graceful: writes the byte, waits DisconnectDelay in real time, then closes the transport', async () => {
    const h = harness({ config: { disconnectDelay: 200 } });
    await connected(h);
    fakeRealTime();
    const done = h.client.disconnect();
    await flush();
    expect(afterHello(h)).toEqual([DisconnectReason.Graceful]);
    expect(h.transport.closed).toBe(false);
    expect(h.client.state).toBe(ConnectionState.Disconnecting);
    await flush(199);
    expect(h.transport.closed).toBe(false);
    await flush(1);
    await done;
    expect(h.transport.closed).toBe(true);
    expect(h.client.state).toBe(ConnectionState.Disconnected);
  });

  it('a client clock that never moves does not hold the disconnect delay (J26, C# and Java parity)', async () => {
    const h = harness({ config: { disconnectDelay: 20 } });
    await connected(h);
    const start = performance.now();
    await h.client.disconnect(); // the manual clock is never advanced
    expect(performance.now() - start).toBeGreaterThanOrEqual(15);
    expect(afterHello(h)).toEqual([DisconnectReason.Graceful]);
    expect(h.transport.closed).toBe(true);
    expect(h.client.state).toBe(ConnectionState.Disconnected);
  });

  it('nothing is sent after the disconnect byte', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    const done = s.disconnect();
    expect(await s.send(encodeHeaderOnly(MessageType.Ping))).toBe(false);
    s.controlSender.requestPong();
    await done;
    expect(afterHello(h)).toEqual([DisconnectReason.Graceful]);
  });

  it('frames queued before the disconnect go out before the byte, also behind a write in flight (K25)', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    h.transport.holdWrites = true;
    const inFlight = s.send(encodeHeaderOnly(MessageType.Ping));
    await tick(); // its write is held
    const credit = encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 0x01));
    const queued = [s.send(credit), s.send(credit)];
    const done = s.disconnect();
    h.transport.holdWrites = false;
    h.transport.releaseWrites();
    await done;
    expect(await inFlight).toBe(true);
    expect(await Promise.all(queued)).toEqual([true, true]);
    expect(afterHello(h)).toEqual([MessageType.Ping, ...credit, ...credit, DisconnectReason.Graceful]);
    expect(await s.send(credit)).toBe(false);
  });

  it('a transport that ends is SocketError, without a byte', async () => {
    const h = harness();
    await connected(h);
    h.transport.serverClose(new Error('reset'));
    await h.client.disconnected;
    expect(h.nexus.calls.at(-1)).toEqual(['disconnected', DisconnectReason.SocketError]);
    expect(afterHello(h)).toEqual([]);
  });

  it('a failed transport write is SocketClosedWhenWriting', async () => {
    const h = harness({ transport: { holdWrites: true } });
    void h.client.tryConnect();
    await tick();
    h.transport.releaseWrites();
    h.transport.serverSend(serverHello());
    await tick();
    void h.client._session?.send(encodeHeaderOnly(MessageType.Ping));
    await tick();
    h.transport.failWrites(new Error('broken pipe'));
    await h.client.disconnected;
    expect(h.nexus.calls.at(-1)).toEqual(['disconnected', DisconnectReason.SocketClosedWhenWriting]);
  });

  it('a message type without a handler is a ProtocolError; a registered handler receives its frames', async () => {
    const h = harness({ config: { disconnectDelay: 0 } });
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    /** @type {number[]} */
    const credits = [];
    s.setHandler(MessageType.InvocationCredit, (frame) => {
      credits.push(decodeInvocationCredit(frame.body).credits);
    });
    expect(() => s.setHandler(MessageType.Ping, () => 0)).toThrow(TypeError);
    h.transport.serverSend(concat([encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 4)), encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 5))]));
    expect(credits).toEqual([4, 5]);

    s.setHandler(MessageType.InvocationCancellation, () => DisconnectReason.FlowControl);
    h.transport.serverSend(concat([encodeFrame(MessageType.InvocationCancellation, Uint8Array.of(0x91, 1)), encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 6))]));
    expect(credits).toEqual([4, 5]); // frames after a disconnect are not processed
    await h.client.disconnected;
    expect(afterHello(h)).toEqual([DisconnectReason.FlowControl]);
  });

  it('frames are dispatched synchronously in the data callback, user events are not', async () => {
    const h = harness();
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    /** @type {string[]} */
    const order = [];
    s.setHandler(MessageType.InvocationCredit, () => { order.push('frame'); });
    h.client.addEventListener('disconnected', () => order.push('event'));
    h.transport.serverSend(Uint8Array.of(...encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 1)), DisconnectReason.ServerShutdown));
    order.push('returned');
    await h.client.disconnected;
    await tick();
    expect(order).toEqual(['frame', 'returned', 'event']);
  });

  it('the session close listeners run once with the reason', async () => {
    const h = harness();
    await connected(h);
    /** @type {number[]} */
    const reasons = [];
    h.client._session?.onClose((r) => reasons.push(r));
    h.transport.serverSend(Uint8Array.of(DisconnectReason.ServerRestarting));
    h.transport.serverClose();
    await h.client.disconnected;
    expect(reasons).toEqual([DisconnectReason.ServerRestarting]);
  });
});

describe('stuck transport write (J20)', () => {
  /**
   * Connects over a transport whose writes are held: the preamble and greeting are released, every later write never
   * resolves.
   * @param {Partial<import('../../src/contract/config.js').ClientConfig>} config
   */
  async function stuck(config) {
    const h = harness({ config, transport: { holdWrites: true } });
    void h.client.tryConnect();
    await tick();
    h.transport.releaseWrites();
    h.transport.serverSend(serverHello());
    await tick();
    expect(h.client.state).toBe(ConnectionState.Connected);
    return h;
  }

  /** @param {ReturnType<typeof createHarness>} h */
  function closedReason(h) {
    return h.nexus.calls.find(([name]) => name === 'disconnected')?.[1];
  }

  it('a graceful close behind a stuck write ends once that write has run for progressTimeout', async () => {
    const h = await stuck({ pingInterval: 60_000, progressTimeout: 300, disconnectDelay: 50 });
    void h.client._session?.send(encodeHeaderOnly(MessageType.Ping));
    await tick();
    h.clock.advance(100); // the write has been stuck for 100 ms on the session clock
    fakeRealTime(); // what is left of progressTimeout is waited in real time (J26)
    const done = h.client.disconnect();
    await flush(199);
    expect(h.transport.closed).toBe(false);
    expect(h.client.state).toBe(ConnectionState.Disconnecting);
    await flush(1);
    await done;
    expect(h.transport.closed).toBe(true);
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    expect(closedReason(h)).toBe(DisconnectReason.Graceful);
  });

  it('a graceful close whose disconnect byte write never resolves ends after progressTimeout', async () => {
    const h = await stuck({ pingInterval: 60_000, progressTimeout: 300, disconnectDelay: 50 });
    fakeRealTime();
    const done = h.client.disconnect();
    await flush();
    expect(afterHello(h)).toEqual([DisconnectReason.Graceful]); // handed to the transport, never acknowledged
    await flush(299);
    expect(h.transport.closed).toBe(false);
    await flush(1);
    await done;
    expect(h.transport.closed).toBe(true);
    expect(closedReason(h)).toBe(DisconnectReason.Graceful);
  });

  it('the close wait never exceeds the 5 s disconnect write timeout', async () => {
    const h = await stuck({ pingInterval: 60_000, timeout: 300_000, progressTimeout: 600_000 });
    fakeRealTime();
    const done = h.client.disconnect();
    await flush(4_999);
    expect(h.transport.closed).toBe(false);
    await flush(1);
    await done;
    expect(closedReason(h)).toBe(DisconnectReason.Graceful);
  });

  it('an idle timeout behind a stuck ping write ends within progressTimeout of the write start, as Timeout', async () => {
    const h = await stuck({ pingInterval: 100, timeout: 250, progressTimeout: 1_000, disconnectDelay: 50 });
    h.clock.advance(100); // first ping: its write never resolves
    await tick();
    fakeRealTime(); // the session clock drives the keepalive; the byte wait runs in real time (J26)
    h.clock.advance(200); // tick at 300: nothing received for 300 ms > timeout
    await flush();
    expect(h.client.state).toBe(ConnectionState.Disconnecting);
    await flush(799); // the ping write has run 200 ms on the session clock: 800 ms left
    expect(h.transport.closed).toBe(false);
    await flush(1);
    await h.client.disconnected;
    expect(h.transport.closed).toBe(true);
    expect(closedReason(h)).toBe(DisconnectReason.Timeout);
  });

  it('a write stall closes at once, without waiting for a disconnect byte it can never write, as Stalled', async () => {
    const h = await stuck({ pingInterval: 100, timeout: 60_000, progressTimeout: 300, disconnectDelay: 50 });
    h.clock.advance(100); // first ping: its write never resolves
    await tick();
    for (let i = 0; i < 4; i++) {
      h.transport.serverSend(Uint8Array.of(MessageType.Ping));
      h.clock.advance(100);
      await tick();
    }
    // The tick at 500 saw the write running for 400 ms > progressTimeout; the clock does not move again.
    await h.client.disconnected;
    expect(h.transport.closed).toBe(true);
    expect(closedReason(h)).toBe(DisconnectReason.Stalled);
  });
});

describe('write path', () => {
  it('frames queued in one tick go out in one transport write', async () => {
    const h = harness();
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    const before = h.transport.writes.length;
    const sends = [1, 2, 3].map(() => s.send(encodeHeaderOnly(MessageType.Ping)));
    await tick();
    expect(await Promise.all(sends)).toEqual([true, true, true]);
    expect(h.transport.writes.length).toBe(before + 1);
    expect([...h.transport.writes[before]]).toEqual([1, 1, 1]);
  });

  it('respects backpressure: nothing is written while a write is pending; then one batched write', async () => {
    const h = harness({ transport: { holdWrites: true } });
    void h.client.tryConnect();
    await tick();
    h.transport.releaseWrites();
    h.transport.serverSend(serverHello());
    await tick();
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    const base = h.transport.writes.length;
    const first = s.send(Uint8Array.of(1));
    await tick();
    const later = [s.send(Uint8Array.of(1)), s.send(Uint8Array.of(1))];
    await tick();
    await tick();
    expect(h.transport.writes.length).toBe(base + 1);
    expect(s.pendingWrites).toBe(2);
    expect(getSessionCounters().pendingWrites).toBe(3);
    h.transport.releaseWrites();
    expect(await first).toBe(true);
    await tick();
    expect(h.transport.writes.length).toBe(base + 2);
    expect([...h.transport.writes[base + 1]]).toEqual([1, 1]);
    h.transport.releaseWrites();
    expect(await Promise.all(later)).toEqual([true, true]);
  });

  it('send before the handshake completes resolves false', async () => {
    const h = harness();
    void h.client.tryConnect();
    await tick();
    expect(await h.client._session?.send(Uint8Array.of(1))).toBe(false);
  });
});

describe('control sender', () => {
  it('coalesces pongs, cancellations and aborts, and batches credit at half the advertised credits', async () => {
    const h = harness({ config: { invocationCredits: 8 } });
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    const c = s.controlSender;
    c.requestPong();
    c.requestPong();
    c.requestInvocationCancellation(7);
    c.requestInvocationCancellation(7);
    c.requestAbort(9, 1, 0);
    c.requestAbort(9, 1, 2);
    c.returnInvocationCredit();
    c.returnInvocationCredit(2);
    await tick();
    const sent = afterHello(h);
    const frames = parse(Uint8Array.from(sent));
    expect(frames.map((f) => f.type)).toEqual([MessageType.Ping, MessageType.MessageAbort, MessageType.InvocationCancellation]);
    expect(decodeMessageAbort(frames[1].body)).toEqual({ invocationId: 9, kind: 1, reason: 2 });
    expect(decodeInvocationCancellation(frames[2].body)).toEqual({ invocationId: 7 });
    expect(c.pendingInvocationCredit).toBe(3);

    c.returnInvocationCredit();
    await tick();
    const more = parse(Uint8Array.from(afterHello(h).slice(sent.length)));
    expect(more.map((f) => f.type)).toEqual([MessageType.InvocationCredit]);
    expect(decodeInvocationCredit(more[0].body)).toEqual({ credits: 4 });
  });

  it('credit rides along with other writes in the same flush', async () => {
    const h = harness({ config: { invocationCredits: 2 } });
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    const base = h.transport.writes.length;
    void s.send(encodeHeaderOnly(MessageType.Ping));
    s.controlSender.returnInvocationCredit();
    await tick();
    expect(h.transport.writes.length).toBe(base + 1);
    expect(parse(h.transport.writes[base]).map((f) => f.type)).toEqual([MessageType.Ping, MessageType.InvocationCredit]);
  });

  it('drains pipe control sources once per flush', async () => {
    const h = harness();
    await connected(h);
    const s = /** @type {import('../../src/session/NexusSession.js').NexusSession} */ (h.client._session);
    let drained = 0;
    const pipe = { takePendingControl: () => { drained++; return [encodeFrame(MessageType.DuplexPipeUpdateState, Uint8Array.of(0x93, 1, 4, 0))]; } };
    s.controlSender.requestPipeControl(pipe);
    s.controlSender.requestPipeControl(pipe);
    await tick();
    expect(drained).toBe(1);
    expect(parse(Uint8Array.from(afterHello(h))).map((f) => f.type)).toEqual([MessageType.DuplexPipeUpdateState]);
  });
});

/**
 * Parses frames the client wrote (no preamble).
 * @param {Uint8Array} bytes
 * @returns {Array<{ type: number, body: Uint8Array }>}
 */
function parse(bytes) {
  const out = [];
  let p = 0;
  while (p < bytes.length) {
    const type = bytes[p];
    if (type === MessageType.Ping || (type >= 20 && type <= 38)) {
      out.push({ type, body: new Uint8Array(0) });
      p += 1;
      continue;
    }
    const n = bytes[p + 1] | (bytes[p + 2] << 8);
    out.push({ type, body: bytes.subarray(p + 3, p + 3 + n) });
    p += 3 + n;
  }
  return out;
}

describe('ping interval rule (C# ClientConfig.GetEffectivePingInterval, J15)', () => {
  it('matches the C# rule', () => {
    expect(effectivePingInterval(null, null)).toBe(DEFAULT_PING_INTERVAL);
    expect(effectivePingInterval(null, 0)).toBe(DEFAULT_PING_INTERVAL);
    expect(effectivePingInterval(null, 1)).toBe(MIN_DERIVED_PING_INTERVAL);
    expect(effectivePingInterval(null, 2999)).toBe(1000);
    expect(effectivePingInterval(null, 30_000)).toBe(10_000);
    expect(effectivePingInterval(null, 30_002)).toBe(10_000);
    expect(effectivePingInterval(null, 0xffffffff)).toBe(1_431_655_765);
    expect(effectivePingInterval(1, 30_000)).toBe(1);
    expect(effectivePingInterval(90_000, 30_000)).toBe(90_000);
    expect(effectivePingInterval(500, null)).toBe(500);
  });

  it('a zero, negative or non-integer pingInterval is a config error', () => {
    const resolve = (/** @type {unknown} */ pingInterval) => resolveClientOptions(
      /** @type {any} */ ({ tcp: { host: 'localhost', port: 1 }, pingInterval }), { tcp: () => null });
    expect(() => resolve(0)).toThrow(RangeError);
    expect(() => resolve(-1)).toThrow(RangeError);
    expect(() => resolve(1.5)).toThrow(TypeError);
    expect(resolve(1).pingInterval).toBe(1);
    expect(resolve(undefined).pingInterval).toBeNull();
    expect(resolve(null).pingInterval).toBeNull();
  });
});
