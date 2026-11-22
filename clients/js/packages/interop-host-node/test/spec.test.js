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

  it('reconnects only for the reasons of reconnectReasons', () => {
    const parsed = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', reconnectDelaysMs: [1], reconnectReasons: ['ServerShutdown', 'SocketError'] }, node);
    expect(parsed.config.reconnectionPolicy.shouldReconnect(28)).toBe(true);
    expect(parsed.config.reconnectionPolicy.shouldReconnect(20)).toBe(true);
    expect(parsed.config.reconnectionPolicy.shouldReconnect(23)).toBe(false);
    expect(parseSpec({ transport: 'Tcp', address: 'tcp://h:1', reconnectDelaysMs: [1] }, node).config.reconnectionPolicy.shouldReconnect).toBeUndefined();
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', reconnectReasons: ['SocketError'] }, node), 'BadRequest');
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', reconnectDelaysMs: [1], reconnectReasons: ['Nope'] }, node), 'BadRequest');
  });

  it('maps the pipe, budget and pool limits', () => {
    const parsed = parseSpec({
      transport: 'Tcp', address: 'tcp://h:1', sessionBudget: 1, maxPipes: 2, pipeWindow: 3, progressTimeoutMs: 4,
      maxSeparatePoolInvocations: 5, invocationQueueWarningThresholdMs: 6,
    }, node);
    expect(parsed.config).toMatchObject({ sessionBudget: 1, maxPipes: 2, pipeWindow: 3, progressTimeout: 4, maxSeparatePoolInvocations: 5,
      invocationQueueWarningThreshold: 6 });
  });

  it('puts the query token on the upgrade transport', () => {
    const ws = parseSpec({ transport: 'WebSocket', address: 'ws://h/ws', queryToken: { name: 'access_token', value: 't' } }, browser);
    expect(ws.config.webSocket.queryToken).toEqual({ name: 'access_token', value: 't' });
    const http = parseSpec({ transport: 'HttpSocket', address: 'http://h/h', queryToken: { name: 'a', value: 'b' } }, node);
    expect(http.config.httpSocket.queryToken).toEqual({ name: 'a', value: 'b' });
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', queryToken: { name: 'a', value: 'b' } }, node), 'NotSupported');
    expectKind(() => parseSpec({ transport: 'WebSocket', address: 'ws://h/ws', queryToken: { value: 'b' } }, node), 'BadRequest');
  });

  it('refuses insecureSkipVerify in a browser', () => {
    expectKind(() => parseSpec({ transport: 'WebSocketTls', address: 'wss://h/ws', insecureSkipVerify: true }, browser), 'NotSupported');
    expect(parseSpec({ transport: 'WebSocketTls', address: 'wss://h/ws', insecureSkipVerify: true }, node).config.webSocket.tls)
      .toEqual({ rejectUnauthorized: false });
  });

  it('scripts the authenticate callback', async () => {
    const signal = new AbortController().signal;
    const numbered = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authenticate: { token: 't', appendCallNumber: true, delayMs: 1 } }, node);
    const decode = (/** @type {Uint8Array | null} */ bytes) => new TextDecoder().decode(/** @type {Uint8Array} */ (bytes));
    expect(decode(await numbered.config.authenticate({ signal }))).toBe('t1');
    expect(decode(await numbered.config.authenticate({ signal }))).toBe('t2');
    const none = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authenticate: {} }, node);
    expect(await none.config.authenticate({ signal })).toBeNull();
    const throws = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authenticate: { throwMessage: 'no' } }, node);
    await expect(throws.config.authenticate({ signal })).rejects.toThrow('no');
    const hang = parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authenticate: { hang: true } }, node);
    const controller = new AbortController();
    const pending = hang.config.authenticate({ signal: controller.signal });
    controller.abort(new Error('timed out'));
    await expect(pending).rejects.toThrow('timed out');
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authToken: 'x', authenticate: {} }, node), 'BadRequest');
    expectKind(() => parseSpec({ transport: 'Tcp', address: 'tcp://h:1', authenticate: { other: 1 } }, node), 'NotSupported');
  });

  it('validates pool options', () => {
    expect(parsePoolOptions({})).toEqual({ maxConnections: 10, maxIdleTime: 120000, minIdleConnections: 1 });
    expect(parsePoolOptions({ maxConnections: 2, maxIdleTimeMs: 0, minIdleConnections: 5 })).toEqual({ maxConnections: 2, maxIdleTime: 0, minIdleConnections: 5 });
    expectKind(() => parsePoolOptions({ maxConnections: 0 }), 'BadRequest');
    expectKind(() => parsePoolOptions({ maxIdleTimeMs: -1 }), 'BadRequest');
    expectKind(() => parsePoolOptions({ other: 1 }), 'NotSupported');
  });
});
