import { describe, expect, it } from 'vitest';
import * as core from '../src/index.js';
import * as browser from '../src/browser.js';
import * as node from '../src/node.js';

const {
  ClientNexusBase, ConnectionState, DisconnectReason, FormatterRegistry, MsgPackReader, MsgPackWriter,
  NexusBusyError, NexusChannelReader, NexusChannelWriter, NexusClient, NexusClientPool, NexusDateTime,
  NexusDecimal, NexusDuplexChannel, NexusDuplexPipe, NexusError, NexusList, NexusMessageTooLargeError,
  NexusRemoteInvocationError, NexusTransportError, NexusUnauthorizedError, NexusVersion, ProxyBase, Version,
  cancellationError, isCancellationError, tuple,
} = core;

/** The public runtime surface (01-context §3). Adding a name here is additive; removing one is a contract change. */
const coreExports = [
  'ClientNexusBase', 'ConnectionResultState', 'ConnectionState', 'DateTimeKind', 'DisconnectReason',
  'FormatterRegistry', 'MsgPackReader', 'MsgPackWriter', 'NexusBusyError', 'NexusChannelReader',
  'NexusChannelWriter', 'NexusClient', 'NexusClientPool', 'NexusCollectionState', 'NexusDateOnly',
  'NexusDateTime', 'NexusDateTimeOffset', 'NexusDecimal', 'NexusDuplexChannel', 'NexusDuplexPipe', 'NexusError',
  'NexusList', 'NexusMessageTooLargeError', 'NexusRemoteInvocationError', 'NexusTimeOnly', 'NexusTimeSpan',
  'NexusTransportError', 'NexusUnauthorizedError', 'NexusVersion', 'ProxyBase', 'RemoteExceptionDetail',
  'TransportKinds', 'Version', 'cancellationError', 'isCancellationError', 'tuple', 'tupleFormatter',
];

const notImplemented = /^not implemented: /;

describe('entry points', () => {
  it('core exports exactly the contract surface', () => {
    expect(Object.keys(core).sort()).toEqual([...coreExports].sort());
  });

  it('browser re-exports core unchanged', () => {
    expect(Object.keys(browser).sort()).toEqual(Object.keys(core).sort());
    for (const name of coreExports) expect(browser[/** @type {keyof typeof browser} */ (name)]).toBe(core[/** @type {keyof typeof core} */ (name)]);
  });

  it('node adds NodeNexusClient and exports it as NexusClient', () => {
    expect(Object.keys(node).sort()).toEqual([...coreExports, 'NodeNexusClient'].sort());
    expect(node.NexusClient).toBe(node.NodeNexusClient);
    expect(Object.getPrototypeOf(node.NodeNexusClient)).toBe(core.NexusClient);
  });

  it('Version is NexusVersion (G03 type mapper name)', () => {
    expect(Version).toBe(NexusVersion);
  });
});

describe('transports per entry', () => {
  it('core supports webSocket only', () => {
    expect(Object.keys(NexusClient.transports)).toEqual(['webSocket']);
  });

  it('node supports every transport kind', () => {
    expect(Object.keys(node.NexusClient.transports).sort()).toEqual([...core.TransportKinds].sort());
  });

  it('transport stubs throw not implemented', () => {
    const factory = /** @type {import('../src/index.js').TransportFactory} */ (node.NexusClient.transports.tcp);
    expect(() => factory({}, { signal: new AbortController().signal })).toThrow(notImplemented);
  });
});

describe('client skeleton', () => {
  class TestProxy extends ProxyBase {}
  class TestNexus extends ClientNexusBase {}
  /** @type {import('../src/index.js').ClientBinding<TestProxy>} */
  const binding = {
    handshake: { version: null, serverNexusHash: 1, clientNexusHash: 2, acceptedIds: new Set([0]) },
    proxy: TestProxy,
  };

  it('create builds an instance of the class it is called on', () => {
    const config = { webSocket: { url: 'ws://localhost:1' } };
    const nexus = new TestNexus();
    const client = node.NexusClient.create(config, nexus, binding);
    expect(client).toBeInstanceOf(node.NodeNexusClient);
    expect(client).toBeInstanceOf(EventTarget);
    expect(client.config).toBe(config);
    expect(client.nexus).toBe(nexus);
  });

  it('members throw not implemented', async () => {
    const client = NexusClient.create({ webSocket: { url: 'ws://localhost:1' } }, new TestNexus(), binding);
    expect(() => client.state).toThrow(notImplemented);
    expect(() => client.proxy).toThrow(notImplemented);
    expect(() => client.isSecure).toThrow(notImplemented);
    expect(() => client.connect()).toThrow(notImplemented);
    expect(() => client.tryConnect()).toThrow(notImplemented);
    expect(() => client.disconnect()).toThrow(notImplemented);
    expect(() => client[Symbol.asyncDispose]()).toThrow(notImplemented);
  });

  it('nexus lifecycle hooks default to no-ops; _dispatch throws', () => {
    const nexus = new TestNexus();
    expect(nexus.onConnected(false)).toBeUndefined();
    expect(nexus.onDisconnected(DisconnectReason.Graceful)).toBeUndefined();
    expect(nexus.onReconnecting()).toBeUndefined();
    expect(() => nexus.context).toThrow(notImplemented);
  });

  it('every skeleton prototype method throws not implemented', () => {
    const classes = [
      MsgPackWriter, MsgPackReader, FormatterRegistry, NexusDuplexPipe, NexusChannelReader, NexusChannelWriter,
      NexusDuplexChannel, NexusList, ProxyBase, NexusClientPool,
    ];
    for (const cls of classes) {
      const proto = cls.prototype;
      const keys = [...Object.getOwnPropertyNames(proto), ...Object.getOwnPropertySymbols(proto)]
        .filter((k) => k !== 'constructor');
      expect(keys.length, cls.name).toBeGreaterThan(0);
      for (const key of keys) {
        const desc = /** @type {PropertyDescriptor} */ (Object.getOwnPropertyDescriptor(proto, key));
        const fn = desc.get ?? desc.value;
        expect(() => fn.call(Object.create(proto)), `${cls.name}.${String(key)}`).toThrow(notImplemented);
      }
    }
  });
});

describe('values', () => {
  it('constructors keep their fields', () => {
    const d = new NexusDecimal(12345n, 2, true);
    expect([d.magnitude, d.scale, d.negative]).toEqual([12345n, 2, true]);
    expect(new NexusDateTime(5n).kind).toBe(core.DateTimeKind.Unspecified);
    const v = new Version(1, 2);
    expect([v.major, v.minor, v.build, v.revision]).toEqual([1, 2, -1, -1]);
  });

  it('tuple returns its items', () => {
    expect(tuple(1, 'a', 2n)).toEqual([1, 'a', 2n]);
  });
});

describe('errors', () => {
  it('error classes carry their names and fields', () => {
    const remote = new NexusRemoteInvocationError('failed', { code: 3, remoteType: 'System.Exception', remoteMessage: 'x' });
    expect(remote).toBeInstanceOf(NexusError);
    expect(remote.name).toBe('NexusRemoteInvocationError');
    expect([remote.code, remote.remoteType, remote.remoteMessage]).toEqual([3, 'System.Exception', 'x']);
    const transport = new NexusTransportError('lost', { reason: DisconnectReason.Timeout });
    expect(transport.reason).toBe(23);
    for (const E of [NexusUnauthorizedError, NexusBusyError, NexusMessageTooLargeError]) {
      const e = new E('m');
      expect(e).toBeInstanceOf(NexusError);
      expect(e.name).toBe(E.name);
    }
  });

  it('cancellation maps to AbortError or the signal reason', () => {
    const plain = cancellationError();
    expect(plain).toBeInstanceOf(DOMException);
    expect(/** @type {DOMException} */ (plain).name).toBe('AbortError');
    expect(isCancellationError(plain)).toBe(true);

    const controller = new AbortController();
    controller.abort();
    expect(isCancellationError(cancellationError(controller.signal))).toBe(true);

    const reason = new Error('custom');
    const custom = new AbortController();
    custom.abort(reason);
    expect(cancellationError(custom.signal)).toBe(reason);
    expect(isCancellationError(new DOMException('t', 'TimeoutError'))).toBe(true);
    expect(isCancellationError(new Error('x'))).toBe(false);
    expect(isCancellationError(null)).toBe(false);
  });
});

describe('enums', () => {
  it('match the C# values', () => {
    expect(ConnectionState).toEqual({ Unset: 0, Connecting: 1, Connected: 2, Reconnecting: 3, Disconnecting: 4, Disconnected: 5 });
    expect(Object.isFrozen(DisconnectReason)).toBe(true);
    expect(DisconnectReason.Stalled).toBe(37);
  });
});
