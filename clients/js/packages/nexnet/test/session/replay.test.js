import { describe, expect, it } from 'vitest';
import { ConnectionResultState, ConnectionState, DisconnectReason } from '../../src/contract/enums.js';
import { decodeServerGreeting } from '../../src/session/wire.js';
import { createHarness, tick } from './support/harness.js';
import { fromHex, recordings, toHex } from './support/fixtures.js';

/**
 * Splits a recorded server stream into preamble + ServerGreeting and the rest.
 * @param {Uint8Array} s2c
 */
function splitAfterGreeting(s2c) {
  expect(s2c[8]).toBe(105);
  const end = 8 + 3 + (s2c[9] | (s2c[10] << 8));
  return { hello: s2c.subarray(0, end), greetingBody: s2c.subarray(11, end), rest: s2c.subarray(end) };
}

/** @param {import('./support/fixtures.js').SessionRecording} rec */
function handshakeOf(rec) {
  return { serverNexusHash: rec.serverNexusHash, clientNexusHash: rec.clientNexusHash };
}

/** Length of preamble + ClientGreeting at the start of a recorded client stream. @param {Uint8Array} c2s */
function clientHelloLength(c2s) {
  expect(c2s[8]).toBe(100);
  return 8 + 3 + (c2s[9] | (c2s[10] << 8));
}

describe('replays C# sessions (fixtures/frames.jsonl)', () => {
  it('handshake, pings and graceful disconnect: the JS client sends the same bytes as the C# client', async () => {
    const rec = recordings['handshake-ping-graceful'];
    const h = createHarness({
      handshake: handshakeOf(rec),
      config: { pingInterval: 250, disconnectDelay: 0, authenticate: () => new TextEncoder().encode('token-123') },
    });
    const c2s = fromHex(rec.clientToServer);
    const { hello, greetingBody, rest } = splitAfterGreeting(fromHex(rec.serverToClient));
    expect([...rest]).toEqual([1, 1]);

    const connecting = h.client.connect();
    await tick();
    expect(toHex(h.transport.sent)).toBe(toHex(c2s.subarray(0, clientHelloLength(c2s))));
    // Preamble and greeting go out in one transport write.
    expect(h.transport.writes.length).toBe(1);

    for (const b of hello) h.transport.serverSend(Uint8Array.of(b)); // byte by byte
    await connecting;
    expect(h.client.state).toBe(ConnectionState.Connected);
    expect(h.client._session?.id).toBe(decodeServerGreeting(greetingBody).clientId);

    for (let i = 0; i < 2; i++) {
      h.clock.advance(250);
      await tick();
      h.transport.serverSend(Uint8Array.of(1));
    }
    await h.client.disconnect();

    expect(toHex(h.transport.sent)).toBe(rec.clientToServer);
    expect(h.client.state).toBe(ConnectionState.Disconnected);
    expect(h.transport.closed).toBe(true);
  });

  it('server shutdown: the recorded disconnect byte ends the session with ServerShutdown', async () => {
    const rec = recordings['server-shutdown'];
    const h = createHarness({ handshake: handshakeOf(rec), config: { disconnectDelay: 0 } });
    const { hello, rest } = splitAfterGreeting(fromHex(rec.serverToClient));
    const connecting = h.client.connect();
    await tick();
    h.transport.serverSend(hello);
    await connecting;
    await tick();

    h.transport.serverSend(rest);
    await h.client.disconnected;
    await tick();
    expect(rec.clientDisconnectReason).toBe(DisconnectReason.ServerShutdown);
    expect(h.events.filter((e) => e.type === 'disconnected').map((e) => e.detail.reason)).toEqual([DisconnectReason.ServerShutdown]);
    expect(h.nexus.calls).toEqual([['connected', false], ['disconnected', DisconnectReason.ServerShutdown]]);
    // A received disconnect byte is never answered (§5.6).
    expect(toHex(h.transport.sent)).toBe(rec.clientToServer);
  });

  for (const scenario of ['client-mismatch', 'server-mismatch', 'authentication-rejected']) {
    it(`${scenario}: tryConnect reports the recorded disconnect reason`, async () => {
      const rec = recordings[scenario];
      const token = scenario === 'authentication-rejected' ? Uint8Array.of(0xde, 0xad) : null;
      const h = createHarness({ handshake: handshakeOf(rec), config: { disconnectDelay: 0, authenticate: () => token } });
      const result = h.client.tryConnect();
      await tick();
      h.transport.serverSend(fromHex(rec.serverToClient));
      expect(await result).toEqual({ success: false, state: ConnectionResultState.Disconnected, disconnectReason: rec.clientDisconnectReason });
      expect(toHex(h.transport.sent)).toBe(rec.clientToServer);
      expect(h.client.state).toBe(ConnectionState.Disconnected);
      expect(() => h.client.proxy).toThrow(/not connected/);
      await tick();
      expect(h.events.some((e) => e.type === 'disconnected')).toBe(false);
    });
  }

  it('connect() rejects with NexusTransportError carrying the recorded reason', async () => {
    const rec = recordings['authentication-rejected'];
    const h = createHarness({ handshake: handshakeOf(rec), config: { authenticate: () => Uint8Array.of(0xde, 0xad) } });
    const connecting = h.client.connect();
    await tick();
    h.transport.serverSend(fromHex(rec.serverToClient));
    await expect(connecting).rejects.toMatchObject({ name: 'NexusTransportError', reason: DisconnectReason.Authentication });
  });
});
