import { describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { TransportBase, transportError, upgradeRefused } from '../../src/transport/TransportBase.js';

class TestTransport extends TransportBase {
  released = 0;
  constructor() {
    super('test', false);
  }
  /** @param {Uint8Array} d */
  deliver(d) { this._deliver(d); }
  /** @param {unknown} [e] */
  end(e) { this._ended(e); }
  _release() { this.released++; }
}

describe('TransportBase', () => {
  it('keeps bytes and the close that arrive before the session registers, and reports them in order', async () => {
    const t = new TestTransport();
    t.deliver(Uint8Array.of(1));
    t.deliver(Uint8Array.of(2));
    const error = new Error('gone');
    t.end(error);
    t.deliver(Uint8Array.of(3)); // after the end: dropped
    /** @type {unknown[]} */
    const log = [];
    t.onClose((e) => log.push(['close', e]));
    t.onData((d) => log.push([...d]));
    expect(log).toEqual([[1], [2]]);
    await Promise.resolve();
    expect(log).toEqual([[1], [2], ['close', error]]);
    expect(t.released).toBe(1);
  });

  it('reports the close once, and ends the transport when the data callback throws', () => {
    const t = new TestTransport();
    /** @type {unknown[]} */
    const closes = [];
    const boom = new Error('parse');
    t.onClose((e) => closes.push(e));
    t.onData(() => { throw boom; });
    t.deliver(Uint8Array.of(1));
    t.end(new Error('later'));
    expect(closes).toEqual([boom]);
    expect(t.ended).toBe(true);
  });

  it('maps errors to disconnect reasons as the C# client does', () => {
    expect(transportError('x', { kind: 'AuthenticationError' }).reason).toBe(DisconnectReason.Authentication);
    expect(transportError('x', { kind: 'ConnectionRefused' }).reason).toBe(DisconnectReason.SocketError);
    expect(upgradeRefused('HttpSocket', 404, 'u')).toMatchObject({ transportError: 'InternalError', status: 404 });
    expect(upgradeRefused('WebSocket', 404, 'u')).toMatchObject({ transportError: 'ProtocolError', status: 404 });
    expect(upgradeRefused('WebSocket', 503, 'u')).toMatchObject({ transportError: 'InternalError' });
    expect(upgradeRefused('WebSocket', 403, 'u')).toMatchObject({ transportError: 'AuthenticationError', reason: DisconnectReason.Authentication });
  });
});
