import { describe, expect, it } from 'vitest';
import { parseSpec, parsePoolOptions, reconnectionPolicy } from '../src/core/spec.js';
import { HostError } from '../src/core/errors.js';

const node = { transports: ['Tcp', 'TcpTls', 'HttpSocket', 'HttpSocketTls', 'WebSocket', 'WebSocketTls'], browser: false };
const browser = { transports: ['WebSocket', 'WebSocketTls'], browser: true };

/** @param {() => unknown} action @param {string} kind */
function expectKind(action, kind) {
  try {
    action();
  } catch (error) {
    expect(error).toBeInstanceOf(HostError);
    expect(/** @type {HostError} */ (error).kind).toBe(kind);
    return;
  }
  throw new Error('expected ' + kind);
}

describe('ClientSpec', () => {
  it('maps each transport and its TLS options as the C# driver does', () => {
    expect(parseSpec({ transport: 'Tcp', address: 'tcp://127.0.0.1:5000' }, node).config.tcp).toEqual({ host: '127.0.0.1', port: 5000 });
    const tls = parseSpec({ transport: 'TcpTls', address: 'tls://[::1]:5001', caPem: 'CA' }, node).config.tcpTls;
    expect(tls).toEqual({ host: '::1', port: 5001, tls: { ca: 'CA', servername: 'localhost' } });
    const pinned = parseSpec({ transport: 'WebSocketTls', address: 'wss://localhost:5002/ws', caPem: 'CA', pinSha256: 'pin' }, node).config.webSocket;
    expect(pinned.tls).toEqual({ pins: ['pin'], rejectUnauthorized: false });
    const http = parseSpec({ transport: 'HttpSocket', address: 'http://localhost:5003/h', authorizationHeader: 'Bearer x' }, node).config.httpSocket;
    expect(http).toEqual({ url: 'http://localhost:5003/h', headers: { Authorization: 'Bearer x' } });
  });

  it('maps the common options', () => {
    const parsed = parseSpec({
      transport: 'WebSocket', address: 'ws://h/ws', authToken: 'tok', timeoutMs: 5, connectionTimeoutMs: 6, handshakeTimeoutMs: 7,
      pingIntervalMs: 8, maxMessageSize: 9, invocationCredits: 10, maxConcurrentConnectionInvocations: 11,
      remoteExceptionDetail: 'TypeAndMessage', fakeTime: true, logLevel: 'Debug', onInvocationException: { code: 3 },
    }, node);
    expect(parsed.config).toMatchObject({ timeout: 5, connectionTimeout: 6, handshakeTimeout: 7, pingInterval: 8, maxMessageSize: 9,
      invocationCredits: 10, maxConcurrentConnectionInvocations: 11, remoteExceptionDetail: 1, reconnectionPolicy: null });
    expect(new TextDecoder().decode(parsed.config.authenticate())).toBe('tok');
    expect(parsed.fakeTime).toBe(true);
    expect(parsed.logLevel).toBe('debug');
    expect(parsed.onInvocationException).toEqual({ code: 3, message: null });
  });

  it('refuses unknown members and what the client cannot honor', () => {
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', nope: 1 }, node), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'Quic', address: 'quic://h:1' }, node), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authorizationHeader: 'x' }, node), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'WebSocketTls', address: 'wss://h/ws', pinSha256: 'p' }, browser), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'WebSocket', address: 'ws://h/ws', authorizationHeader: 'x' }, browser), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', timeoutMs: 'x' }, node), 'BadRequest');
    // The browser does the TLS: a CA is accepted (the launcher's browser context trusts the test server).
    expect(parseSpec({ transport: 'WebSocketTls', address: 'wss://h/ws', caPem: 'CA' }, browser).config.webSocket).toEqual({ url: 'wss://h/ws' });
  });

  it('reconnects like the C# DefaultReconnectionPolicy', () => {
    expect([0, 1, 2, 3].map(reconnectionPolicy([5, 10], false).reconnectDelay)).toEqual([5, 10, null, null]);
    expect([0, 1, 2, 3].map(reconnectionPolicy([5, 10], true).reconnectDelay)).toEqual([5, 10, 10, 10]);
    const parsed = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', reconnectDelaysMs: [1, 2], reconnectContinuously: true }, node);
    expect(parsed.config.reconnectionPolicy.reconnectDelay(5)).toBe(2);
  });

  it('turns reconnection off unless defaultReconnection keeps the runtime default', () => {
    expect(parseSpec({ transport: 'WebSocket', address: 'ws://h/ws' }, browser).config.reconnectionPolicy).toBeNull();
    const kept = parseSpec({ transport: 'WebSocket', address: 'ws://h/ws', defaultReconnection: true }, browser).config;
    expect('reconnectionPolicy' in kept).toBe(false);
    const delays = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', defaultReconnection: true, reconnectDelaysMs: [3] }, node).config;
    expect(delays.reconnectionPolicy.reconnectDelay(0)).toBe(3);
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', defaultReconnection: 1 }, node), 'BadRequest');
  });

  it('validates pool options', () => {
    expect(parsePoolOptions({})).toEqual({ maxConnections: 10, maxIdleTime: 120000, minIdleConnections: 1 });
    expect(parsePoolOptions({ maxConnections: 2, maxIdleTimeMs: 0, minIdleConnections: 5 })).toEqual({ maxConnections: 2, maxIdleTime: 0, minIdleConnections: 5 });
    expectKind(() => parsePoolOptions({ maxConnections: 0 }), 'BadRequest');
    expectKind(() => parsePoolOptions({ maxIdleTimeMs: -1 }), 'BadRequest');
    expectKind(() => parsePoolOptions({ other: 1 }), 'NotSupported');
  });
});
