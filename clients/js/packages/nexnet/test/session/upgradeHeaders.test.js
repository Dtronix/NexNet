import { describe, expect, it } from 'vitest';
import { resolveClientOptions } from '../../src/session/options.js';

// Upgrade-header validation (J43), mirroring C# UpgradeHeaderCollectionTests and Java ClientConfig.Builder.header.
const supported = { webSocket: () => null, httpSocket: () => null };
const urls = { webSocket: 'ws://127.0.0.1:1/', httpSocket: 'http://127.0.0.1:1/' };

describe.each(/** @type {const} */ (['webSocket', 'httpSocket']))('%s.headers', (kind) => {
  /** @param {unknown} headers */
  const resolve = (headers) => resolveClientOptions(/** @type {any} */ ({ [kind]: { url: urls[kind], headers } }), supported);

  it('accepts no headers', () => {
    expect(resolve(undefined).kind).toBe(kind);
    expect(resolve(null).kind).toBe(kind);
    expect(resolve({}).kind).toBe(kind);
  });

  it.each([
    ['X-Tenant', 'acme'],
    ['Authorization', 'Bearer abc'],
    ["x!#$%&'*+-.^_`|~09", 'v'],
    ['X-Tab', 'a\tb'],
    ['X-Latin1', 'caf\xe9'],
    ['X-Empty', ''],
  ])('accepts %j', (name, value) => {
    expect(resolve({ [name]: value }).kind).toBe(kind);
  });

  it.each(['Host', 'connection', 'UPGRADE', 'Sec-WebSocket-Key', 'sec-websocket-protocol', 'Content-Length', 'Transfer-Encoding'])(
    'refuses the transport header %s', (name) => {
      expect(() => resolve({ [name]: 'x' })).toThrow(TypeError);
      expect(() => resolve({ [name]: 'x' })).toThrow(/set by the transport/);
    });

  it.each(['', 'X Space', 'X:Colon', 'X\r\nInjected', 'X-\xe9', '(X)'])('refuses the name %j', (name) => {
    expect(() => resolve({ [name]: 'x' })).toThrow(TypeError);
  });

  it.each(['a\rb', 'a\nb', 'a\r\nX-Injected: 1', 'a\x00b', 'a\x7fb', String.fromCharCode(0x100), String.fromCharCode(0x20ac)])(
    'refuses the value %j', (value) => {
      expect(() => resolve({ 'X-Test': value })).toThrow(TypeError);
      expect(() => resolve({ 'X-Test': value })).toThrow(/control or non-Latin-1/);
    });

  it('refuses a value that is not a string', () => {
    expect(() => resolve({ 'X-Test': 1 })).toThrow(TypeError);
    expect(() => resolve({ 'X-Test': null })).toThrow(TypeError);
  });

  it('refuses headers that are not an object of name to value', () => {
    expect(() => resolve('X-Test: 1')).toThrow(TypeError);
    expect(() => resolve([['X-Test', '1']])).toThrow(TypeError);
  });

  it('refuses a name set twice ignoring case', () => {
    expect(() => resolve({ 'X-One': '1', 'x-one': '2' })).toThrow(/twice/);
  });
});
