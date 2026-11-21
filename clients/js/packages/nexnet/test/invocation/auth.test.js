import { describe, expect, it } from 'vitest';
import { decodeClientGreeting } from '../../src/session/wire.js';
import { serverHello, tick } from '../session/support/harness.js';
import { connectedHarness, parseFrames } from './support/harness.js';

/** @param {Uint8Array} sent */
function greetingToken(sent) {
  return decodeClientGreeting(sent.subarray(11, 11 + (sent[9] | (sent[10] << 8)))).authenticationToken;
}

describe('authenticate (D15)', () => {
  it('a synchronous provider is called on every connect and its token goes in the greeting', async () => {
    let calls = 0;
    const h = await connectedHarness({ config: { authenticate: () => Uint8Array.of(++calls) } });
    expect(greetingToken(h.transport.sent)).toEqual(Uint8Array.of(1));
    await h.client.disconnect();

    const again = h.client.tryConnect();
    await tick();
    h.transport.serverSend(serverHello());
    expect((await again).success).toBe(true);
    expect(calls).toBe(2);
    expect(greetingToken(h.transport.sent)).toEqual(Uint8Array.of(2));
    expect(h.transports.length).toBe(2);
    await h.client.disconnect();
  });

  it('an async provider gets an AbortSignal and may return null for no token', async () => {
    /** @type {AbortSignal | null} */
    let signal = null;
    const h = await connectedHarness({
      config: {
        authenticate: async (options) => {
          signal = options.signal;
          await tick();
          return null;
        },
      },
    });
    expect(signal).toBeInstanceOf(AbortSignal);
    expect(greetingToken(h.transport.sent)).toEqual(new Uint8Array(0));
    expect(parseFrames(h.transport.sent)).toEqual([]);
    await h.client.disconnect();
  });

  // A failing provider (no connection, DisconnectReason.Authentication) is covered by test/session/client.test.js.
});
