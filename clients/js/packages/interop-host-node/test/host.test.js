import { describe, expect, it } from 'vitest';
import { Host, listChangedData, PROTOCOL_VERSION } from '../src/core/host.js';
import { FrameDecoder } from '../src/core/framing.js';
import { pumpItem } from '../src/core/scripts.js';
import { nativeError, toWireError, HostError, ThrowingWatchError } from '../src/core/errors.js';
import { NexusRemoteInvocationError, NexusTransportError } from '@dtronix/nexnet';

/** A host over a fake platform with no generated code; returns it and the messages it sends. */
function createHost() {
  const decoder = new FrameDecoder();
  /** @type {Record<string, any>[]} */
  const sent = [];
  /** @type {number[]} */
  const exits = [];
  let closed = false;
  const host = new Host(
    {
      lang: 'node',
      token: 'tok',
      runtime: 'test',
      lib: 'lib',
      pid: 42,
      capabilities: ['Tcp', 'ClientPool'],
      transports: ['Tcp'],
      maxConcurrency: 2,
      browser: false,
      send: (frame) => sent.push(...decoder.push(frame)),
      close: () => {
        closed = true;
      },
      exit: (code) => exits.push(code),
      dumpThreads: () => 'threads',
    },
    { schemaHash: 'abc', find: () => null, forType: () => null },
  );
  let id = 1;
  /**
   * @param {string | null} test
   * @param {string} op
   * @param {Record<string, unknown>} [args]
   * @param {number} [timeoutMs]
   */
  const request = async (test, op, args = {}, timeoutMs) => {
    const requestId = id++;
    host.receive({ t: 'req', id: requestId, test, op, args, timeoutMs });
    for (let i = 0; i < 200; i++) {
      const response = sent.find((m) => m.t === 'res' && m.id === requestId);
      if (response) return response;
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    throw new Error('no response to ' + op);
  };
  return { host, sent, exits, request, isClosed: () => closed };
}

describe('Host', () => {
  it('says hello with the platform, the schema hash and the token', () => {
    const { host, sent } = createHost();
    host.start();
    expect(sent[0]).toEqual({
      t: 'hello', proto: PROTOCOL_VERSION, token: 'tok', lang: 'node', runtime: 'test', lib: 'lib', schemaHash: 'abc',
      capabilities: ['Tcp', 'ClientPool'], transports: ['Tcp'], maxConcurrency: 2, pid: 42,
    });
  });

  it('reports uncaught errors as host.error, except the throw of a list.watch with throws', () => {
    const { host, sent } = createHost();
    host.hostError(new ThrowingWatchError());
    expect(sent.filter((m) => m.ev === 'host.error')).toEqual([]);
    host.hostError(new Error('real'));
    expect(sent.filter((m) => m.ev === 'host.error').map((m) => m.data.message)).toEqual(['real']);
  });

  it('answers ping at once and runs an empty scope clean', async () => {
    const { host, sent, request } = createHost();
    host.receive({ t: 'req', id: 1, op: 'ping' });
    expect(sent.at(-1)).toMatchObject({ t: 'res', id: 1, ok: true, value: {} });
    expect(typeof sent.at(-1)?.hostNs).toBe('number');
    expect((await request('t1', 'test.begin')).ok).toBe(true);
    const end = await request('t1', 'test.end');
    expect(end.value).toEqual({ openClients: 0, openPipes: 0, timers: 0, pendingInvocations: 0, subscriptions: 0, details: [] });
    // The scope is gone.
    expect((await request('t1', 'test.audit')).error.kind).toBe('BadRequest');
  });

  it('refuses unknown ops, missing scopes, unknown handles and unknown nexus pairs', async () => {
    const { request } = createHost();
    expect((await request(null, 'nope')).error.kind).toBe('BadRequest');
    expect((await request(null, 'client.state', { h: 'c1' })).error.kind).toBe('BadRequest');
    await request('t', 'test.begin');
    expect((await request('t', 'client.state', { h: 'c1' })).error.kind).toBe('UnknownHandle');
    expect((await request('t', 'future.await', { future: 'f9' })).error.kind).toBe('UnknownHandle');
    const create = await request('t', 'client.create', { spec: { transport: 'Tcp', address: 'tcp://127.0.0.1:1' }, nexus: { client: 'A', server: 'B' } });
    expect(create.error.kind).toBe('NotSupported');
    expect((await request('t', 'pool.create', { spec: {}, nexus: { client: 'A', server: 'B' } })).error.kind).toBe('NotSupported');
    expect((await request(null, 'log.level', { level: 'Loud' })).error.kind).toBe('BadRequest');
    expect((await request(null, 'log.level', { level: 'Debug' })).ok).toBe(true);
  });

  it('creates, cancels and disposes runner-named tokens', async () => {
    const { request } = createHost();
    await request('t', 'test.begin');
    expect((await request('t', 'token.create', { token: 'r1' })).ok).toBe(true);
    expect((await request('t', 'token.create', { token: 'r1' })).error.kind).toBe('BadRequest');
    expect((await request('t', 'token.cancel', { token: 'r1' })).ok).toBe(true);
    expect((await request('t', 'token.cancel', { token: 'unknown' })).ok).toBe(true);
    expect((await request('t', 'token.dispose', { token: 'r1' })).ok).toBe(true);
  });

  it('dumps what it is doing', async () => {
    const { request } = createHost();
    const dump = await request(null, 'host.dumpThreads');
    expect(dump.value.text).toContain('threads');
    expect(dump.value.text).toContain('requests in flight');
  });

  it('shuts down with bye and exit code 0, and fails the connection on an unknown message type', async () => {
    const shutdown = createHost();
    await shutdown.request(null, 'host.shutdown');
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(shutdown.sent.at(-1)).toEqual({ t: 'bye', reason: 'host.shutdown' });
    expect(shutdown.exits).toEqual([0]);
    expect(shutdown.isClosed()).toBe(true);

    const broken = createHost();
    broken.host.receive({ t: 'what' });
    expect(broken.sent.at(-1)?.t).toBe('bye');
    expect(broken.exits).toEqual([1]);
  });
});

describe('host mappings', () => {
  const codec = { expression: 'int', formatter: /** @type {any} */ (null), toJson: (/** @type {any} */ v) => v, fromJson: (/** @type {any} */ v) => v };

  it('maps runtime list changes to list.changed (index after, oldIndex before)', () => {
    expect(listChangedData('w', { action: 'add', index: 1, item: 5 }, codec)).toEqual({ watch: 'w', action: 'Add', index: 1, items: [5] });
    expect(listChangedData('w', { action: 'remove', index: 2, item: 5 }, codec)).toEqual({ watch: 'w', action: 'Remove', oldIndex: 2, oldItems: [5] });
    expect(listChangedData('w', { action: 'replace', index: 0, item: 6, oldItem: 5 }, codec))
      .toEqual({ watch: 'w', action: 'Replace', index: 0, items: [6], oldItems: [5] });
    expect(listChangedData('w', { action: 'move', fromIndex: 0, toIndex: 3, item: 5 }, codec))
      .toEqual({ watch: 'w', action: 'Move', index: 3, oldIndex: 0, items: [5] });
    expect(listChangedData('w', { action: 'reset' }, codec)).toEqual({ watch: 'w', action: 'Reset' });
    expect(listChangedData('w', { action: 'ready' }, codec)).toEqual({ watch: 'w', action: 'Ready' });
  });

  it('makes channel.pump items as the C# handler does', () => {
    expect(pumpItem('byte', 300, 0)).toBe(44);
    expect(pumpItem('sbyte', 200, 0)).toBe(-56);
    expect(pumpItem('long', 3, 0)).toBe(3n);
    expect(pumpItem('string', 25, 3)).toBe('zab');
    expect(pumpItem('byte[]', 7, 3)).toEqual(new Uint8Array([7, 38, 69]));
    expect(pumpItem('double', 1, 0)).toBeUndefined();
  });

  it('maps errors to kinds and kinds to native errors', () => {
    expect(toWireError(new NexusRemoteInvocationError('x', { remoteMessage: null, remoteType: 'T' }))).toEqual({ kind: 'RemoteInvocation', message: null, detail: { type: 'T' } });
    expect(toWireError(new NexusTransportError('not connected'))).toEqual({ kind: 'Disconnected', message: 'Session is not connected' });
    expect(toWireError(new DOMException('a', 'AbortError')).kind).toBe('Canceled');
    expect(toWireError(new RangeError('index')).kind).toBe('ArgumentOutOfRange');
    expect(toWireError(new HostError('Timeout', 'late')).kind).toBe('Timeout');
    expect(toWireError(new Error('bug')).kind).toBe('Internal');
    const invalid = /** @type {Error} */ (nativeError('InvalidOperation', 'nope'));
    expect(invalid.constructor.name).toBe('System.InvalidOperationException');
    expect(invalid.message).toBe('nope');
    expect(/** @type {Error} */ (nativeError('Canceled', 'c')).name).toBe('AbortError');
  });
});
