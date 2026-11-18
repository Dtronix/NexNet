import { describe, expect, it } from 'vitest';
import { NodeNexusClient } from '../src/node.js';
import { SampleClientNexusBase, SampleHandshake, SampleServerProxy, createSampleClient } from './fixtures/generated-sample.js';

class SampleNexus extends SampleClientNexusBase {
  /** @param {string} text */
  async shout(text) {
    return text.toUpperCase();
  }
}

describe('generated-code shape against the contract', () => {
  it('the generated factory builds a client with the binding', () => {
    const client = createSampleClient({ tcp: { host: 'localhost', port: 1 } }, new SampleNexus());
    expect(client).toBeInstanceOf(NodeNexusClient);
    expect(Object.isFrozen(SampleHandshake)).toBe(true);
  });

  it('proxy methods reach the runtime invoke members', () => {
    const proxy = new SampleServerProxy({});
    expect(() => proxy.echo('x')).toThrow(/not implemented: ProxyBase._invoke/);
    expect(() => proxy.notify()).toThrow(/not implemented: ProxyBase._invokeVoid/);
    expect(() => proxy.names).toThrow(/not implemented: ProxyBase._collection/);
  });

  it('unknown method IDs fall through to the base dispatcher', () => {
    const nexus = new SampleNexus();
    const ctx = /** @type {import('../src/index.js').DispatchContext} */ ({});
    const reader = /** @type {import('../src/index.js').MsgPackReader} */ ({});
    // @ts-expect-error _dispatch is protected; the runtime calls it.
    expect(() => nexus._dispatch(99, reader, ctx)).toThrow(/not implemented: ClientNexusBase._dispatch/);
  });
});
